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
        { selector: 'edge.back', style: { 'line-style': 'dashed' } },
        { selector: 'edge.guess', style: { 'line-style': 'dotted', 'line-color': css('--warn'), 'target-arrow-color': css('--warn') } },
        { selector: 'edge.onpath', style: { 'width': 2.5, 'line-color': css('--accent'), 'target-arrow-color': css('--accent') } },
    ];
}

export function render(id, elements, rootId, path, dotnet) {
    destroy(id);
    const container = document.getElementById(id);
    if (!container || !window.cytoscape) return;

    const cy = window.cytoscape({
        container, elements, style: styles(),
        layout: { name: 'preset' },
        wheelSensitivity: 0.25, minZoom: 0.05, maxZoom: 3,
    });
    // 위치는 C# 이 왼쪽→오른쪽 트리로 계산해 준다(preset). 글자가 읽히는 크기 아래로는 줄이지 않는다.
    cy.fit(undefined, 24);
    if (cy.zoom() < 0.85) {
        cy.zoom(0.85);
        const root = cy.getElementById(rootId);
        const p = root.renderedPosition();
        cy.panBy({ x: 40 + root.renderedWidth() / 2 - p.x, y: container.clientHeight / 2 - p.y });
    }
    cy.on('tap', 'node', ev => dotnet.invokeMethodAsync('OnGraphNodeClick', ev.target.id()));
    container._cy = cy;   // 화면 점검 스크립트(tools/ui-check.mjs)가 노드를 누를 때 쓴다

    const onTheme = () => cy.style(styles());
    const media = matchMedia('(prefers-color-scheme: dark)');
    media.addEventListener('change', onTheme);
    graphs.set(id, { cy, media, onTheme });
    highlight(id, path, false);
}

// path: 진입점부터 현재 함수까지의 그래프 노드 id 들
export function highlight(id, path, center = true) {
    const g = graphs.get(id);
    if (!g) return;
    const cy = g.cy;
    cy.elements().removeClass('onpath current');
    const ids = path ?? [];
    for (let i = 0; i < ids.length; i++) {
        const node = cy.getElementById(ids[i]);
        node.addClass(i === ids.length - 1 ? 'current' : 'onpath');
        if (i > 0) cy.getElementById(`${ids[i - 1]}->${ids[i]}`).addClass('onpath');
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
