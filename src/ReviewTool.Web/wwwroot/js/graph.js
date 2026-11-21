// 노드(그래프) 보기. Cytoscape 로 그리기만 하고, 무엇을 그릴지는 C#(Home.razor)이 정한다.
const graphs = new Map();

function css(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function styles() {
    return [
        {
            selector: 'node', style: {
                'label': 'data(label)', 'font-family': 'Cascadia Code, Consolas, monospace', 'font-size': 12,
                'text-valign': 'center', 'text-halign': 'center', 'shape': 'round-rectangle',
                'width': 'label', 'height': 22, 'padding': '6px',
                'background-color': css('--panel'), 'border-width': 1, 'border-color': css('--border'), 'color': css('--text'),
            }
        },
        { selector: 'node.entry', style: { 'border-color': css('--accent'), 'border-width': 2, 'font-weight': 'bold' } },
        // 같은 줄 묶음(한 문장에서 이어 부른 호출). 크기는 안의 노드로 정해진다.
        {
            selector: 'node.line-group', style: {
                'text-valign': 'top', 'text-halign': 'center', 'font-size': 10, 'color': css('--muted'),
                'background-color': css('--accent-soft'), 'background-opacity': 0.35,
                'border-style': 'dashed', 'border-color': css('--muted'), 'padding': '6px',
            }
        },
        { selector: 'node.line-group.picked', style: { 'border-style': 'solid', 'border-width': 2, 'border-color': css('--accent'), 'background-opacity': 0.7 } },
        { selector: 'node.external', style: { 'color': css('--ext'), 'border-style': 'dashed' } },
        { selector: 'node.hidden', style: { 'color': css('--ext'), 'font-style': 'italic' } },
        { selector: 'node.more', style: { 'border-color': css('--warn'), 'border-style': 'dotted' } },
        { selector: 'node.onpath', style: { 'background-color': css('--next') } },
        { selector: 'node.current', style: { 'background-color': css('--accent'), 'color': '#fff', 'border-color': css('--accent') } },
        {
            selector: 'edge', style: {
                'width': 1.2, 'line-color': css('--border'), 'target-arrow-color': css('--border'),
                'target-arrow-shape': 'triangle', 'curve-style': 'taxi', 'taxi-direction': 'rightward',
                'taxi-turn': 20, 'arrow-scale': 0.8,
            }
        },
        // 호출 줄 번호(화살표 앞). 노드를 옮겨도 소스 순서를 읽을 수 있게
        {
            selector: 'edge[label != ""]', style: {
                'target-label': 'data(label)', 'target-text-offset': 18, 'target-text-margin-y': -7,
                'font-size': 9, 'color': css('--muted'), 'font-family': 'Cascadia Code, Consolas, monospace',
            }
        },
        { selector: 'edge.back', style: { 'line-style': 'dashed' } },
        { selector: 'edge.guess', style: { 'line-style': 'dotted', 'line-color': css('--warn'), 'target-arrow-color': css('--warn') } },
        { selector: 'edge.onpath', style: { 'width': 2.5, 'line-color': css('--accent'), 'target-arrow-color': css('--accent'), 'color': css('--accent') } },
    ];
}

// 노드를 끌면 트리 자식·손자(data.tp = 트리 부모)도 같이 옮긴다. 묶음 상자는 안의 함수들의 자식까지.
// 묶음 상자 자체는 안의 노드를 따라 Cytoscape 가 옮기므로, 실제로 옮기는 것은 묶음이 아닌 노드뿐이다.
function enableSubtreeDrag(cy) {
    const kids = new Map();
    cy.nodes().forEach(n => {
        const tp = n.data('tp');
        if (tp) (kids.get(tp) ?? kids.set(tp, []).get(tp)).push(n.id());
    });
    let moving = [];
    let last = null;
    cy.on('grab', 'node', ev => {
        const start = ev.target;
        const roots = start.isParent() ? start.children().map(c => c.id()) : [start.id()];
        const seen = new Set(roots);
        const queue = [...roots];
        while (queue.length) {
            for (const k of kids.get(queue.shift()) ?? []) {
                if (!seen.has(k)) { seen.add(k); queue.push(k); }
            }
        }
        // 잡은 것(과 묶음이면 안의 노드)은 Cytoscape 가 옮긴다
        roots.forEach(r => seen.delete(r));
        moving = [...seen].map(k => cy.getElementById(k)).filter(n => !n.isParent());
        last = { ...start.position() };
    });
    cy.on('drag', 'node', ev => {
        if (!last) return;
        const p = ev.target.position();
        const d = { x: p.x - last.x, y: p.y - last.y };
        last = { ...p };
        moving.forEach(n => n.shift(d));
    });
    cy.on('free', 'node', () => { moving = []; last = null; });
}

export function scrollToSelector(selector) {
    document.querySelector(selector)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
}

export function render(id, elements, rootId, path, dotnet, picked) {
    destroy(id);
    const container = document.getElementById(id);
    if (!container || !window.cytoscape) return;

    const cy = window.cytoscape({
        container, elements, style: styles(),
        layout: { name: 'preset' },
        wheelSensitivity: 0.25, minZoom: 0.05, maxZoom: 3,
    });
    // 같은 줄 묶음 안의 함수: 왼쪽 끝을 맞추고, 끌어도 움직이지 않게 고정(묶음은 상자를 잡고 통째로 옮긴다)
    cy.nodes('.line-group').forEach(group => {
        const members = group.children();
        const left = Math.min(...members.map(m => m.position('x') - m.outerWidth() / 2));
        members.forEach(m => m.position('x', left + m.outerWidth() / 2));
        members.ungrabify();
    });
    // 위치는 C# 이 왼쪽→오른쪽 트리로 계산해 준다(preset). 글자가 읽히는 크기 아래로는 줄이지 않는다.
    cy.fit(undefined, 24);
    if (cy.zoom() < 0.85) {
        cy.zoom(0.85);
        const root = cy.getElementById(rootId);
        const p = root.renderedPosition();
        cy.panBy({ x: 40 + root.renderedWidth() / 2 - p.x, y: container.clientHeight / 2 - p.y });
    }
    // 묶음 상자 위를 눌렀을 때는 안의 노드 대신 상자 하나만 알린다(겹친 노드 탭이 두 번 가지 않게)
    cy.on('tap', 'node', ev => {
        ev.stopPropagation?.();
        dotnet.invokeMethodAsync('OnGraphNodeClick', ev.target.id());
    });
    enableSubtreeDrag(cy);
    container._cy = cy;   // 화면 점검 스크립트(tools/ui-check.mjs)가 노드를 누를 때 쓴다

    const onTheme = () => cy.style(styles());
    const media = matchMedia('(prefers-color-scheme: dark)');
    media.addEventListener('change', onTheme);
    graphs.set(id, { cy, media, onTheme });
    highlight(id, path, picked, false);
}

// path: 진입점부터 현재 함수까지의 그래프 노드 id 들, picked: 고른 줄의 묶음 상자 id(없으면 null)
export function highlight(id, path, picked, center = true) {
    const g = graphs.get(id);
    if (!g) return;
    const cy = g.cy;
    cy.elements().removeClass('onpath current picked');
    if (picked) {
        const box = cy.getElementById(picked);
        box.addClass('picked');
        box.incomers('edge').addClass('onpath');   // 부르는 함수 → 고른 줄 묶음 선도 파랗게
    }
    const ids = path ?? [];
    for (let i = 0; i < ids.length; i++) {
        const node = cy.getElementById(ids[i]);
        node.addClass(i === ids.length - 1 ? 'current' : 'onpath');
        if (i > 0) {
            let edge = cy.getElementById(`${ids[i - 1]}->${ids[i]}`);
            // 같은 줄 묶음 안의 노드면 선은 묶음 상자로 가 있다
            if (edge.empty() && node.parent().nonempty()) edge = cy.getElementById(`${ids[i - 1]}->${node.parent().id()}`);
            edge.addClass('onpath');
        }
    }
    const last = ids.length ? cy.getElementById(ids[ids.length - 1]) : null;
    if (center && last && last.nonempty()) {
        const ext = cy.extent();
        const p = last.position();
        if (p.x < ext.x1 || p.x > ext.x2 || p.y < ext.y1 || p.y > ext.y2) cy.animate({ center: { eles: last } }, { duration: 200 });
    }
}

export function fit(id) {
    graphs.get(id)?.cy.fit(undefined, 24);
}

export function destroy(id) {
    const g = graphs.get(id);
    if (!g) return;
    g.media.removeEventListener('change', g.onTheme);
    g.cy.destroy();
    graphs.delete(id);
}

// 전체 흐름도·클래스 다이어그램. 위치(preset)와 크기는 C#(SystemMapView)이 계산한다.
function mapStyles() {
    return [
        {
            selector: 'node', style: {
                'label': 'data(label)', 'font-family': 'Cascadia Code, Consolas, monospace', 'font-size': 11,
                'text-valign': 'center', 'text-halign': 'center', 'shape': 'round-rectangle',
                'text-wrap': 'wrap', 'width': 'data(w)', 'height': 'data(h)',
                'background-color': css('--panel'), 'border-width': 1, 'border-color': css('--border'), 'color': css('--text'),
            }
        },
        {
            selector: 'node.process', style: {
                'text-valign': 'top', 'text-halign': 'center', 'font-size': 13, 'font-weight': 'bold',
                'background-color': css('--bg'), 'background-opacity': 0.6, 'border-style': 'dashed', 'padding': '18px',
            }
        },
        { selector: 'node.uml', style: { 'text-halign': 'center', 'text-justification': 'left', 'font-size': 10.5 } },
        { selector: 'node.entry', style: { 'border-color': css('--accent'), 'border-width': 2 } },
        { selector: 'node.dim', style: { 'opacity': 0.25 } },
        { selector: 'node.on', style: { 'background-color': css('--next'), 'border-color': css('--accent'), 'border-width': 2 } },
        {
            selector: 'edge', style: {
                'width': 'data(width)', 'line-color': css('--border'), 'target-arrow-color': css('--border'),
                'target-arrow-shape': 'triangle', 'curve-style': 'bezier', 'arrow-scale': 0.7, 'opacity': 0.7,
            }
        },
        { selector: 'edge.guess', style: { 'line-style': 'dotted', 'line-color': css('--warn'), 'target-arrow-color': css('--warn') } },
        {
            selector: 'edge.packet', style: {
                'line-color': css('--accent'), 'target-arrow-color': css('--accent'), 'line-style': 'dashed', 'opacity': 0.9,
                'label': 'data(label)', 'font-size': 9, 'color': css('--accent'), 'text-background-color': css('--bg'),
                'text-background-opacity': 0.85, 'text-rotation': 'autorotate',
            }
        },
        { selector: 'edge.inherit', style: { 'target-arrow-shape': 'triangle-backcurve', 'target-arrow-fill': 'hollow', 'arrow-scale': 1.3, 'line-color': css('--text'), 'target-arrow-color': css('--text') } },
        { selector: 'edge.dim', style: { 'opacity': 0.08 } },
    ];
}

export function renderMap(id, elements, dotnet) {
    destroy(id);
    const container = document.getElementById(id);
    if (!container || !window.cytoscape) return;
    const cy = window.cytoscape({
        container, elements, style: mapStyles(), layout: { name: 'preset' },
        wheelSensitivity: 0.25, minZoom: 0.05, maxZoom: 3, autoungrabify: true,
    });
    cy.fit(undefined, 24);
    if (cy.zoom() < 0.5) cy.zoom(0.5);
    cy.on('tap', 'node', ev => {
        if (!ev.target.hasClass('process')) dotnet.invokeMethodAsync('OnMapNodeClick', ev.target.id());
    });
    container._cy = cy;
    const onTheme = () => cy.style(mapStyles());
    const media = matchMedia('(prefers-color-scheme: dark)');
    media.addEventListener('change', onTheme);
    graphs.set(id, { cy, media, onTheme });
}

// 칸 경계선(.splitter) 끌기. 크기는 .panes 의 CSS 변수(data-var)로 넣고 브라우저에 기억한다. 두 번 누르면 기본 크기.
// 경계선은 Blazor 가 그리고, 여기서는 문서 전체에 이벤트를 한 번만 건다(칸이 다시 그려져도 그대로 동작).
const SPLIT_VARS = ['--w-entries', '--w-tree', '--w-graph', '--h-top'];
let splitListening = false;

function resizeGraphs() {
    for (const g of graphs.values()) g.cy.resize();
}

export function initSplitters() {
    const panes = document.querySelector('.panes');
    if (panes) {
        for (const name of SPLIT_VARS) {
            const v = getPref('split' + name);
            if (v) panes.style.setProperty(name, v);
        }
        resizeGraphs();
    }
    if (splitListening) return;
    splitListening = true;
    document.addEventListener('pointerdown', ev => {
        const s = ev.target.closest?.('.splitter');
        const box = s?.closest('.panes');
        const before = s?.previousElementSibling;
        if (!s || !box || !before || ev.button !== 0) return;
        ev.preventDefault();
        const vertical = s.classList.contains('h');      // 위아래로 끄는 경계
        const start = vertical ? ev.clientY : ev.clientX;
        const base = vertical ? before.getBoundingClientRect().height : before.getBoundingClientRect().width;
        const total = vertical ? box.clientHeight : box.clientWidth;
        const name = s.dataset.var;
        s.setPointerCapture(ev.pointerId);
        s.classList.add('drag');
        document.body.classList.add(vertical ? 'resizing-row' : 'resizing-col');
        const move = e => {
            const v = Math.max(120, Math.min(total - 200, base + (vertical ? e.clientY : e.clientX) - start));
            box.style.setProperty(name, Math.round(v) + 'px');
        };
        const up = () => {
            s.removeEventListener('pointermove', move);
            s.removeEventListener('pointerup', up);
            s.removeEventListener('pointercancel', up);
            s.classList.remove('drag');
            document.body.classList.remove('resizing-row', 'resizing-col');
            const v = box.style.getPropertyValue(name);
            if (v) setPref('split' + name, v);
            resizeGraphs();
        };
        s.addEventListener('pointermove', move);
        s.addEventListener('pointerup', up);
        s.addEventListener('pointercancel', up);
    });
    document.addEventListener('dblclick', ev => {
        const s = ev.target.closest?.('.splitter');
        if (!s) return;
        s.closest('.panes')?.style.removeProperty(s.dataset.var);
        try { localStorage.removeItem('review-tool:split' + s.dataset.var); } catch { }
        resizeGraphs();
    });
}

// 보기 방식처럼 사람마다 다른 편의 설정만 저장한다. 막혀 있어도 기본값으로 동작.
export function getPref(key) {
    try { return localStorage.getItem('review-tool:' + key); } catch { return null; }
}

export function setPref(key, value) {
    try { localStorage.setItem('review-tool:' + key, value); } catch { }
}

// 문맥 복사. 클립보드 API 가 막히면(권한·포커스) 숨긴 textarea 로 한 번 더 시도한다.
export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        let ok = false;
        try { ok = document.execCommand('copy'); } catch { }
        ta.remove();
        return ok;
    }
}

// 공유 보고서 내려받기(브라우저 다운로드). 서버에 파일을 쓰지 않는다.
export function downloadText(name, text) {
    const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
