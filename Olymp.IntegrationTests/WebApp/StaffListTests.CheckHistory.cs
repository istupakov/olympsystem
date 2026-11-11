using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;

using Xunit;

using JuryDetails = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails;

namespace Olymp.IntegrationTests.WebApp;

public partial class StaffListTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckHistoryShowsScoresOnlyForSchoolContests(bool school)
    {
        var author = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Check history", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        contest.Abbr = school ? "school-history" : "icpc-history";
        var submission = TestEntities.CreateSubmission(author, TestEntities.CreateProblem(contest),
            TestEntities.CreateCompilator(), DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.CheckResults.Add(new CheckResult
        {
            Submission = submission,
            CheckTime = DateTimeOffset.UtcNow,
            StatusCode = 2,
            Score = 37,
            TestResults = "SCORES_BY_TEST",
            Log = "Compilation:\nuser time: 1\nExecution log"
        });
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(
            $"/jury/submissions/{submission.Id}", new() { [nameof(JuryDetails.Id)] = submission.Id }, Staff(author.Id, RoleNames.Jury)));
        var history = Assert.Single(page.QuerySelectorAll("details.card"));
        Assert.Equal(school, history.TextContent.Contains("SCORES_BY_TEST", StringComparison.Ordinal));
        Assert.Equal("Compilation:\nuser time: 1\nExecution log", history.QuerySelector("pre")!.TextContent);
    }
}
