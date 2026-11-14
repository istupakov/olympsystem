using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.AntiCheat;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class AntiCheatTools
{
    [Parameter] public int? ContestId { get; set; }
    [SupplyParameterFromQuery] public int? AuthorId { get; set; }
    [SupplyParameterFromQuery] public int? SubmissionId { get; set; }
    [SupplyParameterFromQuery] public string? Selection { get; set; }
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = default!;
    private InputModel _input = new();
    private StaffSubmissionFilter _selection = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<PlagiarismReport> _similarity = [];
    private IReadOnlyList<AIUsingReport> _ai = [];
    private List<Problem> _problems = [];
    private int _sourceCount;
    private readonly HashSet<int> _excluded = [];
    private HashSet<int>? _analyzedProblems;
    private int? _reportAuthorId, _reportSubmissionId;
    private bool Finished { get; set; }
    private bool NeedsAnalysis => Finished && _analyzedProblems is not null
        && _problems.Any(problem => !_excluded.Contains(problem.Id) && !_analyzedProblems.Contains(problem.Id));
    private Contest? _contest;
    private string? _error;
    private bool _disposed;
    private (int?, int?, int?, string?)? _parameters;
    private int _generation;
    private bool HasContext => ContestId.HasValue || AuthorId.HasValue || SubmissionId.HasValue || Selection is not null;
    private bool _scopeValid;
    private User? _contextAuthor;
    private Submission? _contextSubmission;
    private string? _authorUrl;
    private readonly List<(string Label, string Value)> _selectionSummary = [];
    private string EmptySelectionText => _input.Mode switch
    {
        AnalysisMode.User when _input.AuthorId is null && string.IsNullOrWhiteSpace(_input.User) => SiteResources.StaffChooseAnalysisAuthor,
        AnalysisMode.User => SiteResources.StaffAuthorNoSubmissions,
        AnalysisMode.Ai when _input.FirstId is null => SiteResources.StaffSubmissionIdRequired,
        _ => SiteResources.StaffEmptyAnalysisSelection
    };

    protected override async Task OnParametersSetAsync()
    {
        var parameters = (ContestId, AuthorId, SubmissionId, Selection);
        if (_parameters == parameters) return;
        _parameters = parameters; Cancel(); _generation++; _error = null; Finished = false; _similarity = []; _ai = []; _excluded.Clear();
        _scopeValid = false;
        _contextAuthor = null;
        _contextSubmission = null;
        _contest = null;
        _problems = [];
        _sourceCount = 0;
        _selectionSummary.Clear();
        _analyzedProblems = null;
        _reportAuthorId = null;
        _reportSubmissionId = null;
        try
        {
            _selection = Selection is null ? new(Contest: ContestId) : StaffSubmissionFilter.FromList(Selection);
            _input = new()
            {
                Mode = SubmissionId.HasValue ? AnalysisMode.Ai : AuthorId.HasValue ? AnalysisMode.User : ContestId.HasValue || Selection is not null ? AnalysisMode.Period : AnalysisMode.User,
                AuthorId = AuthorId,
                FirstId = SubmissionId,
                From = Selection is not null ? _selection.From : ContestId.HasValue ? null : DateTime.Today,
                To = Selection is not null ? _selection.To : ContestId.HasValue ? null : DateTime.Now
            };
            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            _contest = ContestId is int id ? await db.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id) : null;
            _contextAuthor = AuthorId is int authorId ? await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == authorId) : null;
            _contextSubmission = SubmissionId is int submissionId ? await db.Submissions.AsNoTracking().Include(item => item.User)
                .Include(item => item.Problem).ThenInclude(item => item.Contest).SingleOrDefaultAsync(item => item.Id == submissionId) : null;
            var actor = (await AuthenticationState).User;
            var contextAuthor = _contextAuthor ?? _contextSubmission?.User;
            _authorUrl = contextAuthor is Competitor participant ? $"jury/contests/{participant.ContestId}/participants/{participant.Id}"
                : contextAuthor is null ? null : actor.IsInRole(RoleNames.Admin) ? $"admin/users/{contextAuthor.Id}/edit" : $"jury/submissions?AuthorId={contextAuthor.Id}";

            await LoadSelectionSummaryAsync(db);
            await LoadProblemsAsync();
            _scopeValid = true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { _error = SiteResources.StaffInvalidPeriod; }
    }

    private async Task LoadSelectionSummaryAsync(OlympContext db)
    {
        _selectionSummary.Clear();
        if (Selection is null) return;

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) _selectionSummary.Add((label, value));
        }

        if (_selection.Contest is int contest)
            Add(SiteResources.StaffContest, StaffEntityLabel.Format(contest, await db.Contests.Where(item => item.Id == contest).Select(item => item.Name).SingleOrDefaultAsync()));
        if (_selection.AuthorId is int author)
            Add(SiteResources.SubmissionAuthor, StaffEntityLabel.Format(author, await db.Users.Where(item => item.Id == author).Select(item => item.Name).SingleOrDefaultAsync()));
        else Add(SiteResources.SubmissionAuthor, _selection.Author);
        if (_selection.Problem is int problem)
            Add(SiteResources.Problem, StaffEntityLabel.Format(problem, await db.Problems.Where(item => item.Id == problem).Select(item => item.Name).SingleOrDefaultAsync()));
        else Add(SiteResources.Problem, _selection.ProblemSearch);
        if (_selection.Compiler is int compiler)
            Add(SiteResources.Compiler, StaffEntityLabel.Format(compiler, await db.Compilators.Where(item => item.Id == compiler).Select(item => item.Description ?? item.Name).SingleOrDefaultAsync()));

        Add(SiteResources.StaffLanguage, _selection.Language);
        Add(SiteResources.IpAddress, _selection.IpAddress);
        Add(SiteResources.SubmissionStatus, _selection.StatusCode is int status ? CheckStatusCodes.Description(status) : StaffVerdicts.Label(_selection.Status));
        Add(SiteResources.StaffSubmissionAuthors, _selection.Scope switch
        {
            "own" => SiteResources.OwnSubmissions,
            "participants" => SiteResources.ContestParticipants,
            "hidden" => SiteResources.StaffJuryAuthors,
            _ => null
        });
        Add(SiteResources.StaffDateFrom, _selection.From?.ToString("g"));
        Add(SiteResources.StaffDateTo, _selection.To?.ToString("g"));
    }

    private async Task<IQueryable<Submission>> SourcesAsync(OlympContext db, CancellationToken token)
    {
        var actor = (await AuthenticationState).User;
        int userId = int.TryParse(actor.FindFirstValue(ClaimTypes.NameIdentifier), out int idValue) ? idValue : 0;
        var selection = _selection;
        if (_input.Mode == AnalysisMode.Period) selection = selection with { From = _input.From, To = _input.To };
        var query = selection.Apply(db, userId).Where(item => !item.User.IsHidden);
        if (_input.Mode == AnalysisMode.User)
        {
            int? id = _input.AuthorId;
            if (id is null && !string.IsNullOrWhiteSpace(_input.User))
            {
                string name = _input.User.Trim();
                id = await db.Users.Where(item => !item.IsHidden && (item.UserName == name || item.Name == name))
                    .OrderBy(item => item.Id).Select(item => (int?)item.Id).FirstOrDefaultAsync(token);
            }
            query = query.Where(item => item.UserId == id);
        }
        if (_input.Mode == AnalysisMode.Ai) query = query.Where(item => item.Id == _input.FirstId);
        return query;
    }

    private async Task LoadProblemsAsync()
    {
        _similarity = [];
        _ai = [];
        Finished = false;
        _analyzedProblems = null;
        _reportAuthorId = null;
        _reportSubmissionId = null;
        if (_input.Mode == AnalysisMode.Compare || (_input.Mode == AnalysisMode.Period && _input.From > _input.To))
        {
            _problems = [];
            _sourceCount = 0;
            if (_input.Mode == AnalysisMode.Compare) _excluded.Clear();
            return;
        }
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var sources = await SourcesAsync(db, CancellationToken.None);
        _sourceCount = await sources.CountAsync();
        _problems = await sources.Select(item => item.Problem).Distinct().Include(item => item.Contest).AsNoTracking()
            .OrderBy(item => item.ContestId).ThenBy(item => item.Number).ToListAsync();
        _excluded.IntersectWith(_problems.Select(item => item.Id));
        if (_problems.Count == 1) _excluded.Clear();
    }

    private void ToggleProblem(int id, bool enabled) { if (enabled) _excluded.Remove(id); else _excluded.Add(id); }

    private async Task RunAsync()
    {
        if (_cancellation is not null || !_scopeValid || (_input.Mode != AnalysisMode.Compare && _problems.All(item => _excluded.Contains(item.Id)))) return;
        var user = (await AuthenticationState).User;
        if (!(await Authorization.AuthorizeAsync(user, PolicyNames.JuryOrAdmin)).Succeeded) { _error = SiteResources.StaffAccessDenied; return; }
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        int generation = _generation;
        _similarity = []; _ai = []; _error = null; Finished = false;
        var mode = _input.Mode;
        float similarityThreshold = _input.Threshold, aiThreshold = _input.AiThreshold;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            if (mode == AnalysisMode.Compare)
            {
                var sources = db.Submissions.Where(item => !item.User.IsHidden);
                var first = await sources.Include(item => item.User).Include(item => item.Problem).ThenInclude(item => item.Contest).Include(item => item.Compilator).SingleOrDefaultAsync(item => item.Id == _input.FirstId, cancellation.Token);
                var second = await sources.Include(item => item.User).Include(item => item.Problem).ThenInclude(item => item.Contest).Include(item => item.Compilator).SingleOrDefaultAsync(item => item.Id == _input.SecondId, cancellation.Token);
                if (first is null || second is null) { _error = SiteResources.StaffSubmissionNotFound; return; }
                var analyzer = scope.ServiceProvider.GetRequiredService<ISubmissionSimilarityService>();
                float score = await Task.Run(() => analyzer.CompareSimilarity(first.Text, second.Text), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation == _generation && !_disposed) _similarity = [new(first.User, second.User, [new(first, second, score)])];
            }
            else
            {
                var sources = await SourcesAsync(db, cancellation.Token);
                int[] included = [.. _problems.Where(item => !_excluded.Contains(item.Id)).Select(item => item.Id)];
                sources = sources.Where(item => included.Contains(item.ProblemId));
                int? reportAuthor = mode == AnalysisMode.User ? await sources.Select(item => (int?)item.UserId).FirstOrDefaultAsync(cancellation.Token) : _selection.AuthorId;
                int? reportSubmission = mode == AnalysisMode.Ai ? _input.FirstId : null;
                var service = scope.ServiceProvider.GetRequiredService<AntiCheatReportService>();
                var report = await service.AnalyzeAsync(sources, similarityThreshold, aiThreshold, cancellation.Token);
                if (generation == _generation && !_disposed)
                {
                    _similarity = report.Similarity;
                    _ai = report.Ai;
                    _analyzedProblems = [.. included];
                    _reportAuthorId = reportAuthor;
                    _reportSubmissionId = reportSubmission;
                }
            }
            if (generation == _generation && !_disposed) Finished = true;
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException) { if (generation == _generation) _error = SiteResources.StaffInvalidPeriod; }
        catch (Exception exception) { Logger.LogError(exception, "Anti-cheat analysis failed in mode {Mode}", mode); if (generation == _generation && !_disposed) _error = SiteResources.StaffAnalysisFailed; }
        finally { if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null; }
    }

    private void Cancel() => _cancellation?.Cancel();
    public void Dispose() { _disposed = true; Cancel(); GC.SuppressFinalize(this); }
    private enum AnalysisMode { User, Period, Compare, Ai }
    private sealed class InputModel : IValidatableObject
    {
        public AnalysisMode Mode { get; set; }
        public int? AuthorId { get; set; }
        public string? User { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public float Threshold { get; set; } = 0.8f;
        public float AiThreshold { get; set; } = 0.8f;
        public int? FirstId { get; set; }
        public int? SecondId { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext context)
        {
            if (Mode == AnalysisMode.User && AuthorId is null && string.IsNullOrWhiteSpace(User)) yield return new(SiteResources.StaffUserRequired, [nameof(User)]);
            if (Mode != AnalysisMode.Compare && (!float.IsFinite(Threshold) || Threshold is < 0 or > 1 || !float.IsFinite(AiThreshold) || AiThreshold is < 0 or > 1)) yield return new(SiteResources.StaffThresholdInvalid);
            if (Mode == AnalysisMode.Period && From > To) yield return new(SiteResources.StaffInvalidPeriod);
            if (Mode == AnalysisMode.Period && ((From is { } from && (TimeZoneInfo.Local.IsInvalidTime(from) || TimeZoneInfo.Local.IsAmbiguousTime(from)))
                || (To is { } to && (TimeZoneInfo.Local.IsInvalidTime(to) || TimeZoneInfo.Local.IsAmbiguousTime(to))))) yield return new(SiteResources.UnambiguousServerTime);
            if (Mode is AnalysisMode.Compare or AnalysisMode.Ai && FirstId is not > 0) yield return new(SiteResources.StaffSubmissionIdRequired);
            if (Mode == AnalysisMode.Compare && SecondId is not > 0) yield return new(SiteResources.StaffSubmissionIdRequired);
        }
    }
}
