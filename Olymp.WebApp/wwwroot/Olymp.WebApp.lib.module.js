import { formatSavedStandings } from './Components/Contests/SavedStandingView.razor.js';
import { highlightAll } from './Components/Submissions/SubmissionView.razor.js';
import { renderAll } from './Components/Problems/ProblemStatementView.razor.js';

export function afterWebStarted(blazor) {
    formatSavedStandings();
    highlightAll();
    renderAll();
    blazor.addEventListener('enhancedload', formatSavedStandings);
    blazor.addEventListener('enhancedload', highlightAll);
    blazor.addEventListener('enhancedload', renderAll);
}
