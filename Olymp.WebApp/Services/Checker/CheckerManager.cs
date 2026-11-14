using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Checker.Services;
using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Services.Checker;

public interface ICheckerManager
{
    IReadOnlyDictionary<Guid, ICheckerService> Checkers { get; }
    void Register(IRunnerService runner, CancellationToken token);
    void Unregister(IRunnerService runner);
    Task<IDisposable> EnterAsync(CancellationToken token = default);
    Task<bool> TryCheckFromDB(ICheckerService checker, CancellationToken token);
    Task<int> AddCompilatorAsync(Guid runnerId, string name, string description, CompilerConfigurationKey? expected = null, CancellationToken token = default);
    Task SetCompilatorActiveAsync(int id, bool active, CancellationToken token = default);
    Task<int?> QueueRetestAsync(IQueryable<Submission> selection, IReadOnlyCollection<int> confirmedAccepted, CancellationToken token = default);
}

public sealed class CheckerManager(IServiceProvider provider, IDbContextFactory<OlympContext> contextFactory,
    ILogger<CheckerManager> logger) : ICheckerManager
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
        Register(checker, token);
    }

    internal void Register(ICheckerService checker, CancellationToken token)
    {
        if (!_checkers.TryAdd(checker.Id, checker))
            throw new InvalidOperationException($"Runner {checker.Id} already registered");

        logger.LogInformation("Registered runner {id}: {name}", checker.Id, checker.Name);
        _ = Task.Run(() => Process(checker, token), token);
    }

    public void Unregister(IRunnerService runner)
    {
        logger.LogInformation("Unregistered runner {id}: {name}", runner.Id, runner.Name);
        _checkers.TryRemove(runner.Id, out _);
    }

    private static Compilator CreateCompilator(CompilatorInfo info) => new()
    {
        Name = info.Configuration.Name,
        Description = info.DisplayName,
        Language = info.Configuration.Language,
        ConfigName = info.Configuration.ConfigName,
        SourceExtension = info.Configuration.SourceExtension,
        CommandLine = info.Configuration.CompileCommand,
        Submissions = []
    };

    public async Task<int> AddCompilatorAsync(Guid runnerId, string name, string description, CompilerConfigurationKey? expected = null, CancellationToken token = default)
    {
        using var gate = await EnterAsync(token);
        if (!_checkers.TryGetValue(runnerId, out var runner) || runner.SelfTestingStatus != CheckerSelfTestingStatus.Success)
            throw new InvalidOperationException("The selected runner has not passed self-testing.");
        var info = runner.Compilators?.SingleOrDefault(item => item.Configuration.Name == name
            && item.DisplayName == description && item.Error is null)
            ?? throw new InvalidOperationException("The selected compilator is unavailable on this runner.");
        if (expected is not null && expected != CompilerConfigurationKey.From(info))
            throw new InvalidOperationException("The selected configuration has changed. Refresh the preview.");

        await using var db = await contextFactory.CreateDbContextAsync(token);
        var candidates = await db.Compilators.Where(item => item.Name == name).ToListAsync(token);
        var existing = candidates.FirstOrDefault(item => CompilerMatching.Matches(item, info));
        if (existing is not null)
            return existing.Id;

        var compiler = CreateCompilator(info);
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(token);
        return compiler.Id;
    }

    public async Task SetCompilatorActiveAsync(int id, bool active, CancellationToken token = default)
    {
        using var gate = await EnterAsync(token);
        await using var db = await contextFactory.CreateDbContextAsync(token);
        if (await db.Compilators.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, active), token) == 0)
            throw new InvalidOperationException("The selected compilator is unavailable on this runner.");
    }

    public async Task<int?> QueueRetestAsync(IQueryable<Submission> selection, IReadOnlyCollection<int> confirmedAccepted, CancellationToken token = default)
    {
        using var gate = await EnterAsync(token);
        var eligible = selection.Where(item => item.Problem.IsActive && item.StatusCode != 0 && item.StatusCode != 1);
        if (await eligible.AnyAsync(item => item.StatusCode == 2 && !confirmedAccepted.Contains(item.Id), token))
            return null;
        return await eligible.Where(item => item.StatusCode != 2 || confirmedAccepted.Contains(item.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StatusCode, 0)
            .SetProperty(item => item.Score, (int?)null).SetProperty(item => item.ScoresByTest, (string?)null)
            .SetProperty(item => item.LastModification, DateTimeOffset.UtcNow), token);
    }

    public async Task<bool> TryCheckFromDB(ICheckerService checker, CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);

        var available = checker.Compilators!
            .Where(x => x.Error is null).Select(CompilerConfigurationKey.From).ToHashSet();
        var records = await context.Compilators.Where(x => x.IsActive)
            .AsNoTracking().ToListAsync(token);
        var compilatorIds = records
            .Where(x => available.Contains(CompilerConfigurationKey.From(x)))
            .Select(x => x.Id).ToArray();
        if (compilatorIds.Length == 0)
            return false;

        Submission? submission;
        using (await EnterAsync(token))
        {
            var span = DateTimeOffset.Now - TimeSpan.FromMinutes(2);
            var staffIds = context.UserRoles.Where(assignment => context.Roles.Any(role => role.Id == assignment.RoleId
                && (role.Name == RoleNames.Jury || role.Name == RoleNames.Admin))).Select(assignment => assignment.UserId);
            submission = await context.Submissions
                .Where(x => x.StatusCode == 0 || (x.StatusCode == 1 && x.LastModification < span))
                .Where(x => (x.Problem.IsActive || staffIds.Contains(x.UserId)) && compilatorIds.Contains(x.CompilatorId))
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
        var compilator = checker.Compilators!.SingleOrDefault(x => CompilerMatching.Matches(submission.Compilator, x))
            ?? throw new NotSupportedException($"Compilator {submission.Compilator.Id} ({submission.Compilator.Name}) has no matching configuration on runner {checker.Id}");
        var problem = submission.Problem;
        var timeLimit = TimeSpan.FromSeconds(compilator.Configuration.UseSlowTimeLimit
            ? problem.SlowTimeLimit : problem.TimeLimit);
        return new CheckRequest(submission.Id.ToString(), submission.Text,
            compilator, problem.CheckerId,
            [.. problem.Tests.Select(x => new CheckTest(x.Number, x.Input, x.Output, x.Score))],
            timeLimit, problem.MemoryLimit, problem.Contest.IsSchool);
    }

    private async Task Process(ICheckerService checker, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
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
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in checker {id}", checker.Id);
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
    }
}
