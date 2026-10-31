using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Checker.Services;
using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Services.Checker;

public interface ICheckerManager
{
    IReadOnlyDictionary<Guid, ICheckerService> Checkers { get; }
    void Register(IRunnerService runner, CancellationToken token);
    void Unregister(IRunnerService runner);
    Task<IDisposable> EnterAsync(CancellationToken token = default);
    Task<bool> TryCheckFromDB(ICheckerService checker, CancellationToken token);
    Task<int> AddCompilators(Guid checkerId, CancellationToken token);
    Task<int> DisableCompilatorsWithoutRunner(CancellationToken token);
}

public sealed class CheckerManager(IServiceProvider provider, ILogger<CheckerManager> logger) : ICheckerManager
{
    private readonly ConcurrentDictionary<Guid, ICheckerService> _checkers = new();
    private readonly SemaphoreSlim _synchronizationSemaphore = new(1);

    public IReadOnlyDictionary<Guid, ICheckerService> Checkers => _checkers.AsReadOnly();

    // Shared by queue operations, problem imports and retests. Released while running user code.
    public async Task<IDisposable> EnterAsync(CancellationToken token = default)
    {
        await _synchronizationSemaphore.WaitAsync(token);
        return new Lease(_synchronizationSemaphore);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    public void Register(IRunnerService runner, CancellationToken token)
    {
        var checker = ActivatorUtilities.CreateInstance<CheckerService>(provider, runner);
        if (!_checkers.TryAdd(runner.Id, checker))
            throw new InvalidOperationException($"Runner {runner.Id} already registered");

        logger.LogInformation("Registered runner {id}: {name}", runner.Id, runner.Name);
        Task.Run(() => Process(checker, token), token);
    }

    public void Unregister(IRunnerService runner)
    {
        logger.LogInformation("Unregistered runner {id}: {name}", runner.Id, runner.Name);
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

            await using var scope = provider.CreateAsyncScope();
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
            await using var scope = provider.CreateAsyncScope();
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

    public async Task<bool> TryCheckFromDB(ICheckerService checker, CancellationToken token)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();

        var available = checker.Compilators!.Where(x => x.Error is null)
            .Select(x => (x.Configuration.Name, x.DisplayName)).ToHashSet();
        var records = await context.Compilators.Where(x => x.IsActive)
            .Select(x => new { x.Id, x.Name, x.Description }).ToListAsync(token);
        var compilatorIds = records
            .Where(x => x.Description is not null && available.Contains((x.Name, x.Description)))
            .Select(x => x.Id).ToArray();
        if (compilatorIds.Length == 0)
            return false;

        Submission? submission;
        using (await EnterAsync(token))
        {
            var span = DateTimeOffset.Now - TimeSpan.FromMinutes(2);
            submission = await context.Submissions
                .Where(x => x.StatusCode == 0 || (x.StatusCode == 1 && x.LastModification < span))
                .Where(x => x.Problem.IsActive && compilatorIds.Contains(x.CompilatorId))
                .Where(x => x.Problem.Tests.Any(test => test.IsActive))
                .OrderBy(x => x.CommitTime)
                .Include(x => x.Compilator)
                .Include(x => x.User)
                .Include(x => x.Problem)
                .ThenInclude(x => x.Tests.Where(x => x.IsActive).OrderBy(x => x.Number))
                .Include(x => x.Problem)
                .ThenInclude(x => x.Contest)
                .AsSplitQuery()
                .FirstOrDefaultAsync(token);

            if (submission is null)
                return false;
            var now = DateTimeOffset.Now;
            var updated = await context.Submissions
                .Where(x => x.Id == submission.Id
                    && (x.StatusCode == 0 || (x.StatusCode == 1 && x.LastModification < span)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.StatusCode, 1)
                    .SetProperty(x => x.LastModification, now), token);
            if (updated == 0)
                return true;

            submission.StatusCode = 1;
            submission.LastModification = now;
            context.Attach(submission);
        }

        await CheckSubmission(context, checker, submission, token);
        return true;
    }

    private async Task CheckSubmission(OlympContext context, ICheckerService checker,
        Submission submission, CancellationToken token)
    {
        try
        {
            logger.LogDebug("Check: submission {id}, problem {problem}, compilator {compilator}, user {user}",
                submission.Id, submission.Problem.Name, submission.Compilator.Name, submission.User.Name);
            var request = CreateCheckRequest(submission, checker);
            var result = await checker.Check(request, token);
            var log = new CheckResult
            {
                Submission = null!,
                SubmissionId = submission.Id,
                CheckTime = DateTimeOffset.Now,
                StatusCode = result.StatusCode,
                Log = result.Log,
                Score = result.Score,
                TestResults = result.TestResults
            };

            context.CheckResults.Add(log);
            submission.StatusCode = log.StatusCode;
            submission.Score = log.Score;
            submission.ScoresByTest = log.TestResults;

            logger.LogDebug("CheckLog for submission {id}: status: {status}, score {score}",
                submission.Id, log.StatusCode, log.Score);
        }
        catch
        {
            submission.StatusCode = 0;
            throw;
        }
        finally
        {
            submission.LastModification = DateTimeOffset.Now;
            using (await EnterAsync(CancellationToken.None))
                await context.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static CheckRequest CreateCheckRequest(Submission submission, ICheckerService checker)
    {
        var compilator = checker.Compilators!.SingleOrDefault(x => x.Configuration.Name == submission.Compilator.Name
            && x.DisplayName == submission.Compilator.Description)
            ?? throw new NotSupportedException($"Compilator {submission.Compilator.Id} ({submission.Compilator.Name}) has no matching configuration on runner {checker.Id}");
        var problem = submission.Problem;
        var timeLimit = TimeSpan.FromSeconds(compilator.Configuration.UseSlowTimeLimit
            ? problem.SlowTimeLimit : problem.TimeLimit);
        return new CheckRequest(submission.Id.ToString(), submission.Text,
            compilator, problem.CheckerId,
            [.. problem.Tests.Select(x => new CheckTest(x.Number, x.Input, x.Output, x.Score))],
            timeLimit, problem.MemoryLimit, problem.Contest.IsSchool);
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
                    logger.LogWarning("Checker {id} self tests failed", checker.Id);
                    return;
                }

                logger.LogInformation("Checker {id} self tests complete. Start checking submissions", checker.Id);
                while (!token.IsCancellationRequested)
                {
                    if (await TryCheckFromDB(checker, token))
                        continue;

                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in checker {id}", checker.Id);
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
    }
}
