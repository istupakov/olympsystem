using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestExportTests
{
    [Fact]
    public async Task OptionsIndependentlySelectFilesAndBasicArchiveRoundTripsMaterials()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var author = await site.AddUserAsync(RoleNames.Admin);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        db.Attach(author);
        contest.Description = "<p>Description</p>";
        contest.FinalTableText = "<table>Final</table>";
        contest.OfficialTableText = "<table>Official</table>";
        contest.ProblemPdf = new Blob { Data = "%PDF-stored"u8.ToArray() };
        contest.TestZip = new Blob { Data = [1, 2, 3] };
        var problem = TestEntities.CreateProblem(contest, 7);
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        problem.Tests.Add(new Test { Problem = problem, Number = 1, IsActive = true, IsOpen = true, Input = "sample"u8.ToArray(), Output = "answer"u8.ToArray() });
        var compiler = TestEntities.CreateCompilator();
        var reference = TestEntities.CreateSubmission(author, problem, compiler, contest.StartTime.AddMinutes(-1), "int main(){}"u8.ToArray());
        reference.IsReference = true;
        reference.Description = "main.cpp";
        var validator = TestEntities.CreateSubmission(author, problem, compiler, contest.StartTime.AddMinutes(-1), "// validator"u8.ToArray());
        validator.IsReference = true;
        validator.Description = "validator.cpp";
        db.Submissions.AddRange(reference, validator);
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<ContestExportService>();
        string root = contest.Abbr + "/", task = root + "problem07/";
        var basic = await ReadAsync(service, contest.Id, new());
        Assert.Equal(new[] { root + "contest.json", root + "description.html", task + "problem.json" }.Order(), basic.Keys.Order());
        var materials = await ReadAsync(service, contest.Id, new(Tables: true, Markdown: true));
        using var materialZip = new MemoryStream(ProblemArchives.Zip([.. materials.Select(f => (f.Key, f.Value))]));
        var imported = Assert.Single(await MaterialArchive.ReadAsync(materialZip, new(), token));
        Assert.Equal(problem.Text, imported.Statements[problem.Id]);
        Assert.Null(imported.Error);
        Assert.False(Json(basic, root + "contest.json").TryGetProperty("compilers", out _));

        var cases = new (ContestExportOptions Options, string[] Additional)[] {
            (new(Tables: true), [root + "tables/final.html", root + "tables/official.html"]),
            (new(Markdown: true), [task + "statement.md"]),
            (new(Tests: true), [task + "tests/input001.txt", task + "tests/output001.txt"]),
            (new(Participants: true), [root + "participants.jsonl"]),
            (new(ResultsAndMessages: true), [root + "results.jsonl", root + "messages.jsonl"]),
            (new(ReferenceSolutions: true), [task + "solutions/main.cpp", task + "validator.cpp"]),
            (new(JurySolutions: true), [task + "jury-solutions/solutions.jsonl", .. new[] { reference, validator }.Select(s => task + "jury-solutions/" + Convert.ToHexStringLower(SHA256.HashData(s.SourceCode)) + compiler.SourceExtension)]),
            (new(TestZip: true), [root + "published-tests.zip"]),
            (new(Pdf: true), [root + "statements.pdf"])
        };
        foreach (var (options, additional) in cases)
        {
            var files = await ReadAsync(service, contest.Id, options);
            Assert.Equal(basic.Keys.Concat(additional).Order(), files.Keys.Order());
        }
        var tex = await ReadAsync(service, contest.Id, new(Tex: true));
        Assert.Contains(root + "tex/problem07.tex", tex.Keys);
        Assert.Contains(root + "tex/samples/problem07-1.in", tex.Keys);
        Assert.DoesNotContain(root + "tex/warnings.txt", tex.Keys);
        Assert.Contains("\\input{problem07.tex}", Encoding.UTF8.GetString(tex[root + "tex/statements.tex"]));
        Assert.DoesNotContain(tex.Keys, p => p.StartsWith(task + "tests/", StringComparison.Ordinal));
        using var problemZip = new MemoryStream(ProblemArchives.Zip([.. (await ReadAsync(service, contest.Id, new(Tests: true, ReferenceSolutions: true))).Select(f => (f.Key, f.Value))]));
        var package = await ProblemArchive.ReadAsync(problemZip, new(), token);
        Assert.Equal(problem.Id, Assert.Single(package.Problems).Manifest.Id);
        Assert.Null(package.Problems[0].Error);
        Assert.Single(package.Problems[0].Tests);
    }

    [Fact]
    public async Task TexIsSkippedWithoutActiveStatementsAndUsesOriginalNumbersAcrossGlobalExport()
    {
        var empty = await site.AddContestAsync();
        var populated = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(empty);
        db.Attach(populated);
        var inactive = TestEntities.CreateProblem(empty, 1);
        inactive.Text = Olymp.Tests.Statements.StatementTests.Source;
        inactive.IsActive = false;
        var whitespace = TestEntities.CreateProblem(empty, 2);
        whitespace.Text = " \n";
        var missing = TestEntities.CreateProblem(populated, 2);
        var supplied = TestEntities.CreateProblem(populated, 7);
        supplied.Text = Olymp.Tests.Statements.StatementTests.Source;
        db.Problems.AddRange(inactive, whitespace, missing, supplied);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var files = await ReadAsync(scope.ServiceProvider.GetRequiredService<ContestExportService>(), null, new(Tex: true));
        Assert.DoesNotContain(files.Keys, p => p.StartsWith(empty.Abbr + "/tex/", StringComparison.Ordinal));
        Assert.Contains(populated.Abbr + "/tex/problem02.tex", files.Keys);
        Assert.Contains(populated.Abbr + "/tex/problem07.tex", files.Keys);
        Assert.Contains(populated.Abbr + "/tex/warnings.txt", files.Keys);
    }

    [Fact]
    public async Task JuryIncludesAdminsAndHiddenAuthorsWithPreStartSubmissionsOnlyInThisContest()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var other = await site.AddContestAsync();
        var admin = await site.AddUserAsync(RoleNames.Admin);
        var early = await site.AddUserAsync();
        var boundary = await site.AddUserAsync(RoleNames.Jury);
        var elsewhere = await site.AddUserAsync();
        var visible = await site.AddUserAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        foreach (var user in new[] { admin, early, boundary, elsewhere, visible }) db.Attach(user);
        admin.IsHidden = false;
        early.IsHidden = boundary.IsHidden = elsewhere.IsHidden = true;
        db.Attach(contest);
        db.Attach(other);
        var first = TestEntities.CreateProblem(contest, 1);
        var second = TestEntities.CreateProblem(contest, 2);
        var otherProblem = TestEntities.CreateProblem(other, 1);
        var cpp = TestEntities.CreateCompilator();
        cpp.SourceExtension = ".cpp";
        cpp.Language = "C++";
        var py = TestEntities.CreateCompilator();
        py.SourceExtension = ".py";
        py.Language = "Python";
        byte[] source = "same bytes"u8.ToArray();
        var qualifying = TestEntities.CreateSubmission(early, first, cpp, contest.StartTime.AddTicks(-1));
        var cppAttempt = TestEntities.CreateSubmission(early, second, cpp, contest.EndTime.AddHours(1), source);
        var pyAttempt = TestEntities.CreateSubmission(early, second, py, contest.StartTime, source);
        var adminAttempt = TestEntities.CreateSubmission(admin, second, cpp, contest.StartTime, source);
        db.Submissions.AddRange(qualifying, cppAttempt, pyAttempt, adminAttempt,
            TestEntities.CreateSubmission(boundary, second, cpp, contest.StartTime, "boundary"u8.ToArray()),
            TestEntities.CreateSubmission(elsewhere, otherProblem, cpp, contest.StartTime.AddDays(-1)),
            TestEntities.CreateSubmission(elsewhere, second, cpp, contest.EndTime, "elsewhere"u8.ToArray()),
            TestEntities.CreateSubmission(visible, first, cpp, contest.StartTime.AddTicks(-1)),
            TestEntities.CreateSubmission(visible, second, cpp, contest.EndTime, "visible"u8.ToArray()));
        await db.SaveChangesAsync(token);
        var files = await ReadAsync(scope.ServiceProvider.GetRequiredService<ContestExportService>(), contest.Id, new(JurySolutions: true));
        string root = contest.Abbr + "/problem02/jury-solutions/";
        string hash = Convert.ToHexStringLower(SHA256.HashData(source));
        Assert.Equal(source, files[root + hash + ".cpp"]);
        Assert.Equal(source, files[root + hash + ".py"]);
        var solutions = Lines(files, root + "solutions.jsonl");
        Assert.Equal(2, solutions.Length);
        var attempts = solutions.SelectMany(s => s.GetProperty("attempts").EnumerateArray()).ToArray();
        Assert.Equal(new[] { cppAttempt.Id, pyAttempt.Id, adminAttempt.Id }.Order(), attempts.Select(a => a.GetProperty("submissionId").GetInt32()).Order());
        Assert.All(attempts, a => Assert.False(a.TryGetProperty("compilerId", out _)));
        Assert.Contains(attempts, a => a.GetProperty("language").GetString() == "Python" && a.GetProperty("compiler").GetString() == py.Name);
    }
}
