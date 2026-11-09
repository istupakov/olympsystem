using System.Net;
using System.Security.Claims;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class JuryAnnouncementsTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "upcoming")]
    [InlineData("ru", "running")]
    [InlineData("en", "running")]
    [InlineData("en", "ended")]
    public async Task GeneralAnnouncementsAppearOnlyInOwnParticipantHeaderUntilEnd(string culture, string state)
    {
        var contest = await AddContestAsync(state);
        var other = await AddContestAsync("running");
        var participant = await AddParticipantAsync(contest);
        var now = DateTimeOffset.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            db.Messages.AddRange(
                Message(contest, "<strong>Older</strong>", sent: now.AddMinutes(-2)),
                Message(contest, "<strong>Newer</strong>", sent: now.AddMinutes(-1)),
                Message(contest, "<strong>Newest</strong>", sent: now.AddMinutes(-1)),
                Message(contest, "PRIVATE GENERAL", userId: participant.Id),
                Message(contest, "Task clarification", contest.Problems.First()),
                Message(other, "FOREIGN GENERAL"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(participant.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var paths = new List<string> { $"/contests/{contest.Id}/rules", "/submissions" };
        if (state != "upcoming")
            paths.AddRange([$"/contests/{contest.Id}/problems", $"/contests/{contest.Id}/standing"]);
        foreach (string path in paths)
        {
            using var document = await GetAsync(client, path);
            var block = document.QuerySelector(".contest-header .jury-announcements");
            if (state == "ended")
            {
                Assert.Null(block);
            }
            else
            {
                Assert.NotNull(block);
                Assert.Equal(culture == "ru" ? "Объявления жюри" : "Jury announcements", block.QuerySelector("h2")!.TextContent);
                Assert.Equal(["Newest", "Newer", "Older"], block.QuerySelectorAll("article strong").Select(item => item.TextContent));
                Assert.Equal(3, block.QuerySelectorAll("time[datetime]").Length);
                Assert.DoesNotContain("Task clarification", block.TextContent);
                Assert.NotNull(block.PreviousElementSibling!.QuerySelector("a[aria-current='page']"));
            }
            Assert.DoesNotContain("PRIVATE GENERAL", document.DocumentElement.OuterHtml);
            Assert.DoesNotContain("FOREIGN GENERAL", document.DocumentElement.OuterHtml);
        }
        using var foreign = await GetAsync(client, $"/contests/{other.Id}/rules");
        Assert.Null(foreign.QuerySelector(".jury-announcements"));
        Assert.DoesNotContain("FOREIGN GENERAL", foreign.DocumentElement.OuterHtml);
    }

    [Theory]
    [InlineData("permanent", "running", "ru")]
    [InlineData("permanent", "ended", "en")]
    [InlineData("participant", "running", "en")]
    [InlineData("participant", "ended", "ru")]
    [InlineData("foreign-participant", "running", "ru")]
    [InlineData("foreign-participant", "ended", "en")]
    public async Task ClarificationsFollowStatementAccessAndExistingRefreshWithoutPrivateMessages(string actor, string state, string culture)
    {
        var contest = await AddContestAsync(state, open: actor == "permanent");
        var otherContest = await AddContestAsync("running");
        var user = await factory.AddUserAsync();
        User viewer = actor == "permanent" ? user
            : await AddParticipantAsync(actor == "participant" ? contest : otherContest);
        var selected = contest.Problems.First();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            db.Messages.AddRange(
                Message(contest, "<strong>Published clarification</strong>", selected),
                Message(contest, "PRIVATE OWN REPLY", selected, viewer.Id),
                Message(contest, "PRIVATE OTHER REPLY", selected, user.Id),
                Message(contest, "OTHER PROBLEM", contest.Problems.ElementAt(1)),
                Message(contest, "INACTIVE PROBLEM", contest.Problems.Last()),
                Message(contest, "MISMATCHED CONTEST", otherContest.Problems.First()),
                Message(contest, "GENERAL ANNOUNCEMENT"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(viewer.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var document = await GetAsync(client, $"/contests/{contest.Id}/problems?problemId={selected.Id}");
        var block = document.QuerySelector("#problem-detail .jury-announcements")!;
        Assert.Equal(culture == "ru" ? "Уточнения" : "Clarifications", block.QuerySelector("h2")!.TextContent);
        Assert.Equal("Published clarification", Assert.Single(block.QuerySelectorAll("strong")).TextContent);
        Assert.Equal(block, document.QuerySelector(".problem-statement")!.LastElementChild);
        Assert.Null(block.QuerySelector("a, button"));
        Assert.DoesNotContain("GENERAL ANNOUNCEMENT", block.TextContent);
        if (actor != "participant" || state == "ended")
            Assert.Null(document.QuerySelector(".contest-header .jury-announcements"));
        foreach (string hidden in new[] { "PRIVATE OWN REPLY", "PRIVATE OTHER REPLY", "OTHER PROBLEM", "INACTIVE PROBLEM", "MISMATCHED CONTEST" })
            Assert.DoesNotContain(hidden, document.DocumentElement.OuterHtml);

        string refresh = document.QuerySelector("#submissions > div a")!.GetAttribute("href")!;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            db.Messages.Add(Message(contest, "New clarification on refresh", selected));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var updated = await GetAsync(client, refresh);
        Assert.Contains("New clarification on refresh", updated.QuerySelector("#problem-detail .jury-announcements")!.TextContent);
        Assert.Equal(selected.Id.ToString(), updated.QuerySelector("#workspace-problem option[selected]")!.GetAttribute("value"));
        using var switched = await GetAsync(client, $"/contests/{contest.Id}/problems?problemId={contest.Problems.ElementAt(1).Id}");
        Assert.DoesNotContain("Published clarification", switched.QuerySelector("#problem-detail")!.TextContent);
        Assert.Contains("OTHER PROBLEM", switched.QuerySelector("#problem-detail .jury-announcements")!.TextContent);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("future")]
    [InlineData("inactive")]
    public async Task UnpublishedStatementsDoNotExposeClarifications(string reason)
    {
        var contest = await AddContestAsync(reason == "future" ? "upcoming" : "running");
        var selected = contest.Problems.First();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            if (reason == "hidden")
                await db.Contests.Where(item => item.Id == contest.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsHidden, true), TestContext.Current.CancellationToken);
            if (reason == "inactive")
                await db.Problems.Where(item => item.Id == selected.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false), TestContext.Current.CancellationToken);
            db.Messages.Add(Message(contest, "UNPUBLISHED CLARIFICATION", selected));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await client.GetAsync($"/contests/{contest.Id}/problems?problemId={selected.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("UNPUBLISHED CLARIFICATION", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyAndPrivateMessagesDoNotRenderBlocks()
    {
        var contest = await AddContestAsync("running");
        var participant = await AddParticipantAsync(contest);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            foreach (var problem in new Problem?[] { null, contest.Problems.First() })
                foreach (string? text in new[] { null, "", " \t\r\n", "PRIVATE" })
                    db.Messages.Add(Message(contest, text, problem, text == "PRIVATE" ? participant.Id : null));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(participant.UserName!);
        using var document = await GetAsync(client, $"/contests/{contest.Id}/problems");
        Assert.Null(document.QuerySelector(".jury-announcements"));
        Assert.DoesNotContain("PRIVATE", document.DocumentElement.OuterHtml);
    }

    private async Task<Contest> AddContestAsync(string state, bool open = false)
    {
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest($"Announcements {Guid.NewGuid():N}",
            now.AddHours(state == "upcoming" ? 1 : -2), now.AddHours(state == "ended" ? -1 : 2));
        contest.IsOpen = open;
        for (int number = 1; number <= 3; number++)
        {
            var problem = TestEntities.CreateProblem(contest, number);
            problem.Text = "<p>Statement</p>";
            problem.IsActive = number < 3;
            contest.Problems.Add(problem);
        }
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return contest;
    }

    private async Task<Competitor> AddParticipantAsync(Contest contest)
    {
        var participant = TestEntities.CreateCompetitor(contest, $"announcements_{Guid.NewGuid():N}");
        participant.UserName = participant.Name;
        participant.Contest = null!;
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.CreateAsync(participant, WebAppFactory.Password)).Succeeded);
        Assert.True((await users.AddClaimAsync(participant,
            new Claim(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        return participant;
    }

    private static Message Message(Contest contest, string? text, Problem? problem = null, int? userId = null, DateTimeOffset? sent = null) => new()
    {
        ContestId = contest.Id,
        Contest = null!,
        ProblemId = problem?.Id,
        UserId = userId,
        JuryText = text,
        SendTime = sent ?? DateTimeOffset.UtcNow
    };

    private static async Task<IDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
    }
}
