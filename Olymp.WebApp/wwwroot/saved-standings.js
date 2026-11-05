function formatSavedStandings() {
    document.querySelectorAll('[data-saved-standing]').forEach(container => {
        container.querySelectorAll('table').forEach(table => {
            table.classList.add('table', 'caption-top', 'table-sm', 'table-hover', 'table-striped', 'table-bordered');
        });
        const markers = {
            successmark: ['text-success-emphasis', 'text-center'],
            failmark: ['text-danger-emphasis', 'text-center'],
            firstmark: ['table-success', 'text-center']
        };
        for (const [marker, classes] of Object.entries(markers)) {
            container.querySelectorAll(`.${marker}`).forEach(cell => cell.classList.add(...classes));
        }
    });
}

formatSavedStandings();
Blazor.addEventListener('enhancedload', formatSavedStandings);
