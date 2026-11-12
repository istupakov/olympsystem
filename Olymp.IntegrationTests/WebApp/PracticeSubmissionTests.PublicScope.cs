using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

public partial class PracticeSubmissionTests
{
    [Theory]
    [InlineData(0, "Queued")]
    [InlineData(1, "Checking")]
    public async Task PublicScopeShowsSubmissionFactUntilCheckCompletes(int status, string ownStatus)
    {
        var (user, _, problem, compiler) = await SeedAsync();
        var other = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var submission = TestEntities.CreateSubmission(user, problem, compiler, DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        submission.StatusCode = status;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = await factory.LoginAsync(user.UserName!);
        using var otherClient = await factory.LoginAsync(other.UserName!);
        foreach (var viewer in new[] { client, otherClient })
        {
            using var page = await GetAsync(viewer, "/submissions?scope=all");
            var row = page.QuerySelector($"#submission-{submission.Id}")!;
            Assert.Contains("Submitted", row.TextContent);
            Assert.DoesNotContain("Not accepted", row.TextContent);
        }
        using var own = await GetAsync(client, "/submissions");
        Assert.Contains(ownStatus, own.QuerySelector($"#submission-{submission.Id}")!.TextContent);
    }

    [Fact]
    public async Task PublicScopeFiltersCompilationErrorsBeforePaginationAndKeepsFrozenSubmissionFacts()
    {
        var (user, _, problem, compiler) = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.SingleAsync(TestContext.Current.CancellationToken);
        contest.IsOpen = false;
        contest.FreezeTime = contest.StartTime.AddHours(2);

        var accepted = TestEntities.CreateSubmission(user, problem, compiler, contest.StartTime);
        accepted.User = null!;
        accepted.Problem = null!;
        accepted.Compilator = null!;
        accepted.StatusCode = 2;
        db.Submissions.Add(accepted);
        for (int i = 0; i < 21; i++)
        {
            var error = TestEntities.CreateSubmission(user, problem, compiler, contest.FreezeTime.Value.AddMinutes(-1));
            error.User = null!;
            error.Problem = null!;
            error.Compilator = null!;
            error.StatusCode = -1;
            db.Submissions.Add(error);
        }
        var frozen = TestEntities.CreateSubmission(user, problem, compiler, contest.FreezeTime.Value);
        frozen.User = null!;
        frozen.Problem = null!;
        frozen.Compilator = null!;
        frozen.StatusCode = -1;
        db.Submissions.Add(frozen);
        var hidden = TestEntities.CreateSubmission(user, problem, compiler, contest.StartTime);
        hidden.User = null!;
        hidden.Problem = null!;
        hidden.Compilator = null!;
        hidden.IsHidden = true;
        db.Submissions.Add(hidden);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = await factory.LoginAsync(user.UserName!);
        foreach (string path in new[] { "/submissions?scope=all", ProblemPath(contest.Id, problem.Id) + "&scope=all" })
        {
            using var page = await GetAsync(client, path);
            Assert.Equal(2, page.QuerySelectorAll("#submissions tbody tr").Length);
            Assert.Contains("Accepted", page.QuerySelector($"#submission-{accepted.Id}")!.TextContent);
            Assert.Contains("Submitted", page.QuerySelector($"#submission-{frozen.Id}")!.TextContent);
            Assert.Null(page.QuerySelector($"#submission-{hidden.Id}"));
            Assert.DoesNotContain("Compilation error", page.QuerySelector("#submissions")!.TextContent);
            Assert.Empty(page.QuerySelectorAll("#submissions a[href*='/download'], #submissions a[href*='#compilation-errors']"));
            Assert.Null(page.QuerySelector("a[rel='next']"));
        }
        using var own = await GetAsync(client, "/submissions");
        Assert.Contains("Compilation error", own.QuerySelector("#submissions")!.TextContent);
        Assert.NotEmpty(own.QuerySelectorAll("#submissions a[href*='#compilation-errors']"));
    }

    [Theory]
    [InlineData("C++23 (GCC 16.2)")]
    [InlineData(null)]
    public async Task SharedHistoryShowsCompilerNameWithOptionalDescription(string? description)
    {
        var (user, _, problem, _) = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var compiler = await db.Compilators.SingleAsync(TestContext.Current.CancellationToken);
        compiler.Name = description is null ? "Microsoft Visual C++ 2008" : "GCC C++23";
        compiler.Description = description;
        compiler.IsActive = description is not null;
        var submission = TestEntities.CreateSubmission(user, problem, compiler, DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        submission.StatusCode = 2;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = await factory.LoginAsync(user.UserName!);
        foreach (string path in new[] { "/submissions", "/submissions?scope=all" })
        {
            using var page = await GetAsync(client, path);
            var cell = page.QuerySelector($"#submission-{submission.Id}")!.QuerySelectorAll("td")[3];
            Assert.Equal(compiler.Name, cell.TextContent);
            Assert.Equal(description, cell.GetAttribute("title"));
        }
    }
}
