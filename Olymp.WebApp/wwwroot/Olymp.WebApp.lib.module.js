import { formatSavedStandings } from './saved-standings.js';
import { highlightAll } from './Components/Submissions/SubmissionView.razor.js';

export function afterWebStarted(blazor) {
    formatSavedStandings();
    highlightAll();
    blazor.addEventListener('enhancedload', formatSavedStandings);
    blazor.addEventListener('enhancedload', highlightAll);
}
