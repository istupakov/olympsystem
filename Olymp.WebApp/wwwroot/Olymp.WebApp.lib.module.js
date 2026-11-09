import { formatSavedStandings } from './saved-standings.js';
import { highlightCode } from './syntax-highlighting.js';

export function afterWebStarted(blazor) {
    formatSavedStandings();
    highlightCode();
    blazor.addEventListener('enhancedload', formatSavedStandings);
    blazor.addEventListener('enhancedload', highlightCode);
}
