let highlighter;

export async function highlight(element) {
    if (!element?.isConnected || element.dataset.highlighted)
        return;

    highlighter ??= Promise.all([
        import('../../lib/highlightjs/highlight.min.js'),
        import('../../lib/highlightjs/delphi.min.js')
    ]).then(([{ default: hljs }, { default: delphi }]) => {
        hljs.registerLanguage('delphi', delphi);
        return hljs;
    });
    const hljs = await highlighter;
    const language = element.dataset.sourceLanguage;
    if (element.isConnected && !element.dataset.highlighted && hljs.getLanguage(language)) {
        element.classList.add(`language-${language}`);
        hljs.highlightElement(element);
    }
}

export async function highlightAll() {
    await Promise.all(Array.from(document.querySelectorAll('code[data-source-language]:not([data-highlighted])'), highlight));
}
