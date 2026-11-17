using System.Data.Common;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class ContestExportTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    [Fact]
    public async Task SerializableReadBlocksChangesUntilArchiveIsReadyAndReleasesLocksBeforeDownload()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        string connectionString = scope.ServiceProvider.GetRequiredService<OlympContext>().Database.GetConnectionString()!;
        var probe = new LockProbe(connectionString, contest.Id);
        var options = new DbContextOptionsBuilder<OlympContext>().UseSqlServer(connectionString).AddInterceptors(probe).Options;
        var service = new ContestExportService(new PooledDbContextFactory<OlympContext>(options));
        await using var file = await service.ExportAsync(contest.Id, token);
        Assert.True(probe.Blocked);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SET LOCK_TIMEOUT 1000; UPDATE Contests SET Name=Name WHERE Id=@id";
        command.Parameters.AddWithValue("@id", contest.Id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
        Assert.True(file.CanRead);
    }

    [Fact]
    public async Task HistoricalDataPreservesBytesFiltersRecordsAndDeduplicatesJuryAttempts()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var jury = await site.AddUserAsync(RoleNames.Jury);
        var outsider = await site.AddUserAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        db.Attach(jury);
        db.Attach(outsider);
        contest.Name = "Контест C++";
        contest.StartTime = new DateTimeOffset(2005, 10, 28, 10, 0, 0, TimeSpan.FromHours(7));
        contest.EndTime = contest.StartTime.AddHours(5);
        contest.FreezeTime = contest.EndTime.AddHours(-1);
        contest.Description = "<p>Описание\r\nконтеста</p>";
        contest.ProblemPdf = new Blob { Data = [0, 255, 13, 10] };
        contest.TestZip = new Blob { Data = [80, 75, 1, 2, 3] };
        contest.FinalTableText = "<table>Итоги</table>";
        contest.OfficialTableText = "<table>Официальные</table>";
        var problem = TestEntities.CreateProblem(contest, 4);
        problem.Name = "Задача \"C++\"\\путь\nстрока";
        problem.IsActive = false;
        problem.Text = "<h3>Условие</h3>\r\nТекст";
        problem.SlowTimeLimit = 3;
        var emptyProblem = TestEntities.CreateProblem(contest, 103, problem.Checker);
        var participant = TestEntities.CreateCompetitor(contest, "Участник \"C++\"\\путь\nстрока");
        participant.IsApproved = false;
        participant.IsOutOfCompetition = true;
        participant.IsDisqualified = true;
        var noSubmissions = TestEntities.CreateCompetitor(contest, "Без посылок");
        var hiddenParticipant = TestEntities.CreateCompetitor(contest, "Скрытый участник");
        hiddenParticipant.IsHidden = true;
        var otherContest = await site.AddContestAsync();
        db.Attach(otherContest);
        var otherParticipant = TestEntities.CreateCompetitor(otherContest, "Другое соревнование");
        var compiler = TestEntities.CreateCompilator();
        var secondCompiler = TestEntities.CreateCompilator();
        secondCompiler.ConfigName = "second";
        db.Problems.AddRange(problem, emptyProblem);
        db.Competitors.AddRange(participant, noSubmissions, hiddenParticipant, otherParticipant);
        db.Tests.AddRange(
            new Test { Problem = problem, Number = 1, Input = [0, 255, 13, 10], Output = [128], Score = 0 },
            new Test { Problem = problem, Number = 2, Input = [2], Output = [3], IsOpen = true, IsActive = true, Comment = "Пример" },
            new Test { Problem = problem, Number = 1001, Input = [], Output = [], Score = 100, IsActive = true });
        byte[] source = [255, 0, 13, 10, 10];
        var start = TestEntities.CreateSubmission(participant, problem, compiler, contest.StartTime);
        start.Score = 0;
        var end = TestEntities.CreateSubmission(participant, problem, compiler, contest.EndTime);
        end.StatusCode = 2;
        var juryBefore = TestEntities.CreateSubmission(jury, problem, compiler, contest.StartTime.AddHours(-1), source);
        juryBefore.IsReference = true;
        juryBefore.IsHidden = true;
        var juryAfter = TestEntities.CreateSubmission(jury, problem, secondCompiler, contest.EndTime.AddSeconds(0.5), source);
        var hiddenAttempt = TestEntities.CreateSubmission(participant, problem, compiler, contest.StartTime.AddMinutes(1));
        hiddenAttempt.IsHidden = true;
        db.Submissions.AddRange(start, end, juryBefore, juryAfter, hiddenAttempt,
            TestEntities.CreateSubmission(participant, problem, compiler, contest.StartTime.AddTicks(-1)),
            TestEntities.CreateSubmission(participant, problem, compiler, contest.EndTime.AddTicks(1)),
            TestEntities.CreateSubmission(outsider, problem, compiler, contest.StartTime),
            TestEntities.CreateSubmission(otherParticipant, problem, compiler, contest.StartTime));
        var privateMessage = new Message { Contest = contest, User = participant, Problem = problem, UserText = "Вопрос", JuryText = "Ответ", SendTime = contest.StartTime };
        var announcement = new Message { Contest = contest, JuryText = "Объявление" };
        var clarification = new Message { Contest = contest, Problem = problem, JuryText = "Уточнение" };
        db.Messages.AddRange(privateMessage, announcement, clarification,
            new Message { Contest = contest, User = jury, JuryText = "Пропустить" },
            new Message { Contest = contest, User = otherParticipant, UserText = "Чужой участник" });
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<ContestExportService>();
        var files = await ReadAsync(service, contest.Id);
        var repeated = await ReadAsync(service, contest.Id);
        Assert.Equal(files.Keys, repeated.Keys);
        foreach (string path in files.Keys) Assert.Equal(files[path], repeated[path]);
        string root = contest.Abbr + "/";
        string task = root + "problem04/";
        Assert.Contains("Контест C++", Encoding.UTF8.GetString(files[root + "contest.json"]));
        Assert.Contains("Задача ", Encoding.UTF8.GetString(files[task + "problem.json"]));
        Assert.Contains("C++", Encoding.UTF8.GetString(files[task + "problem.json"]));
        Assert.Contains("Пример", Encoding.UTF8.GetString(files[task + "problem.json"]));
        Assert.Contains("Участник ", Encoding.UTF8.GetString(files[root + "participants.jsonl"]));
        Assert.Contains("C++", Encoding.UTF8.GetString(files[root + "participants.jsonl"]));
        Assert.Contains("Вопрос", Encoding.UTF8.GetString(files[root + "messages.jsonl"]));
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, files[root + "statements.pdf"]);
        Assert.Equal(contest.TestZip.Data, files[root + "published-tests.zip"]);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, files[task + "tests/input001.txt"]);
        Assert.Equal(new byte[] { 128 }, files[task + "tests/output001.txt"]);
        Assert.Empty(files[task + "tests/input1001.txt"]);
        Assert.Equal(problem.Text, Encoding.UTF8.GetString(files[task + "statement.html"]));
        Assert.Equal(contest.Description, Encoding.UTF8.GetString(files[root + "description.html"]));
        Assert.Equal(contest.FinalTableText, Encoding.UTF8.GetString(files[root + "tables/final.html"]));
        Assert.Equal(contest.OfficialTableText, Encoding.UTF8.GetString(files[root + "tables/official.html"]));
        var metadata = Json(files, root + "contest.json");
        Assert.Equal(contest.Id, metadata.GetProperty("id").GetInt32());
        Assert.EndsWith("+07:00", metadata.GetProperty("startTime").GetString());
        Assert.Equal([compiler.Id, secondCompiler.Id], metadata.GetProperty("compilers").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
        Assert.Single(metadata.GetProperty("checkers").EnumerateArray());
        var manifest = Json(files, task + "problem.json");
        Assert.Equal(problem.Name, manifest.GetProperty("name").GetString());
        Assert.False(manifest.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, manifest.GetProperty("openTestCount").GetInt32());
        Assert.Equal(2, Assert.Single(manifest.GetProperty("openTests").EnumerateArray()).GetInt32());
        Assert.Equal(0, manifest.GetProperty("scores").GetProperty("1").GetInt32());
        Assert.False(manifest.GetProperty("scores").TryGetProperty("2", out _));
        Assert.Equal(1, Assert.Single(manifest.GetProperty("inactiveTests").EnumerateArray()).GetInt32());
        Assert.Equal("Пример", manifest.GetProperty("testComments").GetProperty("2").GetString());
        var empty = Json(files, root + "problem103/problem.json");
        Assert.Equal(0, empty.GetProperty("openTestCount").GetInt32());
        Assert.False(empty.TryGetProperty("openTests", out _));
        Assert.DoesNotContain(files.Keys, path => path.StartsWith(root + "problem103/tests/", StringComparison.Ordinal));
        var participants = Lines(files, root + "participants.jsonl");
        Assert.Equal(participant.Name, participants[0].GetProperty("name").GetString());
        Assert.Equal([participant.Id, noSubmissions.Id], participants.Select(item => item.GetProperty("id").GetInt32()));
        Assert.Equal(5, participants[0].EnumerateObject().Count());
        Assert.Equal(new[] { privateMessage.Id, announcement.Id, clarification.Id }.Order(), Lines(files, root + "messages.jsonl").Select(item => item.GetProperty("id").GetInt32()));
        var results = Lines(files, root + "results.jsonl");
        Assert.Equal([start.Id, end.Id], results.Select(item => item.GetProperty("submissionId").GetInt32()));
        Assert.Equal(6, results[0].EnumerateObject().Count());
        Assert.Equal(0, results[0].GetProperty("score").GetInt32());
        Assert.Equal(JsonValueKind.Null, results[1].GetProperty("score").ValueKind);
        string sourcePath = task + "jury-solutions/" + Convert.ToHexStringLower(SHA256.HashData(source)) + ".src";
        Assert.Equal(source, files[sourcePath]);
        var solution = Assert.Single(Lines(files, task + "jury-solutions/solutions.jsonl"));
        var attempts = solution.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal([juryBefore.Id, juryAfter.Id], attempts.Select(item => item.GetProperty("submissionId").GetInt32()));
        Assert.Equal(-3600, attempts[0].GetProperty("secondsFromStart").GetDecimal());
        Assert.Equal(18000.5m, attempts[1].GetProperty("secondsFromStart").GetDecimal());
        Assert.True(attempts[0].GetProperty("isReference").GetBoolean());
        Assert.False(attempts[1].TryGetProperty("isReference", out _));
        Assert.Equal(jury.Name, attempts[0].GetProperty("authorName").GetString());
        foreach (var (path, bytes) in files.Where(item => item.Key.EndsWith(".json", StringComparison.Ordinal) || item.Key.EndsWith(".jsonl", StringComparison.Ordinal)))
        {
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), path);
            Assert.DoesNotContain((byte)'\r', bytes);
        }
        Assert.Equal(9, await db.Submissions.CountAsync(item => item.ProblemId == problem.Id, token));
    }

    [Fact]
    public async Task AllContestsIncludeHiddenArchiveAndPathChangesPreserveIds()
    {
        var token = TestContext.Current.CancellationToken;
        var first = await site.AddContestAsync();
        var second = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(first);
        db.Attach(second);
        first.EndTime = first.StartTime.AddDays(-100);
        var problem = TestEntities.CreateProblem(first);
        db.Problems.Add(problem);
        db.Tests.Add(new Test { Problem = problem, Number = 3, IsOpen = true, Input = [], Output = [] });
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<ContestExportService>();
        var all = await ReadAsync(service, null);
        Assert.Contains(first.Abbr + "/contest.json", all.Keys);
        Assert.Contains(second.Abbr + "/contest.json", all.Keys);
        Assert.Empty(all[second.Abbr + "/results.jsonl"]);
        Assert.Empty(all[second.Abbr + "/messages.jsonl"]);
        Assert.Empty(all[second.Abbr + "/participants.jsonl"]);
        var initial = Json(all, first.Abbr + "/problem01/problem.json");
        Assert.Equal(1, initial.GetProperty("openTestCount").GetInt32());
        Assert.False(initial.TryGetProperty("openTests", out _));
        string oldAbbr = first.Abbr;
        first.Abbr += "-renamed";
        problem.Number = 12;
        await db.SaveChangesAsync(token);
        var renamed = await ReadAsync(service, first.Id);
        Assert.DoesNotContain(renamed.Keys, path => path.StartsWith(oldAbbr + "/", StringComparison.Ordinal));
        Assert.Equal(first.Id, Json(renamed, first.Abbr + "/contest.json").GetProperty("id").GetInt32());
        Assert.Equal(problem.Id, Json(renamed, first.Abbr + "/problem12/problem.json").GetProperty("id").GetInt32());
        first.Name = "Новое имя";
        problem.Name = "Новое имя задачи";
        await db.SaveChangesAsync(token);
        Assert.Equal(renamed.Keys, (await ReadAsync(service, first.Id)).Keys);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.ExportAsync(int.MaxValue, token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(first.Id, cancelled.Token));
    }

    [Fact]
    public async Task ProblemZeroKeepsNumberIdentityAndTestsInSingleAndAllContestExports()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        var problem = TestEntities.CreateProblem(contest, 0);
        db.Tests.Add(new Test { Problem = problem, Number = 1, IsOpen = true, Input = [0, 255], Output = [13, 10] });
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<ContestExportService>();
        foreach (int? contestId in new int?[] { contest.Id, null })
        {
            var files = await ReadAsync(service, contestId);
            string root = contest.Abbr + "/problem00/";
            var manifest = Json(files, root + "problem.json");
            Assert.Equal(0, manifest.GetProperty("number").GetInt32());
            Assert.Equal(problem.Id, manifest.GetProperty("id").GetInt32());
            Assert.Equal(new byte[] { 0, 255 }, files[root + "tests/input001.txt"]);
            Assert.Equal("\r\n"u8.ToArray(), files[root + "tests/output001.txt"]);
        }
        problem.Number = -1;
        await db.SaveChangesAsync(token);
        var error = await Assert.ThrowsAsync<OperationValidationException>(() => service.ExportAsync(contest.Id, token));
        Assert.Contains($"problemId={problem.Id}", error.Message);
        problem.Number = 0;
        await db.SaveChangesAsync(token);
    }

    [Fact]
    public async Task InvalidAndDuplicatePathsRejectCompleteExportWithRecordIds()
    {
        var token = TestContext.Current.CancellationToken;
        var first = await site.AddContestAsync();
        var second = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(first);
        db.Attach(second);
        var service = scope.ServiceProvider.GetRequiredService<ContestExportService>();
        string original = first.Abbr;
        foreach (string invalid in new[] { "../escape", "a\\b", "", "CON", "a.", "a:b" })
        {
            first.Abbr = invalid;
            await db.SaveChangesAsync(token);
            var error = await Assert.ThrowsAsync<OperationValidationException>(() => service.ExportAsync(first.Id, token));
            Assert.Contains($"contestId={first.Id}", error.Message);
        }
        first.Abbr = second.Abbr.ToUpperInvariant();
        await db.SaveChangesAsync(token);
        var duplicate = await Assert.ThrowsAsync<OperationValidationException>(() => service.ExportAsync(null, token));
        Assert.Contains($"contestId={first.Id}", duplicate.Message);
        Assert.Contains($"contestId={second.Id}", duplicate.Message);
        first.Abbr = original;
        var firstProblem = TestEntities.CreateProblem(first);
        var secondProblem = TestEntities.CreateProblem(first, 1, firstProblem.Checker);
        db.Problems.AddRange(firstProblem, secondProblem);
        await db.SaveChangesAsync(token);
        duplicate = await Assert.ThrowsAsync<OperationValidationException>(() => service.ExportAsync(first.Id, token));
        Assert.Contains($"problemId={firstProblem.Id}", duplicate.Message);
        Assert.Contains($"problemId={secondProblem.Id}", duplicate.Message);
        secondProblem.Number = 2;
        var firstTest = new Test { Problem = firstProblem, Number = 1, Input = [], Output = [] };
        var secondTest = new Test { Problem = firstProblem, Number = 1, Input = [], Output = [] };
        db.Tests.AddRange(firstTest, secondTest);
        await db.SaveChangesAsync(token);
        duplicate = await Assert.ThrowsAsync<OperationValidationException>(() => service.ExportAsync(first.Id, token));
        Assert.Contains($"problemId={firstProblem.Id}", duplicate.Message);
        Assert.Contains($"testId={firstTest.Id}", duplicate.Message);
        Assert.Contains($"testId={secondTest.Id}", duplicate.Message);
        secondTest.Number = 2;
        await db.SaveChangesAsync(token);
    }

    private static async Task<Dictionary<string, byte[]>> ReadAsync(ContestExportService service, int? contestId)
    {
        string temporaryPath;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var stream = await service.ExportAsync(contestId, TestContext.Current.CancellationToken))
        {
            temporaryPath = stream.Name;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                await using var source = entry.Open();
                using var target = new MemoryStream();
                await source.CopyToAsync(target, TestContext.Current.CancellationToken);
                files.Add(entry.FullName, target.ToArray());
            }
        }
        Assert.False(File.Exists(temporaryPath));
        return files;
    }

    private static JsonElement Json(Dictionary<string, byte[]> files, string path)
    {
        using var document = JsonDocument.Parse(files[path]);
        return document.RootElement.Clone();
    }

    private static JsonElement[] Lines(Dictionary<string, byte[]> files, string path) => [.. Encoding.UTF8.GetString(files[path])
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        })];

    private sealed class LockProbe(string connectionString, int contestId) : DbCommandInterceptor
    {
        private bool _checked;
        public bool Blocked { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (_checked) return result;
            _checked = true;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText = "SET LOCK_TIMEOUT 250; UPDATE Contests SET Name=Name WHERE Id=@id";
            update.Parameters.AddWithValue("@id", contestId);
            try
            {
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex) when (ex.Number == 1222)
            {
                Blocked = true;
            }
            return result;
        }
    }
}
