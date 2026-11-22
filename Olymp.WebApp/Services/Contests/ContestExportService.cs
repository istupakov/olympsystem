using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Statements;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed partial class ContestExportService(IDbContextFactory<OlympContext> factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions JsonLineOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record ExportResult(FileStream Stream, string FileName);

    // Ownership of the stream passes to the caller. Closing it also deletes the temporary archive.
    public Task<ExportResult> ExportAsync(int? contestId, CancellationToken token = default)
        => ExportAsync(contestId, new ContestExportOptions(), token);

    public Task<ExportResult> ExportAsync(int? contestId, ContestExportOptions options, CancellationToken token = default)
        => ExportCoreAsync(contestId, false, options, token);

    public Task<ExportResult> ExportProblemsAsync(int contestId, CancellationToken token = default)
        => ExportCoreAsync(contestId, true, new(Tests: true, ReferenceSolutions: true, Markdown: true), token);

    private async Task<ExportResult> ExportCoreAsync(int? contestId, bool problemsOnly, ContestExportOptions options, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var contests = await db.Contests.AsNoTracking().Where(item => contestId == null || item.Id == contestId)
            .OrderBy(item => item.Id).ToListAsync(token);
        if (contestId is not null && contests.Count == 0) throw new EntityNotFoundException();
        await ValidatePathsAsync(db, contests, contestId, token);

        var file = new FileStream(Path.Combine(Path.GetTempPath(), $"olymp-export-{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var contest in contests)
                    if (problemsOnly)
                    {
                        var problemIds = await db.Problems.Where(problem => problem.ContestId == contest.Id)
                            .OrderBy(problem => problem.Number).ThenBy(problem => problem.Id).Select(problem => problem.Id).ToListAsync(token);
                        if (problemIds.Count == 0) throw new OperationValidationException(SiteResources.NoProblemsFound);
                        foreach (int id in problemIds) await WriteProblemAsync(db, archive, contest, id, true, options, token);
                    }
                    else await WriteContestAsync(db, archive, contest, options, token);
            await transaction.CommitAsync(token);
            file.Position = 0;
            return new(file, problemsOnly ? $"{contests.Single().Abbr}-problems.zip"
                : contestId is not null ? $"{contests.Single().Abbr}-export.zip" : "contests-export.zip");
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private static async Task ValidatePathsAsync(OlympContext db, List<Contest> contests, int? contestId, CancellationToken token)
    {
        var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var contest in contests)
        {
            string name = contest.Abbr;
            string stem = name.Split('.')[0];
            bool reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
            if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ')
                || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) || reserved)
                throw InvalidPath(name, $"contestId={contest.Id}");
            if (!folders.TryAdd(name, contest.Id))
                throw InvalidPath(name, $"contestId={folders[name]}, contestId={contest.Id}");
        }

        var problems = await db.Problems.AsNoTracking().Where(item => contestId == null || item.ContestId == contestId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.Number, item.ContestId }).ToListAsync(token);
        var numbers = new Dictionary<(int ContestId, int Number), int>();
        foreach (var problem in problems)
        {
            var key = (problem.ContestId, problem.Number);
            string path = $"{contests.Single(item => item.Id == problem.ContestId).Abbr}/problem{problem.Number.ToString("D2", CultureInfo.InvariantCulture)}";
            if (problem.Number < 0) throw InvalidPath(path, $"contestId={problem.ContestId}, problemId={problem.Id}");
            if (!numbers.TryAdd(key, problem.Id))
                throw InvalidPath(path, $"contestId={problem.ContestId}, problemId={numbers[key]}, problemId={problem.Id}");
        }

        var tests = new Dictionary<(int ProblemId, int Number), int>();
        await foreach (var test in db.Tests.AsNoTracking().Where(item => contestId == null || item.Problem.ContestId == contestId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.Number, item.ProblemId, item.Problem.ContestId })
            .AsAsyncEnumerable().WithCancellation(token))
        {
            var key = (test.ProblemId, test.Number);
            if (test.Number <= 0) throw InvalidPath($"input{test.Number}.txt", $"contestId={test.ContestId}, problemId={test.ProblemId}, testId={test.Id}");
            if (!tests.TryAdd(key, test.Id))
                throw InvalidPath($"input{test.Number}.txt", $"contestId={test.ContestId}, problemId={test.ProblemId}, testId={tests[key]}, testId={test.Id}");
        }
    }

    private static OperationValidationException InvalidPath(string path, string records) =>
        new(string.Format(CultureInfo.CurrentCulture, SiteResources.StaffExportInvalidPath, path, records));

    private static async Task WriteContestAsync(OlympContext db, ZipArchive archive, Contest contest, ContestExportOptions options, CancellationToken token)
    {
        string root = contest.Abbr + "/";
        var checkers = await db.Checkers.AsNoTracking().Where(item => item.Problems.Any(problem => problem.ContestId == contest.Id))
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.Description }).ToListAsync(token);
        await WriteJsonAsync(archive, root + "contest.json", new
        {
            FormatVersion = 1,
            contest.Id,
            contest.Name,
            contest.Abbr,
            contest.StartTime,
            contest.EndTime,
            contest.ProblemShowTime,
            contest.FreezeTime,
            contest.IsOpen,
            contest.IsHidden,
            Checkers = checkers
        }, token);
        if (contest.Description is not null) await WriteTextAsync(archive, root + "description.html", contest.Description, token);
        foreach (var (id, path, table) in new[]
        {
            (contest.ProblemPdfId, "statements.pdf", false), (contest.TestZipId, "published-tests.zip", false),
            (contest.FinalTableId, "tables/final.html", true), (contest.OfficialTableId, "tables/official.html", true)
        })
        {
            if (id is null || (table && !options.Tables) || (path == "statements.pdf" && !options.Pdf) || (path == "published-tests.zip" && !options.TestZip)) continue;
            byte[] data = await db.Blobs.Where(item => item.Id == id).Select(item => item.Data).SingleAsync(token);
            if (table) await WriteTextAsync(archive, root + path, Encoding.GetEncoding(1251).GetString(data), token);
            else await WriteBytesAsync(archive, root + path, data, token);
        }
        await WriteRecordsAsync(db, archive, contest, options, token);
        var problemIds = await db.Problems.Where(item => item.ContestId == contest.Id).OrderBy(item => item.Id).Select(item => item.Id).ToListAsync(token);
        foreach (int id in problemIds)
            await WriteProblemAsync(db, archive, contest, id, false, options, token);
        if (options.Tex) await WriteTexAsync(db, archive, contest, token);
    }

    private static async Task WriteJsonAsync<T>(ZipArchive archive, string path, T value, CancellationToken token)
    {
        await using var stream = archive.CreateEntry(path).Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
        await stream.WriteAsync("\n"u8.ToArray(), token);
    }

    private static async Task WriteJsonLinesAsync<T>(ZipArchive archive, string path, IAsyncEnumerable<T> records, CancellationToken token)
    {
        await using var stream = archive.CreateEntry(path).Open();
        await foreach (var record in records.WithCancellation(token))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonLineOptions, token);
            await stream.WriteAsync("\n"u8.ToArray(), token);
        }
    }

    private static Task WriteTextAsync(ZipArchive archive, string path, string text, CancellationToken token) =>
        WriteBytesAsync(archive, path, Encoding.UTF8.GetBytes(text), token);

    private static async Task WriteBytesAsync(ZipArchive archive, string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = archive.CreateEntry(path).Open();
        await stream.WriteAsync(bytes, token);
    }

    private static async Task WriteRecordsAsync(OlympContext db, ZipArchive archive, Contest contest, ContestExportOptions options, CancellationToken token)
    {
        string root = contest.Abbr + "/";
        var participants = db.Competitors.AsNoTracking().Where(item => item.ContestId == contest.Id && !item.IsHidden);
        if (options.Participants) await WriteJsonLinesAsync(archive, root + "participants.jsonl", participants.OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Name, item.IsApproved, item.IsOutOfCompetition, item.IsDisqualified })
            .AsAsyncEnumerable(), token);
        if (!options.ResultsAndMessages) return;
        await WriteJsonLinesAsync(archive, root + "messages.jsonl", db.Messages.AsNoTracking()
            .Where(item => item.ContestId == contest.Id && (item.UserId == null || participants.Any(participant => participant.Id == item.UserId)))
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.UserId, item.ProblemId, item.SendTime, item.UserText, item.JuryText })
            .AsAsyncEnumerable(), token);
        await WriteJsonLinesAsync(archive, root + "results.jsonl", db.Submissions.AsNoTracking()
            .Where(item => item.Problem.ContestId == contest.Id && !item.IsHidden
                && participants.Any(participant => participant.Id == item.UserId)
                && item.CommitTime >= contest.StartTime && item.CommitTime <= contest.EndTime)
            .OrderBy(item => item.Id).Select(item => new { SubmissionId = item.Id, item.ProblemId, item.UserId, item.CommitTime, item.StatusCode, item.Score })
            .AsAsyncEnumerable(), token);
    }

    private static async Task WriteProblemAsync(OlympContext db, ZipArchive archive, Contest contest, int problemId, bool problemsOnly, ContestExportOptions options, CancellationToken token)
    {
        var problem = await db.Problems.AsNoTracking().SingleAsync(item => item.Id == problemId, token);
        string root = $"{(problemsOnly ? "" : contest.Abbr + "/")}problem{problem.Number.ToString("D2", CultureInfo.InvariantCulture)}/";
        var tests = await db.Tests.AsNoTracking().Where(item => item.ProblemId == problemId).OrderBy(item => item.Number)
            .Select(item => new { item.Number, item.IsOpen, item.IsActive, item.Score, item.Comment }).ToListAsync(token);
        int openTestCount = tests.TakeWhile(item => item.IsOpen).Count();
        var openTests = tests.Where(item => item.IsOpen).Select(item => item.Number).ToArray();
        if (problemsOnly && openTests.Length != openTestCount)
            throw new OperationValidationException(string.Format(CultureInfo.CurrentCulture, SiteResources.ExportProblemsOpenTests, problem.Name));
        if (problemsOnly && tests.Any(test => test.Number is < 1 or > 999))
            throw new OperationValidationException(string.Format(CultureInfo.CurrentCulture, SiteResources.ExportProblemsTestNumbers, problem.Name));
        var scores = tests.Where(item => item.Score is not null).ToDictionary(item => item.Number, item => item.Score!.Value);
        var inactive = tests.Where(item => !item.IsActive).Select(item => item.Number).ToArray();
        var comments = tests.Where(item => !string.IsNullOrEmpty(item.Comment)).ToDictionary(item => item.Number, item => item.Comment!);
        await WriteJsonAsync(archive, root + "problem.json", new ExportedProblem(
            problem.Id, problem.Number, problem.Name, problem.TimeLimit, problem.SlowTimeLimit, problem.MemoryLimit, problem.CheckerId,
            problem.IsActive, openTestCount, openTests.Length == openTestCount ? null : openTests,
            scores.Count == 0 ? null : scores, inactive.Length == 0 ? null : inactive, comments.Count == 0 ? null : comments), token);
        if (options.Markdown && problem.Text is not null)
            await WriteTextAsync(archive, root + "statement.md", problem.Text, token);
        if (options.Tests) await foreach (var test in db.Tests.AsNoTracking().Where(item => item.ProblemId == problemId).OrderBy(item => item.Number)
            .Select(item => new { item.Number, item.Input, item.Output }).AsAsyncEnumerable().WithCancellation(token))
        {
            string number = test.Number.ToString("D3", CultureInfo.InvariantCulture);
            await WriteBytesAsync(archive, root + $"tests/input{number}.txt", test.Input, token);
            await WriteBytesAsync(archive, root + $"tests/output{number}.txt", test.Output, token);
        }
        if (options.JurySolutions) await WriteJurySolutionsAsync(db, archive, contest, problemId, root, token);
        if (options.ReferenceSolutions) await WriteReferenceSolutionsAsync(db, archive, problemId, root, token);
    }

    private static async Task WriteReferenceSolutionsAsync(OlympContext db, ZipArchive archive, int problemId, string root, CancellationToken token)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasValidator = false;
        await foreach (var submission in db.Submissions.AsNoTracking().Where(s => s.ProblemId == problemId && s.IsReference)
            .OrderBy(s => s.Id).Select(s => new { s.Id, s.Description, s.SourceCode, s.Compilator.SourceExtension })
            .AsAsyncEnumerable().WithCancellation(token))
        {
            string path = ReferenceSolutionFiles.Filename(submission.Id, submission.Description, submission.SourceExtension);
            if (ReferenceSolutionFiles.IsValidator(path))
            {
                if (hasValidator) throw InvalidPath(path, $"problemId={problemId}, submissionId={submission.Id}");
                hasValidator = true;
                await WriteTextAsync(archive, root + ReferenceSolutionFiles.ValidatorFilename,
                    ReferenceSolutionFiles.SourceEncoding.GetString(submission.SourceCode), token);
                continue;
            }
            if (!paths.Add(path))
            {
                path = $"submission-{submission.Id}/{path}";
                if (!paths.Add(path)) throw InvalidPath(path, $"problemId={problemId}, submissionId={submission.Id}");
            }
            await WriteTextAsync(archive, root + "solutions/" + path, ReferenceSolutionFiles.SourceEncoding.GetString(submission.SourceCode), token);
        }
    }

    private static async Task WriteJurySolutionsAsync(OlympContext db, ZipArchive archive, Contest contest, int problemId, string root, CancellationToken token)
    {
        var solutions = new SortedDictionary<string, List<JuryAttempt>>(StringComparer.Ordinal);
        await foreach (var submission in db.Submissions.AsNoTracking().Where(item => item.ProblemId == problemId &&
                (db.UserRoles.Any(ur => ur.UserId == item.UserId && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == RoleNames.Admin))
                || (item.User.IsHidden && item.User.Submissions.Any(s => s.Problem.ContestId == contest.Id && s.CommitTime < contest.StartTime))))
            .OrderBy(item => item.Id).Select(item => new
            {
                item.Id,
                item.SourceCode,
                AuthorName = item.User.Name,
                item.Compilator.Language,
                Compiler = item.Compilator.Name,
                item.Compilator.SourceExtension,
                item.CommitTime,
                item.StatusCode,
                item.Score,
                item.IsReference
            }).AsAsyncEnumerable().WithCancellation(token))
        {
            string file = Convert.ToHexStringLower(SHA256.HashData(submission.SourceCode)) + SafeExtension(submission.SourceExtension);
            if (!solutions.TryGetValue(file, out var attempts))
            {
                attempts = [];
                solutions.Add(file, attempts);
                await WriteBytesAsync(archive, root + "jury-solutions/" + file, submission.SourceCode, token);
            }
            attempts.Add(new(submission.Id, submission.AuthorName, submission.Language, submission.Compiler,
                (submission.CommitTime - contest.StartTime).Ticks / (decimal)TimeSpan.TicksPerSecond,
                submission.StatusCode, submission.Score, submission.IsReference ? true : null));
        }
        if (solutions.Count == 0) return;
        await WriteJsonLinesAsync(archive, root + "jury-solutions/solutions.jsonl", Records(), token);
        async IAsyncEnumerable<object> Records()
        {
            foreach (var (file, attempts) in solutions)
                yield return new { File = file, Attempts = attempts };
            await Task.CompletedTask;
        }
    }

    private static string SafeExtension(string extension)
    {
        if (extension.Length < 2 || extension[0] != '.' || extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw InvalidPath(extension, "SourceExtension");
        return extension.ToLowerInvariant();
    }

    private sealed record ExportedProblem(int Id, int Number, string Name, int TimeLimit, int SlowTimeLimit, int MemoryLimit,
        int CheckerId, bool IsActive, int OpenTestCount,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? OpenTests,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<int, int>? Scores,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? InactiveTests,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<int, string>? TestComments);

    private sealed record JuryAttempt(int SubmissionId, string AuthorName, string Language, string Compiler, decimal SecondsFromStart, int StatusCode, int? Score,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsReference);
}
