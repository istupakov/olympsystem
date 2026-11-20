using System.Collections.ObjectModel;
using System.Data;
using System.IO.Compression;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Services.Contests;

public sealed partial class ContestProblemService(OlympContext context, ICheckerManager checkerManager,
    IOptions<ContestImportOptions> options, IAuthorizationService authorization, TimeProvider timeProvider)
{
    public const int LegacyCleanupContestId = 1;
    public static ReadOnlyCollection<int> LegacyCleanupProblemIds { get; } = Array.AsReadOnly<int>([3554, 3582]);

    public async Task CleanupLegacyProblemsAsync(ClaimsPrincipal actor, CancellationToken token = default)
    {
        if (!(await authorization.AuthorizeAsync(actor, null, PolicyNames.Admin)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);

        using var gate = await checkerManager.EnterAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        int[] ids = [.. LegacyCleanupProblemIds];
        var problems = context.Problems.Where(item => ids.Contains(item.Id));
        if (await problems.AnyAsync(item => item.ContestId != LegacyCleanupContestId, token))
            throw new OperationValidationException(SiteResources.StaffLegacyProblemsMoved);
        var blocked = await problems.Where(item => item.Submissions.Any() || item.Messages.Any())
            .OrderBy(item => item.Id).Select(item => item.Id).ToListAsync(token);
        if (blocked.Count != 0)
            throw new OperationValidationException(string.Format(SiteResources.StaffLegacyProblemsHaveHistory, string.Join(", ", blocked)));

        await problems.ExecuteDeleteAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task AuthorizeAsync(ClaimsPrincipal actor, int contestId)
    {
        if (!(await authorization.AuthorizeAsync(actor, contestId, PolicyNames.ManageContest)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
    }

    public async Task SetProblemActiveAsync(ClaimsPrincipal actor, int contestId, int problemId, bool active, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        using var gate = await checkerManager.EnterAsync(token);
        int changed = await context.Problems.Where(item => item.Id == problemId && item.ContestId == contestId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, active), token);
        if (changed == 0) throw new EntityNotFoundException();
    }

    public async Task SetTestActiveAsync(ClaimsPrincipal actor, int problemId, int testId, bool active, CancellationToken token = default)
    {
        var contestId = await context.Problems.Where(item => item.Id == problemId).Select(item => (int?)item.ContestId).SingleOrDefaultAsync(token)
            ?? throw new EntityNotFoundException();
        await AuthorizeAsync(actor, contestId);
        using var gate = await checkerManager.EnterAsync(token);
        int changed = await context.Tests.Where(item => item.Id == testId && item.ProblemId == problemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, active), token);
        if (changed == 0)
            throw new EntityNotFoundException();
    }

    public const string BusyMessage = "A related submission is still being checked. Please try again after checking finishes.";

    public async Task UploadPdfAsync(ClaimsPrincipal actor, int contestId, byte[] data, bool replace, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var contest = await context.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == contestId, token)
            ?? throw new EntityNotFoundException();
        if (contest.ProblemPdfId is not null && !replace)
            throw new OperationValidationException(SiteResources.StaffReplacePdfConfirm);
        await UploadPdfAsync(contestId, data, token);
        await transaction.CommitAsync(token);
    }

    internal async Task UploadPdfAsync(int contestId, byte[] data, CancellationToken token = default)
    {
        if (data.LongLength == 0 || data.LongLength > options.Value.MaxPdfBytes)
            throw new InvalidDataException($"Invalid file size. Maximum: {options.Value.MaxPdfBytes / 1024 / 1024} MiB.");
        if (!data.AsSpan().StartsWith("%PDF-"u8))
            throw new InvalidDataException("The file is not a PDF.");

        var contest = await context.Contests.Include(x => x.ProblemPdf).AsTracking()
            .SingleAsync(x => x.Id == contestId, token);
        if (contest.ProblemPdf is null)
            contest.ProblemPdf = new Blob { Data = data };
        else
            contest.ProblemPdf.Data = data;

        await context.SaveChangesAsync(token);
    }

    public async Task RemovePdfAsync(int contestId, CancellationToken token = default)
    {
        var contest = await context.Contests.Include(x => x.ProblemPdf).AsTracking()
            .SingleAsync(x => x.Id == contestId, token);
        if (contest.ProblemPdf is not { } pdf)
            return;

        contest.ProblemPdf = null;
        context.Blobs.Remove(pdf);
        await context.SaveChangesAsync(token);
    }

    public async Task<int> RetestAsync(int contestId, int problemId, bool exceptAccepted, CancellationToken token = default)
    {
        using var gate = await checkerManager.EnterAsync(token);
        var problem = await context.Problems.SingleOrDefaultAsync(x => x.Id == problemId && x.ContestId == contestId, token)
            ?? throw new InvalidDataException("The problem was not found in this contest.");
        if (!problem.IsActive)
            throw new InvalidDataException("The problem must be active for retesting.");
        await EnsureIdleAsync([problemId], token);
        return await context.Submissions
            .Where(x => x.ProblemId == problemId && x.StatusCode != 0 && x.StatusCode != 1
                && (x.Description == null || x.Description != ReferenceSolutionFiles.ValidatorFilename)
                && (!exceptAccepted || x.StatusCode != 2))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.StatusCode, 0)
                .SetProperty(x => x.Score, (int?)null).SetProperty(x => x.ScoresByTest, (string?)null)
                .SetProperty(x => x.LastModification, DateTimeOffset.Now), token);
    }

    public async Task<int> RetestCompilationErrorsAsync(int contestId, CancellationToken token = default)
    {
        using var gate = await checkerManager.EnterAsync(token);
        var problemIds = await context.Problems.Where(x => x.ContestId == contestId && x.IsActive)
            .Select(x => x.Id).ToArrayAsync(token);
        await EnsureIdleAsync(problemIds, token);
        var status = CheckerResultStatus.CompilationError.MakeStatusCode();
        return await context.Submissions.Where(x => problemIds.Contains(x.ProblemId) && x.StatusCode == status
                && (x.Description == null || x.Description != ReferenceSolutionFiles.ValidatorFilename))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.StatusCode, 0)
                .SetProperty(x => x.Score, (int?)null).SetProperty(x => x.ScoresByTest, (string?)null)
                .SetProperty(x => x.LastModification, DateTimeOffset.Now), token);
    }

    private async Task EnsureIdleAsync(int[] problemIds, CancellationToken token)
    {
        if (await context.Submissions.AnyAsync(x => problemIds.Contains(x.ProblemId) && x.StatusCode == 1
            && (x.Description == null || x.Description != ReferenceSolutionFiles.ValidatorFilename), token))
            throw new InvalidDataException(BusyMessage);
    }

    public async Task PublishTestZipAsync(ClaimsPrincipal actor, int contestId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        var end = await context.Contests.Where(item => item.Id == contestId).Select(item => (DateTimeOffset?)item.EndTime).SingleOrDefaultAsync(token)
            ?? throw new EntityNotFoundException();
        if (end >= timeProvider.GetUtcNow()) throw new OperationValidationException(SiteResources.StaffContestMustEnd);
        await GenerateTestZipAsync(contestId, token);
        await transaction.CommitAsync(token);
    }

    public async Task RemoveTestZipAsync(ClaimsPrincipal actor, int contestId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        var contest = await context.Contests.AsTracking().Include(item => item.TestZip).SingleOrDefaultAsync(item => item.Id == contestId, token)
            ?? throw new EntityNotFoundException();
        if (contest.TestZip is { } blob)
        {
            contest.TestZip = null;
            context.Blobs.Remove(blob);
            await context.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    internal async Task GenerateTestZipAsync(int contestId, CancellationToken token = default)
    {
        using var gate = await checkerManager.EnterAsync(token);
        var contest = await context.Contests.Include(x => x.TestZip).AsTracking().SingleAsync(x => x.Id == contestId, token);
        var problems = await context.Problems.Where(x => x.ContestId == contestId && x.IsActive)
            .Include(x => x.Tests.Where(t => t.IsActive)).OrderBy(x => x.Number).ToListAsync(token);
        if (!problems.Any(x => x.Tests.Count != 0))
            throw new InvalidDataException("The contest has no active tests.");
        long size = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var problem in problems)
                foreach (var test in problem.Tests.OrderBy(x => x.Number))
                    foreach (var (prefix, bytes) in new[] { ("input", test.Input), ("output", test.Output) })
                    {
                        size += bytes.LongLength;
                        if (size > options.Value.MaxExpandedBytes)
                            throw new InvalidDataException("The test archive exceeds the expanded size limit.");
                        var path = $"{SafeSegment("Test" + contest.Abbr)}/{SafeSegment(problem.NameWithNumber)}/{prefix}{test.Number:000}.txt";
                        if (!names.Add(path))
                            throw new InvalidDataException($"Duplicate test path: {path}");
                        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                        await using var output = entry.Open();
                        await output.WriteAsync(bytes, token);
                    }
        }
        if (memory.Length > options.Value.MaxZipBytes)
            throw new InvalidDataException("The tests ZIP exceeds the size limit.");
        contest.TestZip ??= new Blob { Data = [] };
        contest.TestZip.Data = memory.ToArray();
        await context.SaveChangesAsync(token);
    }

    private static string SafeSegment(string name) =>
        string.Concat(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).TrimEnd(' ', '.');
}
