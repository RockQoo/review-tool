// 사용법 화면 만들기: 헤드리스 Edge 로 화면을 찍고, 버튼 위치에 번호를 달아 docs/usage/index.html 을 만든다(외부 패키지 없음, Node 22+).
//   서버가 떠 있을 때: node tools/usage-shots.mjs [url] [진입점 이름] [트리에서 누를 함수] [코드에서 열 줄 글자] [함수 검색어]
// 사용법은 docs/usage/ 에 모은다(index.html 스크린샷, detail.html 글, img/). 스크린샷에는 대상 소스가 보이므로 img/ 는 커밋하기 전에 확인한다.
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const url = process.argv[2] ?? 'http://127.0.0.1:5057/';
const ENTRY = process.argv[3] ?? 'C2ZMove';
const FN = process.argv[4] ?? 'Fanout';
const LINE = process.argv[5] ?? 'RequestMove';
const SEARCH = process.argv[6] ?? 'RequestMove';
const imgDir = 'docs/usage/img';
mkdirSync(imgDir, { recursive: true });
const W = 1600, H = 1000;

const port = 9300 + Math.floor(Math.random() * 600);
const profile = mkdtempSync(join(tmpdir(), 'rt-usage-'));
const proc = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
  '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  `--window-size=${W},${H}`, '--force-device-scale-factor=1', 'about:blank',
], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let target;
for (let i = 0; i < 50 && !target; i++) {
  await sleep(200);
  try { target = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find(t => t.type === 'page'); } catch { }
}
if (!target) throw new Error('브라우저 연결 실패');
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener('open', r, { once: true }));
let id = 0;
const pending = new Map();
ws.addEventListener('message', ev => { const m = JSON.parse(ev.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } });
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async expr => (await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
const waitFor = async (expr, ms = 15000) => { for (let t = 0; t < ms; t += 200) { if (await evaluate(expr)) return; await sleep(200); } throw new Error('대기 실패: ' + expr); };
const find = (sel, text) => `[...document.querySelectorAll(${JSON.stringify(sel)})].find(e => ${text ? `e.textContent.includes(${JSON.stringify(text)})` : 'true'})`;
const click = async (sel, text) => { if (!await evaluate(`(() => { const e = ${find(sel, text)}; if (!e) return false; e.click(); return true; })()`)) throw new Error(`클릭 대상 없음: ${sel} ${text ?? ''}`); await sleep(500); };
const view = async title => { await evaluate(`document.querySelector('.views button[title^="${title}"]').click()`); await sleep(900); };

// 표시할 곳의 화면 좌표. { sel, text } 또는 { cy: 캔버스 id, label }
async function rectOf(m) {
  if (m.cy) {
    return evaluate(`(() => { const el = document.getElementById(${JSON.stringify(m.cy)}); const cy = el?._cy; if (!cy) return null;
      const n = cy.nodes().filter(n => (n.data('label') ?? '').includes(${JSON.stringify(m.label)}))[0]; if (!n) return null;
      const b = n.renderedBoundingBox(), o = el.getBoundingClientRect(); return { x: o.left + b.x1, y: o.top + b.y1, w: b.w, h: b.h }; })()`);
  }
  return evaluate(`(() => { const e = ${find(m.sel, m.text)}; if (!e) return null; const r = e.getBoundingClientRect();
    return r.width ? { x: r.left, y: r.top, w: r.width, h: r.height } : null; })()`);
}

const scenes = [];
async function scene(key, title, intro, marks, clipSpec) {
  let clip = { x: 0, y: 0, width: W, height: H };
  if (clipSpec) {
    const r = await rectOf(clipSpec);
    const pad = clipSpec.pad ?? 8;
    clip = { x: Math.max(0, r.x - pad), y: Math.max(0, r.y - pad), width: Math.min(W, r.w + pad * 2), height: Math.min(H, r.h + pad * 2) };
  }
  const placed = [];
  for (const [i, m] of marks.entries()) {
    const r = await rectOf(m);
    if (!r) { console.warn(`  표시 못 함: ${key} #${i + 1} ${m.sel ?? m.label} ${m.text ?? ''}`); continue; }
    placed.push({ n: i + 1, name: m.name, desc: m.desc, x: r.x - clip.x, y: r.y - clip.y, w: r.w, h: r.h });
  }
  const shot = await send('Page.captureScreenshot', { format: 'png', clip: { ...clip, scale: 1 } });
  writeFileSync(join(imgDir, key + '.png'), Buffer.from(shot.result.data, 'base64'));
  scenes.push({ key, title, intro, w: clip.width, h: clip.height, zoom: clipSpec?.zoom ?? 1, marks: placed, all: marks });
  console.log('scene', key, `${placed.length}/${marks.length}`);
}

await send('Runtime.enable');
await send('Page.enable');
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 1, mobile: false });
await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
await send('Page.navigate', { url });
await waitFor(`!!document.querySelector('.entries .entry')`);
await sleep(1500);

// ---- 기본 화면: 진입점 → 트리에서 함수 → 코드 줄 열기 ----
await click('.entries .entry', ENTRY);
await waitFor(`!!document.querySelector('.tree .row')`);
await click('.tree .row', FN);
await waitFor(`document.querySelectorAll('.frame').length >= 2`);
await evaluate(`${find('#frame-0 .line.call', LINE)}?.click()`);
await sleep(500);
await scene('main', '기본 화면', '진입점 고르기 → 트리에서 함수 → 오른쪽에 코드가 이어 붙음', [
  { sel: 'header button.brand', name: '홈', desc: '대상 추가·비교·보고서 창 닫고 기본 화면으로' },
  { sel: '.nav-btns', name: '뒤로 / 앞으로', desc: '보던 진입점·경로로 이동 · Alt+←/→ · 새로 고침해도 마지막 위치 복원' },
  { sel: 'header.topbar > select', name: '대상', desc: '분석할 서버 고르기' },
  { sel: 'header button', text: '대상 추가', name: '대상 추가', desc: '새 폴더 등록 · 설정 자동 생성' },
  { sel: 'header button', text: '다시 분석', name: '다시 분석', desc: '고른 대상의 소스를 다시 읽어 호출 트리 새로 만들기 · 코드는 안 고침' },
  { sel: '.editor-pick', name: '코드 열기', desc: '"열기"·줄 번호를 누르면 띄울 프로그램 (이 도구는 편집 안 함)' },
  { sel: '.views', name: '보기 전환', desc: '보기 8가지 (아래 표)' },
  { sel: '.fresh', name: '신선도', desc: '분석 뒤 코드 바뀜 여부 · 30초마다' },
  { sel: '.meta button', text: '비교', name: '비교', desc: '수정 전후 구조 비교' },
  { sel: '.meta button', text: '보고서', name: '보고서', desc: '공유용 Markdown' },
  { sel: '.meta button.warn', name: '못 본 것', desc: '분석이 놓친 부분' },
  { sel: '.entries .search', name: '검색', desc: '진입점·함수 이름으로 찾기' },
  { sel: '.entries .kind-filter', name: '종류 필터', desc: '패킷·메시지·시작·테스트·함수 · 여러 개 켜기 · 다 끄면 전체' },
  { sel: '.entries .group.folder', name: '폴더', desc: '프로젝트(프로세스) 묶음 · 눌러 접기/펼치기' },
  { sel: '.entries .group.cls', name: '클래스', desc: '클래스(테스트 묶음) · 눌러 접기/펼치기' },
  { sel: '.entries .entry.active', name: '진입점', desc: '패킷 핸들러 · 레인 메시지 · main · 테스트' },
  { sel: '.tree-tools label', name: '외부 호출', desc: 'std 등 범위 밖 함수 표시' },
  { sel: '.tree-tools button', text: '모두 펼치기', name: '모두 펼치기 / 접기', desc: '트리 전체 열고 닫기' },
  { sel: '.tree .row', text: FN, name: '트리의 함수', desc: '누르면 진입점→여기 코드' },
  { sel: '.tree .line-group', name: '같은 줄 묶음', desc: '한 문장에서 이어 부른 호출 (N줄)' },
  { sel: '.splitter.v', name: '경계선', desc: '끌어서 칸 크기 · 두 번 누르면 기본' },
  { sel: '.flow-tools button', text: '문맥 복사', name: '문맥 복사', desc: '흐름 전체를 Markdown 으로' },
  { sel: '.flow-tools label.check', name: '코드 포함', desc: '끄면 구조(이름·파일:줄)만' },
  { sel: '.flow-tools button', text: 'AI 에게 질문', name: 'AI 에게 질문', desc: '이 흐름으로 Claude / Codex 질문' },
  { sel: '#frame-0 .fname', name: '함수 칸', desc: '번호 = 진입점부터 순서' },
  { sel: '#frame-0 button', text: '영향', name: '영향', desc: '바꾸면 영향받는 패킷' },
  { sel: '#frame-0 button', text: '열기', name: '열기', desc: '편집기에서 이 함수' },
  { sel: '#frame-0 button', text: '아래 닫기', name: '아래 닫기', desc: '이 칸 아래 지우기' },
  { sel: '#frame-0 .line.open .ln', name: '줄 번호', desc: '편집기에서 이 줄' },
  { sel: '#frame-0 .line.open', name: '호출 줄', desc: '누르면 부르는 함수 목록' },
  { sel: '#frame-0 .callees', name: '호출 단추', desc: '누르면 한 단계 더 · 파랑 = 따라 들어간 함수' },
  { sel: '#frame-0 .line.next', name: '다음 칸 줄', desc: '파랑 ▶ = 아래 칸으로 이어진 호출' },
  { sel: '#frame-1 .from', name: '부른 곳', desc: '위 칸의 몇 번째 줄에서 불림' },
  { sel: '#frame-0 .tk-keyword', name: '문법 색칠', desc: '키워드·문자열·주석·숫자 색' },
  { sel: '#frame-0 .review-row', name: '검토 표시', desc: '✓ 확인 · 보류 · 메모 → 진입점 옆 진행률, 본문이 바뀌면 "다시 봐야 함"' },
]);

// ---- 함수 검색: 이름으로 찾아 진입점부터 그 함수까지 ----
await evaluate(`(() => { const i = document.querySelector('.entries .search'); i.value = ${JSON.stringify(SEARCH)}; i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
await sleep(600);
await evaluate(`[...document.querySelectorAll('.entries .fn-result')][0]?.click()`);
await sleep(800);
await scene('search', '함수 검색', '왼쪽 검색칸에 함수 이름(두 글자 이상)', [
  { sel: '.entries .search', name: '검색칸', desc: '진입점과 함수 이름을 같이 찾음' },
  { sel: '.entries .fn-result', name: '함수 결과', desc: '누르면 진입점부터 이 함수까지 코드 스택' },
  { sel: '.impact', name: '닿는 진입점', desc: '이 함수에 닿는 패킷 목록 · 다른 경로 고르기' },
  { sel: '.pane.stack .frame:last-of-type .fname', name: '찾은 함수', desc: '코드 스택 맨 아래' },
]);
await evaluate(`(() => { document.querySelector('.impact button.close')?.click(); const i = document.querySelector('.entries .search'); i.value = ''; i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
await sleep(400);
await click('.entries .entry', ENTRY);
await click('.tree .row', FN);
await waitFor(`document.querySelectorAll('.frame').length >= 2`);

await scene('views', '보기 전환 아이콘', '상단 아이콘 8개', [
  { sel: '.views button[title="트리 보기"]', name: '트리', desc: '호출을 들여쓰기로' },
  { sel: '.views button[title="노드 보기"]', name: '노드', desc: '호출을 그래프로' },
  { sel: '.views button[title^="노드 크게"]', name: '노드 크게', desc: '그래프 위 · 코드 아래' },
  { sel: '.views button[title^="패킷 여정"]', name: '패킷 여정', desc: '보낸 곳 → 여기 → 나가는 곳' },
  { sel: '.views button[title^="흐름 요약"]', name: '흐름 요약', desc: 'DB·캐시·스레드 호출만 순서대로' },
  { sel: '.views button[title^="전체 흐름도"]', name: '전체 흐름도', desc: '프로세스 × 클래스 지도' },
  { sel: '.views button[title^="클래스 다이어그램"]', name: '클래스', desc: '상속 · 멤버' },
  { sel: '.views button[title^="점검 규칙"]', name: '점검 규칙', desc: '락 안 I/O 등 규칙 위반' },
], { sel: '.views', pad: 6, zoom: 2 });

// ---- 패널: 영향 · 비교 · AI 질문 · 보고서 · 신선도 · 못 본 것 ----
await click('#frame-1 button', '영향');
await waitFor(`!!document.querySelector('.impact')`);
await scene('impact', '영향', '함수 칸의 "영향"', [
  { sel: '.impact .frame-head', name: '대상', desc: '이 함수를 바꾸면 · 진입점 수' },
  { sel: '.impact-list button', name: '영향받는 진입점', desc: '누르면 그 경로를 코드 스택으로' },
  { sel: '.impact-list .path', name: '경로', desc: '진입점 → … → 이 함수' },
  { sel: '.impact details.limits', name: '한계', desc: '이 결과가 증명하지 않는 것' },
]);
await click('.impact button', '닫기');

await click('.meta button', '비교');
await waitFor(`!!document.querySelector('.compare')`);
await evaluate(`[...document.querySelectorAll('.git-changes button')].find(b => b.textContent.includes('git 변경'))?.click()`);
await waitFor(`!document.querySelector('.git-changes button')?.disabled && (document.querySelector('.git-changes')?.textContent.includes('바뀐 곳') || !!document.querySelector('.git-changes .warn-line'))`, 30000);
await scene('compare', '비교', '상단 "비교"', [
  { sel: '.compare button', text: '기준으로 저장', name: '기준 저장', desc: 'AI 수정 전에 누르기' },
  { sel: '.compare select', name: '기준 고르기', desc: '직전 분석 · 저장한 기준' },
  { sel: '.compare', name: '비교 결과', desc: '진입점·함수·호출 추가/삭제/변경 · 바뀐 함수는 코드 칸에 "바뀐 줄 보기"(빨강/초록) · 지워진 함수는 기준 코드로' },
  { sel: '.git-changes', name: 'git 변경', desc: '작업 폴더 vs 마지막 커밋: 바뀐 줄이 걸린 함수 · 어떤 흐름에도 안 걸린 변경' },
  { sel: '.compare button', text: '닫기', name: '닫기', desc: '' },
]);
await click('.compare button', '닫기');

await click('.flow-tools button', 'AI 에게 질문');
await waitFor(`!!document.querySelector('.ask')`);
await click('.ask .preset', '설명');
await scene('ask', 'AI 에게 질문', '코드 스택 위 "AI 에게 질문"', [
  { sel: '.ask select', name: 'AI', desc: 'Claude Code / Codex' },
  { sel: '.ask .preset', name: '자주 묻는 질문', desc: '실패 처리 · 스레드 · 입력 검증 · 설명' },
  { sel: '.ask .ask-input', name: '질문', desc: '직접 쓰기' },
  { sel: '.ask button.primary', name: '보내기', desc: '누를 때만 전송 · AI 는 읽기만' },
]);
await click('.flow-tools button', 'AI 에게 질문');

await click('.meta button', '보고서');
await waitFor(`!!document.querySelector('.report')`);
await click('.report .compare-bar button.link', '만들기');
await waitFor(`!!document.querySelector('.report-text')`);
await scene('report', '보고서', '상단 "보고서"', [
  { sel: '.report .compare-bar label', text: '고른 진입점', name: '범위', desc: '고른 진입점 / 전체' },
  { sel: '.report .compare-bar label', text: '호출 트리', name: '넣을 항목', desc: '트리 · 여정 · 요약 · 점검' },
  { sel: '.report .compare-bar label', text: '소스 본문', name: '소스 본문', desc: '기본 뺌 (회사 코드)' },
  { sel: '.report .compare-bar label', text: '절대 경로', name: '절대 경로', desc: '기본 뺌 (내 PC 경로)' },
  { sel: '.report .compare-bar button', text: '만들기', name: '만들기', desc: '' },
  { sel: '.report-actions', name: '복사 / 내려받기', desc: '클립보드 · .md 파일' },
]);
await click('.report .compare-bar button.close');

await click('.fresh');
await sleep(400);
await scene('fresh', '신선도', '배지(● 최신 / ● 바뀜 N / ● 확인 불가)를 누르면', [
  { sel: '.fresh', name: '배지', desc: '최신 · 바뀜 N · 확인 불가' },
  { sel: 'section.diagnostics:not(.compare)', name: '내용', desc: '바뀐 파일 목록 → 다시 분석' },
]);
await click('.fresh');

await click('.meta button.warn');
await sleep(400);
await scene('diagnostics', '못 본 것', '상단 "못 본 것 N"', [
  { sel: '.meta button.warn', name: '못 본 것', desc: '분석이 놓친 개수' },
  { sel: 'section.diagnostics:not(.compare)', name: '목록', desc: '못 찾은 정의 · 상한 · 해석 실패' },
]);
await click('.meta button.warn');

// ---- 보기 ----
await view('노드 보기');
await waitFor(`!!document.getElementById('graph-canvas')?._cy`);
await evaluate(`(() => { const cy = document.getElementById('graph-canvas')._cy; cy.nodes().filter(n => (n.data('label') ?? '').includes(${JSON.stringify(FN)}))[0]?.emit('tap'); })()`);
await sleep(900);
await scene('graph', '노드 보기', '같은 호출을 그래프로 · 끌기 / 휠 확대', [
  { sel: '.tree-tools label', text: '깊이', name: '깊이', desc: '몇 단계까지 그릴지' },
  { sel: '.tree-tools label', text: '외부 호출', name: '외부 호출', desc: '범위 밖 함수 표시' },
  { sel: '.tree-tools button', text: '화면 맞춤', name: '화면 맞춤', desc: '처음 배치로 되돌리기' },
  { cy: 'graph-canvas', label: 'HandleMove', name: '진입점', desc: '갈색 = 시작' },
  { cy: 'graph-canvas', label: '줄', name: '같은 줄 묶음', desc: '누르면 그 줄 선택 · 선 하나' },
  { cy: 'graph-canvas', label: FN, name: '고른 함수', desc: '파랑 = 고른 것과 경로' },
  { cy: 'graph-canvas', label: 'OnFanout', name: '추정 연결', desc: '주황 점선 = 메시지·등록 (추정)' },
  { sel: '.graph-legend', name: '범례', desc: '선 숫자 = 호출 줄 · 끌면 아래 호출도 같이' },
]);

await view('패킷 여정');
await waitFor(`!!document.querySelector('.journey')`);
await scene('journey', '패킷 여정', '프로세스를 넘는 흐름', [
  { sel: '.view-title', name: '진입점', desc: '지금 보는 패킷' },
  { sel: '.journey .chain', name: '오기까지', desc: '보낸 프로세스·함수·줄 → 여기' },
  { sel: '.journey .j-station', name: '정류장', desc: '누르면 그 코드' },
  { sel: '.journey .j-tree', name: '나가는 곳', desc: '여기서 보낸 패킷 → 받는 핸들러' },
]);

await view('흐름 요약');
await waitFor(`!!document.querySelector('.summary')`);
await scene('summary', '흐름 요약', '표시 붙은 호출만 소스 순서대로', [
  { sel: '.summary .tags', name: '표시 거르기', desc: '설정·스레드·네트워크·DB·캐시…' },
  { sel: '.summary-list .summary-row', name: '항목', desc: '누르면 그 줄까지 코드 스택' },
]);

await view('전체 흐름도');
await waitFor(`!!document.getElementById('map-canvas')?._cy`);
await sleep(600);
await evaluate(`(() => { const cy = document.getElementById('map-canvas')._cy; cy.nodes('.on').filter(n => !n.hasClass('process'))[0]?.emit('tap'); })()`);
await sleep(700);
await scene('map', '전체 흐름도', '프로세스 × 클래스', [
  { sel: '.pane.tree .tree-tools', name: '도구', desc: '고른 흐름만 / 전체 · 화면 맞춤' },
  { sel: '#map-canvas', name: '지도', desc: '상자 = 클래스 · 묶음 = 프로세스 · 밝은 것 = 고른 흐름' },
  { sel: '.map-info', name: '클래스 정보', desc: '호출 수 · 누르면 코드' },
]);

await view('클래스 다이어그램');
await waitFor(`!!document.getElementById('map-canvas')?._cy && document.getElementById('map-canvas')._cy.nodes('.uml').length > 0`);
await sleep(700);
await scene('classes', '클래스 다이어그램', '흐름에 나온 클래스', [
  { sel: '.pane.tree .tree-tools', name: '도구', desc: '고른 흐름만 / 전체' },
  { sel: '#map-canvas', name: '다이어그램', desc: '부모(상속) · 멤버' },
]);

await view('점검 규칙');
await waitFor(`!!document.querySelector('.rule-table')`);
await scene('checks', '점검 규칙', '설정한 규칙 위반 찾기', [
  { sel: '.checks .tags', name: '확실도', desc: '높음 · 보통 · 낮음 거르기' },
  { sel: '.checks label', text: '고른 진입점만', name: '고른 진입점만', desc: '이 패킷 경로의 지적만' },
  { sel: '.checks button', text: '다시 점검', name: '다시 점검', desc: '규칙 고친 뒤 · 다시 분석 불필요' },
  { sel: '.rule-table', name: '규칙 표', desc: '돌았음 · 꺼짐 · 오류 · 지적 수' },
  { sel: '.checks .finding-list, .checks .hint-line, .checks .note', name: '지적', desc: '누르면 경로 + 근거 줄 표시 (지금은 0건)' },
]);
await view('트리 보기');

// ---- 대상 추가 ----
await click('header button', '대상 추가');
await waitFor(`!!document.querySelector('.setup .folders')`);
await scene('setup', '대상 추가', '새 서버 폴더 등록', [
  { sel: '.empty .back', name: '돌아가기', desc: '원래 화면으로 (보기 아이콘·홈도 됨)' },
  { sel: '.setup input.path', name: '경로', desc: '직접 입력 · 이동' },
  { sel: '.setup .folders', name: '폴더 목록', desc: '눌러서 들어가기' },
  { sel: '.setup button.primary', name: '설정 만들기', desc: '이 폴더로 진입점 찾기' },
  { sel: '.setup button', text: '취소', name: '취소', desc: '원래 화면으로' },
]);
await click('.setup button.link', '취소');

// ---- HTML ----
const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const body = scenes.map(s => {
  const pct = (v, t) => (v / t * 100).toFixed(3) + '%';
  const boxes = s.marks.map(m => `<span class="box" style="left:${pct(m.x, s.w)};top:${pct(m.y, s.h)};width:${pct(m.w, s.w)};height:${pct(m.h, s.h)}"><i>${m.n}</i></span>`).join('');
  const rows = s.marks.map(m => `<li><i>${m.n}</i><b>${esc(m.name)}</b>${m.desc ? `<span>${esc(m.desc)}</span>` : ''}</li>`).join('');
  return `<section id="${s.key}"><h2>${esc(s.title)}</h2><p class="intro">${esc(s.intro)}</p>
<div class="shot" style="max-width:${s.w * s.zoom}px"><a href="img/${s.key}.png" target="_blank"><img src="img/${s.key}.png" width="${s.w}" height="${s.h}" alt="${esc(s.title)}"></a>${boxes}</div>
<ol class="keys">${rows}</ol></section>`;
}).join('\n');
const toc = scenes.map(s => `<a href="#${s.key}">${esc(s.title)}</a>`).join('');
writeFileSync('docs/usage/index.html', `<!doctype html>
<html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>review-tool 사용법</title>
<style>
:root { --bg:#f7f7f5; --panel:#fff; --text:#1d1f21; --muted:#5f6368; --line:#e2e2de; --mark:#e5383b; --accent:#2f6fd6; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#17181a; --panel:#1f2124; --text:#e6e6e3; --muted:#a0a4a8; --line:#33363a; --mark:#ff5a5f; --accent:#7aa7f0; } }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--text); font:15px/1.6 "Malgun Gothic", system-ui, sans-serif; }
main { max-width:1640px; margin:0 auto; padding:24px 16px 80px; }
h1 { margin:0 0 6px; font-size:26px; }
.sub { color:var(--muted); margin:0 0 14px; }
nav { display:flex; flex-wrap:wrap; gap:6px; position:sticky; top:0; background:var(--bg); padding:8px 0; z-index:5; border-bottom:1px solid var(--line); }
nav a { color:var(--accent); text-decoration:none; border:1px solid var(--line); border-radius:14px; padding:2px 10px; font-size:13px; background:var(--panel); }
h2 { margin:36px 0 2px; font-size:20px; }
.intro { color:var(--muted); margin:0 0 10px; }
.shot { position:relative; border:1px solid var(--line); border-radius:6px; overflow:hidden; }
.shot img { display:block; width:100%; height:auto; }
.box { position:absolute; border:2px solid var(--mark); border-radius:4px; pointer-events:none; }
.box i, .keys i { font-style:normal; font-weight:700; font-size:12px; color:#fff; background:var(--mark); border-radius:10px; min-width:20px; height:20px; line-height:20px; text-align:center; padding:0 5px; display:inline-block; }
.box i { position:absolute; left:-10px; top:-10px; box-shadow:0 0 0 2px #0006; }
.keys { list-style:none; padding:0; margin:12px 0 0; display:grid; grid-template-columns:repeat(auto-fill, minmax(330px, 1fr)); gap:6px 18px; }
.keys li { display:flex; gap:8px; align-items:baseline; background:var(--panel); border:1px solid var(--line); border-radius:6px; padding:6px 10px; }
.keys b { white-space:nowrap; }
.keys span { color:var(--muted); font-size:14px; }
.keys span::before { content:"— "; }
</style></head>
<body><main>
<h1>review-tool 사용법</h1>
<p class="sub">빨간 번호 = 아래 설명 번호 · 그림을 누르면 원본 크기 · 자세한 글 설명은 <a href="detail.html">detail.html</a></p>
<nav>${toc}</nav>
${body}
</main></body></html>
`);
console.log('docs/usage/index.html', scenes.length, '장면');

try { await Promise.race([send('Browser.close'), sleep(2000)]); } catch { }
ws.close();
await sleep(800);
proc.kill();
if (process.platform === 'win32') {
  const name = profile.split(/[\\/]/).pop();
  spawnSync('powershell', ['-NoProfile', '-Command',
    `Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like '*${name}*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }`], { stdio: 'ignore' });
}
await sleep(500);
try { rmSync(profile, { recursive: true, force: true }); } catch { }
