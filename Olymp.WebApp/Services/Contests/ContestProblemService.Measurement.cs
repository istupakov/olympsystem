using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Services.Contests;

public sealed record LimitMeasurement(string File, string Compiler, int TimeLimit, CheckerResult? Result, string? Error);

public sealed partial class ContestProblemService
{
    public async Task MeasureLimitsAsync(ClaimsPrincipal actor, int problemId, Func<LimitMeasurement, Task> resultAvailable,
        CancellationToken token = default)
    {
        if (!(await authorization.AuthorizeAsync(actor, null, PolicyNames.JuryOrAdmin)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
        var problem = await context.Problems.AsNoTracking().Include(problem => problem.Tests.Where(test => test.IsActive))
            .SingleOrDefaultAsync(problem => problem.Id == problemId, token) ?? throw new EntityNotFoundException();
        var references = await context.Submissions.AsNoTracking().Where(submission => submission.ProblemId == problemId
                && submission.IsReference && (submission.Description == null || submission.Description != ReferenceSolutionFiles.ValidatorFilename))
            .Include(submission => submission.Compilator).OrderBy(submission => submission.Id).ToListAsync(token);
        if (references.Count == 0) throw new OperationValidationException(SiteResources.ImportNoSolutions);
        if (problem.Tests.Count == 0) throw new OperationValidationException(SiteResources.StaffNoTests);
        var tests = problem.Tests.OrderBy(test => test.Number).Select(test => new CheckTest(test.Number, test.Input, test.Output, test.Score)).ToArray();
        var configurations = ContestImportService.AvailableConfigurations(checkerManager);
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            if (!(await authorization.AuthorizeAsync(actor, null, PolicyNames.JuryOrAdmin)).Succeeded)
                throw new OperationValidationException(SiteResources.StaffAccessDenied);
            string filename = ReferenceSolutionFiles.Filename(reference.Id, reference.Description, reference.Compilator.SourceExtension);
            var matching = configurations.Where(configuration => reference.Compilator.IsActive
                && CompilerMatching.Matches(reference.Compilator, configuration.Info)).ToArray();
            if (matching.Length == 0)
            {
                await resultAvailable(new(filename, reference.Compilator.Description ?? reference.Compilator.Name,
                    problem.TimeLimit, null, SiteResources.ImportCompilerRunnerUnavailable));
                continue;
            }
            foreach (var (runner, info) in matching)
            {
                int limit = info.Configuration.UseSlowTimeLimit ? problem.SlowTimeLimit : problem.TimeLimit;
                try
                {
                    var result = await runner.Check(new(Guid.NewGuid().ToString("N"), reference.Text, info, problem.CheckerId,
                        tests, TimeSpan.FromSeconds((double)limit * 5), problem.MemoryLimit, AllTests: false), token);
                    await resultAvailable(new(filename, info.FullName, limit, result, null));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    await resultAvailable(new(filename, info.FullName, limit, null, SiteResources.StaffRunnerDisconnected));
                }
            }
        }
    }
}
