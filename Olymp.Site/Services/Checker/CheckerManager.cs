using System.Collections.Concurrent;

namespace Olymp.Site.Services.Checker;

public interface ICheckerManager
{
    IReadOnlyDictionary<Guid, ICheckerService> Checkers { get; }
    void Register(IRunnerService runner, CancellationToken token);
    void Unregister(IRunnerService runner);
}

public class CheckerManager(IServiceProvider provider, ILogger<CheckerManager> logger) : ICheckerManager
{
    private readonly IServiceProvider _provider = provider;
    private readonly ILogger _logger = logger;
    private readonly ConcurrentDictionary<Guid, ICheckerService> _checkers = new();

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

    private async Task Process(CheckerService checker, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
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
