let highlighter;

export async function highlightCode() {
    const elements = document.querySelectorAll('code[data-source-language]:not([data-highlighted])');
    if (elements.length === 0)
        return;

    highlighter ??= Promise.all([
        import('./lib/highlightjs/highlight.min.js'),
        import('./lib/highlightjs/delphi.min.js')
    ]).then(([{ default: hljs }, { default: delphi }]) => {
        hljs.registerLanguage('delphi', delphi);
        return hljs;
    });
    const hljs = await highlighter;
    for (const element of elements) {
        const language = element.dataset.sourceLanguage;
        if (element.isConnected && !element.dataset.highlighted && hljs.getLanguage(language)) {
            element.classList.add(`language-${language}`);
            hljs.highlightElement(element);
        }
    }
}
