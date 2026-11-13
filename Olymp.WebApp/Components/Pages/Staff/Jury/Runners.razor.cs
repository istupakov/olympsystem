using System.ComponentModel.DataAnnotations;
using System.Data.Common;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class Runners
{
    [SupplyParameterFromQuery]
    public bool Compilers { get; set; }
    [SupplyParameterFromQuery]
    public Guid? Runner { get; set; }
    [SupplyParameterFromQuery]
    public string? Compiler { get; set; }
    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationState { get; set; } = default!;

    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _poll;
    private List<ICheckerService> _runners = [];
    private List<Compilator> _compilers = [];
    private Guid? _selectedId;
    private Compilator? _pendingDisable;
    private readonly RunInput _input = new();
    private string _search = "";
    private string? _error;
    private string? _notice;
    private string? _output;
    private bool _loaded;
    private bool _admin;
    private bool _busy;
    private bool _running;
    private bool _refreshing;
    private ICheckerService? SelectedRunner => _runners.SingleOrDefault(item => item.Id == _selectedId);
    private IEnumerable<Compilator> VisibleCompilers => _compilers.Where(item => string.IsNullOrWhiteSpace(_search)
        || item.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
        || (item.Description?.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || item.Language.Contains(_search, StringComparison.CurrentCultureIgnoreCase));

    protected override async Task OnParametersSetAsync()
    {
        await RefreshAsync();
        _selectedId = Runner ?? _selectedId ?? (_runners.Count > 0 ? _runners[0].Id : null);
        if (Compiler is not null)
            _input.Compiler = Compiler;
        var configurations = SelectedRunner?.Configurations;
        if (configurations is null || !configurations.Any(item => item.Name == _input.Compiler))
            _input.Compiler = configurations is { Count: > 0 } ? configurations[0].Name : null;
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            _poll = PollAsync();
        return Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            _admin = (await AuthenticationState).User.IsInRole(RoleNames.Admin);
            _runners = [.. Checkers.Checkers.Values.OrderBy(item => item.Name)];
            await using var db = await ContextFactory.CreateDbContextAsync(_stop.Token);
            _compilers = await db.Compilators.AsNoTracking().OrderByDescending(item => item.IsActive).ThenByDescending(item => item.Id).ToListAsync(_stop.Token);
            _loaded = true;
        }
        catch (DbException exception)
        {
            Logger.LogWarning(exception, "Could not refresh Runner information");
            _error = SiteResources.StaffRefreshFailed;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await InvokeAsync(async () =>
                {
                    if (_stop.IsCancellationRequested)
                        return;
                    await RefreshAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private static IEnumerable<CompilatorInfo> RunnerCompilers(ICheckerService runner) => runner.Compilators
        ?? [.. runner.Configurations.Select(config => new CompilatorInfo(config, config.Name, null))];

    private IEnumerable<ICheckerService> AvailableRunners(Compilator compiler) => _runners.Where(runner => runner.SupportsCompiler(compiler));

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
        tests.Count(item => item.Result == item.ExpectedResult), tests.Length,
        tests.Count(item => item.Result is not null && item.Result != item.ExpectedResult));

    private static string RunnerUrl(Guid id) => $"jury/runners?Runner={id}";
    private static string RunUrl(Guid id, string compiler) => $"{RunnerUrl(id)}&Compiler={Uri.EscapeDataString(compiler)}#trial-run";
    private void SelectRunner(ChangeEventArgs args) => Navigation.NavigateTo(Guid.TryParse(args.Value?.ToString(), out var id) ? RunnerUrl(id) : "jury/runners");

    private Task ToggleAsync(Compilator compiler)
    {
        if (compiler.IsActive)
        {
            _pendingDisable = compiler;
            return Task.CompletedTask;
        }
        return SetActiveAsync(compiler.Id, true);
    }

    private async Task SetActiveAsync(int id, bool active)
    {
        if (_busy || !(await Authorization.AuthorizeAsync((await AuthenticationState).User, PolicyNames.Admin)).Succeeded)
            return;
        _busy = true;
        _error = _notice = null;
        try
        {
            await Checkers.SetCompilatorActiveAsync(id, active, _stop.Token);
            _pendingDisable = null;
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or InvalidOperationException)
        {
            Logger.LogWarning(exception, "Could not change compiler {CompilerId}", id);
            _error = SiteResources.StaffSaveFailed;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AddCompilerAsync(Guid id, CompilatorInfo info)
    {
        if (_busy || !(await Authorization.AuthorizeAsync((await AuthenticationState).User, PolicyNames.Admin)).Succeeded)
            return;
        _busy = true;
        _error = _notice = null;
        try
        {
            await Checkers.AddCompilatorAsync(id, info.Configuration.Name, info.DisplayName, _stop.Token);
            _notice = SiteResources.StaffCompilerAdded;
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or InvalidOperationException)
        {
            Logger.LogWarning(exception, "Could not add compiler from Runner {RunnerId}", id);
            _error = SiteResources.StaffCompilerUnavailable;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunAsync()
    {
        if (_running || !(await Authorization.AuthorizeAsync((await AuthenticationState).User, PolicyNames.JuryOrAdmin)).Succeeded)
            return;
        _error = _output = null;
        if (_selectedId is not Guid id || !Checkers.Checkers.TryGetValue(id, out var runner)
            || runner.Configurations.SingleOrDefault(item => item.Name == _input.Compiler) is not { } config)
        {
            _error = SiteResources.StaffCompilerUnavailable;
            return;
        }
        _running = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _runCancellation = cancellation;
        try
        {
            _output = await runner.Run(_input.Code, config, _input.Input, TimeSpan.FromSeconds(1), 128, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Trial run failed on Runner {RunnerId}", id);
            _error = SiteResources.StaffRunnerDisconnected;
        }
        finally
        {
            _runCancellation = null;
            _running = false;
        }
    }

    private Task CancelRunAsync() => _runCancellation?.CancelAsync() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_poll is not null)
            await _poll;
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class RunInput
    {
        public string? Compiler { get; set; }
        [Required(ErrorMessageResourceType = typeof(AccountResources), ErrorMessageResourceName = nameof(AccountResources.FieldRequired))]
        [Display(Name = nameof(SiteResources.SourceCode), ResourceType = typeof(SiteResources))]
        public string Code { get; set; } = "";
        public string Input { get; set; } = "";
    }
}
