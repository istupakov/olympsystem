// Loaded synchronously before styles so each page starts with the selected theme.
(() => {
    const systemTheme = window.matchMedia('(prefers-color-scheme: dark)');
    const validTheme = value => ['light', 'dark', 'auto'].includes(value) ? value : 'auto';
    const readTheme = () => {
        try {
            return validTheme(localStorage.getItem('theme'));
        } catch {
            return 'auto';
        }
    };
    let theme = readTheme();

    function applyTheme() {
        const appliedTheme = theme === 'auto'
            ? (systemTheme.matches ? 'dark' : 'light')
            : theme;
        document.documentElement.dataset.bsTheme = appliedTheme;
        document.querySelectorAll('link[data-highlight-theme]').forEach(stylesheet => {
            stylesheet.media = stylesheet.dataset.highlightTheme === appliedTheme ? 'all' : 'not all';
        });

        const toggle = document.getElementById('theme-toggle');
        if (!toggle)
            return;

        const selected = document.querySelector(`[data-theme-value="${theme}"]`);
        const label = `${toggle.dataset.label}: ${selected.querySelector('span').textContent}`;
        toggle.setAttribute('aria-label', label);
        toggle.setAttribute('title', label);
        toggle.querySelector('use').setAttribute('href', `#preference-${theme}`);
        document.querySelectorAll('[data-theme-value]').forEach(button => {
            button.setAttribute('aria-pressed', String(button.dataset.themeValue === theme));
        });
    }

    applyTheme();
    systemTheme.addEventListener('change', () => {
        if (theme === 'auto')
            applyTheme();
    });
    window.addEventListener('pageshow', () => {
        theme = readTheme();
        applyTheme();
    });
    document.addEventListener('DOMContentLoaded', () => {
        applyTheme();
        document.querySelectorAll('[data-theme-value]').forEach(button => {
            button.addEventListener('click', () => {
                theme = validTheme(button.dataset.themeValue);
                try {
                    localStorage.setItem('theme', theme);
                } catch {
                    // Keep switching available even when browser storage is disabled.
                }
                applyTheme();
            });
        });
    });
})();
