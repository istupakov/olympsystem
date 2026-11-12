using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

public partial class PracticeSubmissionTests
{
    [Theory]
    [InlineData("C++23 (GCC 16.2)")]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task SubmissionDetailsShowAuthorContestLinkedProblemAndFullCompiler(string? description)
    {
        var (user, contest, problem, _) = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var compiler = await db.Compilators.SingleAsync(TestContext.Current.CancellationToken);
        compiler.Name = description is null ? "Microsoft Visual C++ 2008" : "GCC C++23";
        compiler.Description = description;
        compiler.IsActive = false;
        var submission = TestEntities.CreateSubmission(user, problem, compiler, DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = await factory.LoginAsync(user.UserName!);
        using var page = await GetAsync(client, $"/submissions/{submission.Id}");
        var metadata = page.QuerySelector("main dl")!;
        Assert.Contains(user.Name, metadata.TextContent);
        Assert.Contains(contest.Name, metadata.TextContent);
        Assert.Contains(string.IsNullOrWhiteSpace(description) ? compiler.Name : description, metadata.TextContent);
        var link = Assert.Single(metadata.QuerySelectorAll("a"));
        Assert.Equal(problem.NameWithNumber, link.TextContent);
        Assert.Equal($"contests/{contest.Id}/problems?problemId={problem.Id}#submissions", link.GetAttribute("href"));
        using var destination = await GetAsync(client, "/" + link.GetAttribute("href"));
        Assert.Contains(problem.Name, destination.QuerySelector("main")!.TextContent);
    }

    [Theory]
    [InlineData(" \n\t\r\n")]
    [InlineData("Compilation:\nuser time: 1\ntotal time: 2\n \n")]
    [InlineData("Compilation:\ncommand: private command\ncompilator:\n \n")]
    public async Task BlankCompilationOutputShowsDiagnosticsUnavailable(string log)
    {
        var (user, _, problem, compiler) = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var submission = TestEntities.CreateSubmission(user, problem, compiler, DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        submission.StatusCode = -1;
        submission.CheckResults.Add(new()
        {
            Submission = submission,
            StatusCode = -1,
            CheckTime = DateTimeOffset.UtcNow,
            Log = log
        });
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = await factory.LoginAsync(user.UserName!);
        using var page = await GetAsync(client, $"/submissions/{submission.Id}");
        Assert.Equal("Compilation diagnostics are unavailable.", page.QuerySelector("#compilation-errors pre")!.TextContent);
    }
}
