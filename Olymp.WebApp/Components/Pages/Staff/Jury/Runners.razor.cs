using System.Data.Common;

using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class Runners
{
    [SupplyParameterFromQuery] public Guid? Runner { get; set; }
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
        await RefreshAsync();
        _selectedId = Runner ?? (_runners.Count > 0 ? _runners[0].Id : null);
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
            _runners = [.. Checkers.Checkers.Values.OrderBy(item => item.Name).ThenBy(item => item.Id)];
            await using var db = await ContextFactory.CreateDbContextAsync(_stop.Token);
            _compilers = await db.Compilators.AsNoTracking().OrderByDescending(item => item.Id).ToListAsync(_stop.Token);
            _loaded = true;
        }
        catch (DbException exception) { Logger.LogWarning(exception, "Could not refresh Runner information"); _error = SiteResources.StaffRefreshFailed; }
        finally { _refreshing = false; }
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
    private static string RunnerUrl(Guid id) => $"jury/runners?Runner={id}";
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); if (_poll is not null) await _poll;
        _stop.Dispose(); GC.SuppressFinalize(this);
    }
}
