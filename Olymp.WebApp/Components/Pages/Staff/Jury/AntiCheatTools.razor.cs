using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.AntiCheat;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class AntiCheatTools
{
    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationState { get; set; } = default!;
    private readonly InputModel _input = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<PlagiarismReport> _similarity = [];
    private IReadOnlyList<AIUsingReport> _ai = [];
    private string? _error;
    private bool _finished;
    private bool _disposed;

    private async Task RunAsync()
    {
        if (_cancellation is not null) return;
        var user = (await AuthenticationState).User;
        if (!(await Authorization.AuthorizeAsync(user, PolicyNames.JuryOrAdmin)).Succeeded) { _error = SiteResources.StaffAccessDenied; return; }
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _similarity = []; _ai = []; _error = null; _finished = false;
        var mode = _input.Mode;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var service = scope.ServiceProvider.GetRequiredService<AntiCheatReportService>();
            var sources = db.Submissions.Where(item => !item.User.IsHidden);
            if (mode is AnalysisMode.User or AnalysisMode.Period)
            {
                if (mode == AnalysisMode.User)
                {
                    string name = _input.User!.Trim();
                    var author = await db.Users.AsNoTracking().OrderBy(item => item.Id)
                        .FirstOrDefaultAsync(item => !item.IsHidden && (item.UserName == name || item.Name == name), cancellation.Token);
                    if (author is null) { _error = SiteResources.StaffUserNotFound; return; }
                    sources = sources.Where(item => item.UserId == author.Id);
                }
                else
                {
                    var from = new DateTimeOffset(_input.From!.Value);
                    var to = new DateTimeOffset(_input.To!.Value);
                    sources = sources.Where(item => item.CommitTime >= from && item.CommitTime <= to);
                }
                _similarity = await service.FindPlagiarismAsync(sources, _input.Threshold, cancellation.Token);
            }
            else
            {
                var first = await sources.AsNoTracking().Include(item => item.User).Include(item => item.Problem).Include(item => item.Compilator)
                    .SingleOrDefaultAsync(item => item.Id == _input.FirstId, cancellation.Token);
                if (first is null) { _error = SiteResources.StaffSubmissionNotFound; return; }
                if (mode == AnalysisMode.Compare)
                {
                    var second = await sources.AsNoTracking().Include(item => item.User).Include(item => item.Problem).Include(item => item.Compilator)
                        .SingleOrDefaultAsync(item => item.Id == _input.SecondId, cancellation.Token);
                    if (second is null) { _error = SiteResources.StaffSubmissionNotFound; return; }
                    var analyzer = scope.ServiceProvider.GetRequiredService<ISubmissionSimilarityService>();
                    float score = await Task.Run(() => analyzer.CompareSimilarity(first.Text, second.Text), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    _similarity = [new(first.User, second.User, [new(first, second, score)])];
                }
                else
                {
                    var analyzer = scope.ServiceProvider.GetRequiredService<IAIDetectionService>();
                    var result = await Task.Run(() => analyzer.DetectAI(first.Text, first.Compilator.Language), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    _ai = [new(first.User, [new(first, result)])];
                }
            }
            if (!_disposed) _finished = true;
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException) { _error = SiteResources.StaffInvalidPeriod; }
        catch (Exception exception) { Logger.LogError(exception, "Anti-cheat analysis failed in mode {Mode}", mode); if (!_disposed) _error = SiteResources.StaffAnalysisFailed; }
        finally { _cancellation = null; }
    }
    private void Cancel() => _cancellation?.Cancel();
    public void Dispose() { _disposed = true; Cancel(); GC.SuppressFinalize(this); }
    private enum AnalysisMode { User, Period, Compare, Ai }
    private sealed class InputModel : IValidatableObject
    {
        public AnalysisMode Mode { get; set; }
        public string? User { get; set; }
        public DateTime? From { get; set; } = DateTime.Today;
        public DateTime? To { get; set; } = DateTime.Now;
        public float Threshold { get; set; } = 0.8f;
        public int? FirstId { get; set; }
        public int? SecondId { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext context)
        {
            if (Mode == AnalysisMode.User && string.IsNullOrWhiteSpace(User)) yield return new(SiteResources.StaffUserRequired, [nameof(User)]);
            if (Mode is AnalysisMode.User or AnalysisMode.Period && (!float.IsFinite(Threshold) || Threshold < 0 || Threshold > 1)) yield return new(SiteResources.StaffThresholdInvalid, [nameof(Threshold)]);
            if (Mode == AnalysisMode.Period && (From is null || To is null || From > To)) yield return new(SiteResources.StaffInvalidPeriod, [nameof(To)]);
            if (Mode == AnalysisMode.Period && ((From is { } from && (TimeZoneInfo.Local.IsInvalidTime(from) || TimeZoneInfo.Local.IsAmbiguousTime(from)))
                || (To is { } to && (TimeZoneInfo.Local.IsInvalidTime(to) || TimeZoneInfo.Local.IsAmbiguousTime(to))))) yield return new(SiteResources.UnambiguousServerTime);
            if (Mode is AnalysisMode.Compare or AnalysisMode.Ai && FirstId is not > 0) yield return new(SiteResources.StaffSubmissionIdRequired, [nameof(FirstId)]);
            if (Mode == AnalysisMode.Compare && SecondId is not > 0) yield return new(SiteResources.StaffSubmissionIdRequired, [nameof(SecondId)]);
        }
    }
}
