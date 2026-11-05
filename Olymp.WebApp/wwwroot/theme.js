// Apply before styles load to avoid flashing the wrong theme.
(() => {
    const systemTheme = window.matchMedia('(prefers-color-scheme: dark)');
    const themes = ['auto', 'light', 'dark'];
    const validTheme = value => themes.includes(value) ? value : 'auto';
    const readTheme = () => {
        try {
            return validTheme(localStorage.getItem('theme'));
        } catch {
            return 'auto';
        }
    };
    let theme = readTheme();

    function applyTheme() {
        document.documentElement.dataset.bsTheme = theme === 'auto'
            ? (systemTheme.matches ? 'dark' : 'light')
            : theme;
        document.querySelector('[data-theme-icon]')?.setAttribute('href', `#theme-${theme}`);
        const button = document.querySelector('[data-theme-toggle]');
        if (button) {
            const label = button.getAttribute(`data-label-${theme}`);
            button.setAttribute('aria-label', label);
            button.setAttribute('title', label);
        }
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
        Blazor.addEventListener('enhancedload', applyTheme);
    });
    document.addEventListener('click', event => {
        const button = event.target.closest('[data-theme-toggle]');
        if (!button)
            return;
        theme = themes[(themes.indexOf(theme) + 1) % themes.length];
        try {
            localStorage.setItem('theme', theme);
        } catch {
            // Keep switching available when browser storage is disabled.
        }
        applyTheme();
    });
})();
