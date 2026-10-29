using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Site;

using Xunit;

namespace Olymp.IntegrationTests.Site;

[Trait("Category", "Integration")]
public class SubmissionTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Fact]
    public async Task HealthCheckCanReachTestDatabase()
    {
        using var client = factory.CreateSiteClient();

        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousVisitorIsRedirectedToLogin()
    {
        using var client = factory.CreateSiteClient();

        using var response = await client.GetAsync($"/submissions/download/{factory.Data.Submission.Id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/Identity/Account/Login", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("download")]
    public async Task OwnerCanReadOwnSubmission(string page)
    {
        using var client = await factory.LoginAsync("team");

        using var response = await client.GetAsync($"/submissions/{page}/{factory.Data.Submission.Id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SiteData.Source, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("download")]
    public async Task OtherParticipantCannotReadSubmission(string page)
    {
        using var client = await factory.LoginAsync("team");

        using var response = await client.GetAsync($"/submissions/{page}/{factory.Data.OtherSubmission.Id}",
            TestContext.Current.CancellationToken);

        AssertAccessDenied(response);
    }

    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Jury)]
    public async Task JuryAndAdministratorCanReadOtherParticipantsSubmission(string role)
    {
        using var client = await factory.LoginAsync(role);

        using var response = await client.GetAsync($"/submissions/download/{factory.Data.Submission.Id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SiteData.Source, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TeamMemberCanReadTeamSubmission()
    {
        using var client = await factory.LoginAsync("member");

        using var response = await client.GetAsync($"/submissions/download/{factory.Data.Submission.Id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SiteData.Source, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingSubmissionReturnsNotFound()
    {
        using var client = await factory.LoginAsync("team");

        using var response = await client.GetAsync($"/submissions/download/{int.MaxValue}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WrongPasswordDoesNotGrantAccess()
    {
        using var client = factory.CreateSiteClient();
        using var login = await SiteFactory.PostFormAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = "team",
            ["Input.Password"] = "wrong-password"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync($"/submissions/download/{factory.Data.Submission.Id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/Identity/Account/Login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ParticipantCanSubmitSolutionToOwnContest()
    {
        using var client = await factory.LoginAsync("team");
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        int count = await context.Submissions.CountAsync(TestContext.Current.CancellationToken);

        using var response = await SiteFactory.PostFormAsync(client, $"/contests/submits/{factory.Data.Contest.Id}",
            SubmissionFields(factory.Data.Problem.Id));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(count + 1, await context.Submissions.CountAsync(TestContext.Current.CancellationToken));
        var submission = await context.Submissions.OrderByDescending(item => item.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(factory.Data.Competitor.Id, submission.UserId);
        Assert.Equal(factory.Data.Problem.Id, submission.ProblemId);
        Assert.Equal(factory.Data.Compilator.Id, submission.CompilatorId);
        Assert.Equal(SiteData.Source, submission.Text);
        Assert.Equal(0, submission.StatusCode);
    }

    [Fact]
    public async Task ParticipantCannotSubmitToAnotherContest()
    {
        using var client = await factory.LoginAsync("team");
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        int count = await context.Submissions.CountAsync(TestContext.Current.CancellationToken);

        using var response = await SiteFactory.PostFormAsync(client,
            $"/contests/submits/{factory.Data.OtherContest.Id}", SubmissionFields(factory.Data.OtherProblem.Id),
            tokenPage: $"/contests/submits/{factory.Data.Contest.Id}");

        AssertAccessDenied(response);
        Assert.Equal(count, await context.Submissions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProblemFromAnotherContestDoesNotCreateSubmission()
    {
        using var client = await factory.LoginAsync("team");
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        int count = await context.Submissions.CountAsync(TestContext.Current.CancellationToken);

        using var response = await SiteFactory.PostFormAsync(client, $"/contests/submits/{factory.Data.Contest.Id}",
            SubmissionFields(factory.Data.OtherProblem.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(count, await context.Submissions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisqualifiedParticipantCannotSubmitWithExistingSession()
    {
        using var client = await factory.LoginAsync("team");
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var competitor = await context.Competitors.AsTracking()
            .SingleAsync(user => user.Id == factory.Data.Competitor.Id, TestContext.Current.CancellationToken);
        competitor.IsDisqualified = true;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        try
        {
            using var response = await client.GetAsync($"/contests/submits/{factory.Data.Contest.Id}",
                TestContext.Current.CancellationToken);

            AssertAccessDenied(response);
        }
        finally
        {
            competitor.IsDisqualified = false;
            await context.SaveChangesAsync(CancellationToken.None);
        }
    }

    private Dictionary<string, string> SubmissionFields(int problemId)
    {
        return new()
        {
            ["Input.ProblemId"] = problemId.ToString(),
            ["Input.CompilatorId"] = factory.Data.Compilator.Id.ToString(),
            ["Input.Code"] = SiteData.Source
        };
    }

    private static void AssertAccessDenied(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/Identity/Account/AccessDenied", response.Headers.Location?.OriginalString);
    }
}
