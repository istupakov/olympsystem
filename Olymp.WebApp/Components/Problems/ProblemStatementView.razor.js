let renderer;

export async function render(container) {
    if (!container?.isConnected) return;
    const formulas = container.querySelectorAll('.statement-math:not([data-rendered])');
    if (formulas.length === 0) return;

    renderer ??= import('../../lib/katex/katex.mjs');
    const { default: katex } = await renderer;
    for (const element of formulas) {
        if (!element.isConnected || element.dataset.rendered) continue;
        katex.render(element.textContent, element, {
            displayMode: element.dataset.display === 'true',
            throwOnError: false,
            trust: false,
            strict: 'error',
            maxExpand: 1000
        });
        element.dataset.rendered = 'true';
    }
}

export async function renderAll() {
    await Promise.all(Array.from(document.querySelectorAll('.problem-statement'), render));
}
