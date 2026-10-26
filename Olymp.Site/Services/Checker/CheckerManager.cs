using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Services.Checker;

public interface ICheckerManager
{
    IReadOnlyDictionary<Guid, ICheckerService> Checkers { get; }
    void Register(IRunnerService runner, CancellationToken token);
    void Unregister(IRunnerService runner);
    Task<int> AddCompilators(Guid checkerId, CancellationToken token);
    Task<int> DisableCompilatorsWithoutRunner(CancellationToken token);
}

public class CheckerManager(IServiceProvider provider, ILogger<CheckerManager> logger) : ICheckerManager
{
    private readonly IServiceProvider _provider = provider;
    private readonly ILogger _logger = logger;
    private readonly ConcurrentDictionary<Guid, ICheckerService> _checkers = new();
    private readonly SemaphoreSlim _synchronizationSemaphore = new(1);

    public IReadOnlyDictionary<Guid, ICheckerService> Checkers => _checkers.AsReadOnly();

    public void Register(IRunnerService runner, CancellationToken token)
    {
        var checker = ActivatorUtilities.CreateInstance<CheckerService>(_provider, runner);
        if (!_checkers.TryAdd(runner.Id, checker))
            throw new InvalidOperationException($"Runner {runner.Id} already registered");

        _logger.LogInformation("Registered runner {id}: {name}", runner.Id, runner.Name);
        Task.Run(() => Process(checker, token), token);
    }

    public void Unregister(IRunnerService runner)
    {
        _logger.LogInformation("Unregistered runner {id}: {name}", runner.Id, runner.Name);
        _checkers.TryRemove(runner.Id, out _);
    }

    public async Task<int> AddCompilators(Guid checkerId, CancellationToken token)
    {
        await _synchronizationSemaphore.WaitAsync(token);
        try
        {
            if (!_checkers.TryGetValue(checkerId, out var checker))
                throw new InvalidOperationException("The selected runner is no longer connected.");
            if (checker.SelfTestingStatus != CheckerSelfTestingStatus.Success)
                throw new InvalidOperationException("The selected runner has not passed self-testing.");

            await using var scope = _provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var records = await context.Compilators.AsTracking().ToListAsync(token);
            foreach (var info in checker.Compilators!)
            {
                var configuration = info.Configuration;
                var record = records.SingleOrDefault(x => x.Name == configuration.Name && x.Description == info.DisplayName);
                if (record is null)
                {
                    record = new Compilator
                    {
                        Name = configuration.Name,
                        Description = info.DisplayName,
                        Language = configuration.Language,
                        ConfigName = configuration.ConfigName,
                        SourceExtension = configuration.SourceExtension,
                        CommandLine = configuration.CompileCommand,
                        Submissions = []
                    };
                    context.Compilators.Add(record);
                }

                record.IsActive = true;
            }

            return await context.SaveChangesAsync(token);
        }
        finally
        {
            _synchronizationSemaphore.Release();
        }
    }

    public async Task<int> DisableCompilatorsWithoutRunner(CancellationToken token)
    {
        await _synchronizationSemaphore.WaitAsync(token);
        try
        {
            var checkers = _checkers.Values.Select(x => (x.SelfTestingStatus, x.Compilators)).ToArray();
            if (checkers.Any(x => x.SelfTestingStatus is CheckerSelfTestingStatus.Unknown or CheckerSelfTestingStatus.InProcess))
                throw new InvalidOperationException("Wait for runner self-testing to finish.");

            var available = checkers.Where(x => x.SelfTestingStatus == CheckerSelfTestingStatus.Success)
                .SelectMany(x => x.Compilators!)
                .Select(x => (x.Configuration.Name, x.DisplayName)).ToHashSet();
            await using var scope = _provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var records = await context.Compilators.AsTracking().Where(x => x.IsActive).ToListAsync(token);
            foreach (var record in records)
                if (record.Description is null || !available.Contains((record.Name, record.Description)))
                    record.IsActive = false;

            return await context.SaveChangesAsync(token);
        }
        finally
        {
            _synchronizationSemaphore.Release();
        }
    }

    private async Task Process(CheckerService checker, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await checker.LoadCompilators(token);
                if (!await checker.RunSelfTests(token))
                {
                    _logger.LogWarning("Checker {id} self tests failed", checker.Id);
                    return;
                }

                _logger.LogInformation("Checker {id} self tests complete. Start checking submissions", checker.Id);
                while (!token.IsCancellationRequested)
                {
                    if (await checker.TryCheckFromDB(token))
                        continue;

                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in checker {id}", checker.Id);
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
    }
}
