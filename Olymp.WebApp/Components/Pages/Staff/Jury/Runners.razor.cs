using System.Data.Common;

using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain.Models;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class Runners
{
    [SupplyParameterFromQuery] public Guid? Runner { get; set; }
    [SupplyParameterFromQuery] public string? SelfTest { get; set; }
    [SupplyParameterFromQuery] public bool? MissingCompiler { get; set; }
    private Filters _filters = new();
    private sealed class Filters { public string? SelfTest { get; set; } public string? MissingCompiler { get; set; } }
    private sealed record RunnerRow(int Index, Guid Id, string Name, DateTimeOffset Connected, CheckerSelfTestingStatus Status, string Summary, bool MissingCompiler);
    private IReadOnlyList<RunnerRow> Rows => [.. _runners.Select((runner, index) => new RunnerRow(index, runner.Id, runner.Name, runner.ConnectingTime,
        runner.SelfTestingStatus, Summary([.. runner.SelfTestingResults]), runner.Compilators?.Any(info => !_compilers.Any(compiler => CompilerMatching.Matches(compiler, info))) ?? false))
        .Where(row => !Enum.TryParse<CheckerSelfTestingStatus>(SelfTest, out var status) || row.Status == status)
        .Where(row => MissingCompiler is not bool missing || row.MissingCompiler == missing)];
    private void ApplyFilters() => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
    {
        [nameof(SelfTest)] = _filters.SelfTest,
        [nameof(MissingCompiler)] = bool.TryParse(_filters.MissingCompiler, out bool missing) ? missing : null,
        ["row"] = null
    }));
    private readonly CancellationTokenSource _stop = new();
    private Task? _poll;
    private List<ICheckerService> _runners = [];
    private List<Compilator> _compilers = [];
    private Guid? _selectedId;
    private string? _error;
    private bool _loaded, _refreshing;
    private ICheckerService? SelectedRunner => _runners.SingleOrDefault(item => item.Id == _selectedId);
    protected override async Task OnParametersSetAsync()
    {
        _filters = new() { SelfTest = SelfTest, MissingCompiler = MissingCompiler?.ToString() };
        await RefreshAsync();
        var rows = Rows;
        _selectedId = Runner ?? (rows.Count > 0 ? rows[0].Id : null);
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) _poll = PollAsync();
        return Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await using var db = await ContextFactory.CreateDbContextAsync(_stop.Token);
            var compilers = await db.Compilators.AsNoTracking().OrderByDescending(item => item.Id).ToListAsync(_stop.Token);
            _runners = [.. Checkers.Checkers.Values.OrderBy(item => item.Name).ThenBy(item => item.Id)];
            _compilers = compilers;
            _error = null;
        }
        catch (DbException exception) { Logger.LogWarning(exception, "Could not refresh Runner information"); _error = SiteResources.StaffRefreshFailed; }
        finally { _loaded = true; _refreshing = false; }
    }

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_stop.Token))
                await InvokeAsync(async () => { await RefreshAsync(); StateHasChanged(); });
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private static IEnumerable<CompilatorInfo> RunnerCompilers(ICheckerService runner) => runner.Compilators
        ?? [.. runner.Configurations.Select(config => new CompilatorInfo(config, config.Name, null))];
    private static string StatusText(CheckerSelfTestingStatus status) => status switch
    {
        CheckerSelfTestingStatus.Success => SiteResources.StaffSelfTestPassed,
        CheckerSelfTestingStatus.Failed => SiteResources.StaffSelfTestFailed,
        CheckerSelfTestingStatus.InProcess => SiteResources.StaffSelfTestRunning,
        _ => SiteResources.StaffSelfTestUnknown
    };
    private static string StatusClass(CheckerSelfTestingStatus status) => status switch
    {
        CheckerSelfTestingStatus.Success => "text-bg-success",
        CheckerSelfTestingStatus.Failed => "text-bg-danger",
        CheckerSelfTestingStatus.InProcess => "text-bg-primary",
        _ => "text-bg-secondary"
    };
    private static string Summary(CheckerTestResult[] tests) => string.Format(SiteResources.StaffTestsSummary,
        tests.Count(item => item.Result == item.ExpectedResult), tests.Length, tests.Count(item => item.Result is not null && item.Result != item.ExpectedResult));
    private string RunnerUrl(Guid id) => StaffFilterUrl.Set(Navigation.Uri, nameof(Runner), id);
    private static string ConfigurationStatusClass(CompilatorInfo info, CheckerTestResult[] tests) =>
        info.Error is not null || tests.Any(test => test.Result is not null && test.Result != test.ExpectedResult) ? "text-bg-danger"
        : tests.Length > 0 && tests.All(test => test.Result == test.ExpectedResult) ? "text-bg-success" : "text-bg-secondary";
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); if (_poll is not null) await _poll;
        _stop.Dispose(); GC.SuppressFinalize(this);
    }
}
