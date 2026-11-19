// 화면 점검용: 헤드리스 Edge 를 CDP 로 조종해 클릭하고 스크린샷을 남긴다(외부 패키지 없음, Node 22+).
//   node tools/ui-check.mjs [url] [출력폴더]
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync, mkdtempSync, rmSync, readdirSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const url = process.argv[2] ?? 'http://127.0.0.1:5057/';
const outDir = process.argv[3] ?? 'data/ui-check';
mkdirSync(outDir, { recursive: true });

const browsers = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
];
const port = 9300 + Math.floor(Math.random() * 600);
const profile = mkdtempSync(join(tmpdir(), 'rt-ui-'));
const proc = spawn(browsers[0], [
  '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  '--window-size=1600,1000', '--force-device-scale-factor=1', 'about:blank',
], { stdio: 'ignore' });

const sleep = ms => new Promise(r => setTimeout(r, ms));
let target;
for (let i = 0; i < 50 && !target; i++) {
  await sleep(200);
  try {
    const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
    target = list.find(t => t.type === 'page');
  } catch { /* 아직 안 뜸 */ }
}
if (!target) throw new Error('브라우저 연결 실패');

const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener('open', r, { once: true }));
let id = 0;
const pending = new Map();
const consoleErrors = [];
ws.addEventListener('message', ev => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  if (msg.method === 'Runtime.exceptionThrown') consoleErrors.push(msg.params.exceptionDetails.text);
  if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error')
    consoleErrors.push(msg.params.args.map(a => a.value ?? a.description).join(' '));
});
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async expr => (await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
const shot = async name => {
  const r = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(join(outDir, name + '.png'), Buffer.from(r.result.data, 'base64'));
  console.log('screenshot', name);
};
const waitFor = async (expr, ms = 15000) => {
  for (let t = 0; t < ms; t += 200) { if (await evaluate(expr)) return true; await sleep(200); }
  throw new Error('대기 실패: ' + expr);
};
const click = async (selector, text) => {
  const ok = await evaluate(`(() => {
    const els = [...document.querySelectorAll(${JSON.stringify(selector)})];
    const el = ${text ? `els.find(e => e.textContent.includes(${JSON.stringify(text)}))` : 'els[0]'};
    if (!el) return false; el.click(); return true; })()`);
  if (!ok) throw new Error(`클릭 대상 없음: ${selector} ${text ?? ''}`);
  await sleep(400);
};

await send('Runtime.enable');
await send('Page.enable');
await send('Emulation.setFocusEmulationEnabled', { enabled: true });   // 헤드리스는 포커스가 없어 클립보드가 막힌다
await send('Browser.grantPermissions', { origin: new URL(url).origin, permissions: ['clipboardReadWrite', 'clipboardSanitizedWrite'] });
await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
await send('Page.navigate', { url });
// 대화형 연결(SignalR)이 붙을 때까지
await waitFor(`!!document.querySelector('.entries .entry')`);
await sleep(1500);
await shot('1-home');

await click('.entries .entry', 'C2ZMove');
await waitFor(`!!document.querySelector('.tree .row')`);
await shot('2-entry-C2ZMove');

// 트리에서 Zone::Fanout 을 누르면 스택에 이어 붙는다
await click('.tree .row', 'Fanout');
await waitFor(`document.querySelectorAll('.frame').length >= 2`);
await shot('3-tree-click-Fanout');

// 코드 줄 클릭으로 한 단계 더(Fanout 안의 PushMsg)
const pushed = await evaluate(`(() => {
  const frames = [...document.querySelectorAll('.frame')];
  const last = frames[frames.length - 1];
  const line = [...last.querySelectorAll('.line.call')].find(l => l.textContent.includes('PushMsg'));
  if (!line) return false; line.click(); return true; })()`);
console.log('code line click', pushed);
await sleep(600);
const chip = await evaluate(`(() => { const c = [...document.querySelectorAll('.callees .chip')].find(c => !c.disabled && c.textContent.includes('PushMsg')); if (c) { c.click(); return true; } return false; })()`);
console.log('chip click', chip);
await sleep(600);
const frames = await evaluate(`[...document.querySelectorAll('.frame .fname')].map(e => e.textContent)`);
console.log('frames', frames);
await shot('4-code-line-click');

// 신선도 배지와 변경 영향: 마지막 칸(PushMsg)에서 "영향" → 영향받는 진입점 목록 → 하나를 누르면 그 경로가 스택으로
console.log('freshness', await evaluate(`document.querySelector('.fresh')?.textContent.trim()`));
await evaluate(`(() => { const f = [...document.querySelectorAll('.frame')].pop(); [...f.querySelectorAll('button')].find(b => b.textContent.includes('영향')).click(); })()`);
await waitFor(`!!document.querySelector('.impact')`);
console.log('impact', await evaluate(`document.querySelector('.impact .frame-head').textContent.replace(/\\s+/g, ' ').trim()`));
await shot('4b-impact');
await evaluate(`[...document.querySelectorAll('.impact-list button')].find(b => b.textContent.includes('C2ZChat'))?.click()`);
await sleep(600);
console.log('frames after impact', await evaluate(`[...document.querySelectorAll('.frame .fname')].map(e => e.textContent)`));
await evaluate(`[...document.querySelectorAll('.impact button')].find(b => b.textContent.includes('닫기')).click()`);
await sleep(300);

// 전후 비교 패널
await click('.meta button', '비교');
await waitFor(`!!document.querySelector('.compare')`);
console.log('compare', await evaluate(`document.querySelector('.compare').textContent.replace(/\\s+/g, ' ').trim().slice(0, 160)`));
await shot('4c-compare');
await click('.compare button', '닫기');

// 문맥 복사 · AI 에게 질문 (실제 전송은 UI_ASK=1 일 때만: 비용이 들고 답이 매번 다르다)
await click('.flow-tools button', '문맥 복사');
await waitFor(`!!document.querySelector('.flow-tools .flow-note')`);
console.log('copy', await evaluate(`document.querySelector('.flow-tools .flow-note').textContent.trim()`));
console.log('clipboard', await evaluate(`navigator.clipboard.readText().then(t => t.split('\\n').slice(0, 6).join(' / ')).catch(e => 'read 실패 ' + e)`));
await click('.flow-tools button', 'AI 에게 질문');
await waitFor(`!!document.querySelector('.ask')`);
console.log('ask', await evaluate(`[...document.querySelectorAll('.ask select option')].map(o => o.textContent).join(',') || document.querySelector('.ask .note')?.textContent`));
await click('.ask .preset', '설명');
await sleep(300);
await shot('4d-ask');
if (process.env.UI_ASK === '1') {
  if (process.env.UI_ASK_AI) {   // claude | codex
    await evaluate(`(() => { const s = document.querySelector('.ask select'); s.value = ${JSON.stringify(process.env.UI_ASK_AI)}; s.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await sleep(300);
  }
  await click('.ask button.primary', '보내기');
  await waitFor(`!!document.querySelector('.ask button.primary') && !!document.querySelector('.answer-text')?.textContent.trim()`, 300000);
  console.log('answer', await evaluate(`document.querySelector('.answer-text').textContent.trim().slice(0, 300)`));
  console.log('answer error', await evaluate(`document.querySelector('.answer .note')?.textContent ?? null`));
  await shot('4e-answer');
}
await click('.flow-tools button', 'AI 에게 질문');

// 노드 보기: 아이콘으로 전환 → 그래프의 Fanout 노드를 누르면 코드 스택이 진입점→Fanout 으로 바뀐다
await evaluate(`document.querySelector('.views button[title="노드 보기"]').click()`);
await waitFor(`!!document.getElementById('graph-canvas')?._cy`);
await sleep(800);
await shot('6-graph');
const tapped = await evaluate(`(() => {
  const cy = document.getElementById('graph-canvas')._cy;
  const n = cy.nodes().filter(n => n.data('label') === 'Zone::Fanout')[0];
  if (!n) return false; n.emit('tap'); return true; })()`);
console.log('graph node tap', tapped);
await sleep(800);
console.log('frames after tap', await evaluate(`[...document.querySelectorAll('.frame .fname')].map(e => e.textContent)`));
console.log('graph nodes', await evaluate(`document.getElementById('graph-canvas')._cy.nodes().length`),
  'highlighted', await evaluate(`document.getElementById('graph-canvas')._cy.$('.onpath, .current').length`));
await shot('7-graph-tap');

await evaluate(`document.querySelector('.views button[title^="노드 크게"]').click()`);
await waitFor(`document.querySelector('.panes.mode-wide') && !!document.getElementById('graph-canvas')?._cy`);
await sleep(800);
await shot('8-wide');

// 대상 추가: 폴더 목록 → 경로 입력 → 설정 제안(저장은 하지 않고 취소)
await click('button.icon-btn', '대상 추가');
await waitFor(`!!document.querySelector('.setup .folders')`);
await shot('9-add-target-drives');
// 설정 제안을 시험할 폴더: 환경 변수, 없으면 targets/ 첫 설정의 root
const firstTarget = readdirSync('targets').find(f => f.endsWith('.json'));
const setupRoot = (process.env.UI_CHECK_TARGET_ROOT
  ?? JSON.parse(readFileSync(join('targets', firstTarget), 'utf8')).root).replaceAll('\\', '\\\\');
await evaluate(`(() => { const i = document.querySelector('.setup input.path'); i.value = '${setupRoot}';
  i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
await sleep(400);
await click('.setup button.primary', '설정 만들기');
await waitFor(`!!document.querySelector('.setup .presets')`, 60000);
console.log('presets', await evaluate(`[...document.querySelectorAll('.setup .preset')].map(p => p.querySelector('b').textContent + ' ' + p.querySelector('.count').textContent + (p.querySelector('input').checked ? ' [x]' : ''))`));
await shot('10-add-target-suggest');
await click('.setup button.link', '취소');
await waitFor(`!document.querySelector('.setup')`);

await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
await sleep(300);
await shot('5-dark');

console.log('console errors', consoleErrors);
// Browser.close 로 정상 종료를 먼저 부탁하고, 남은 것은 이 실행의 프로필 경로로만 찾아 끈다
// (msedge.exe 는 실행기 프로세스가 곧 끝나서 PID 트리 종료로는 자식이 남는다. 사용자 Edge 는 건드리지 않는다).
try { await Promise.race([send('Browser.close'), sleep(2000)]); } catch { }
ws.close();
await sleep(800);
proc.kill();
if (process.platform === 'win32') {
  const name = profile.split(/[\\/]/).pop();
  spawnSync('powershell', ['-NoProfile', '-Command',
    `Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like '*${name}*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }`],
    { stdio: 'ignore' });
}
await sleep(500);
try { rmSync(profile, { recursive: true, force: true }); } catch { }
