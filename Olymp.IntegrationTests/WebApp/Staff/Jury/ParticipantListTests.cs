using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public class ParticipantListTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, true)]
    public async Task BulkOperationsFollowWholeContestStateDespiteTableFilters(bool ended, bool unapproved, bool login, bool history)
    {
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest("Participant actions", now.AddDays(-2), ended ? now.AddDays(-1) : now.AddDays(1));
        contest.Abbr = "ioi-test";
        var participant = TestEntities.CreateCompetitor(contest, "Participant");
        participant.IsApproved = !unapproved;
        participant.UserName = login ? $"assigned-{Guid.NewGuid():N}" : null;
        db.Competitors.Add(participant);
        if (history)
            db.Submissions.Add(TestEntities.CreateSubmission(participant, TestEntities.CreateProblem(contest), TestEntities.CreateCompilator(), now));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantList>(
            $"/jury/contests/{contest.Id}/participants?Search=no-matching-participant", new() { ["ContestId"] = contest.Id }, Staff(actor.Id)));
        var actions = page.QuerySelector($"details[aria-label='{SiteResources.StaffParticipantsOperations}']")!;
        Assert.NotNull(actions);
        Assert.False(actions.HasAttribute("open"));
        var buttons = actions.QuerySelectorAll("button").Select(item => item.TextContent.Trim()).ToArray();
        Assert.Equal(unapproved, buttons.Contains(SiteResources.StaffApproveAll));
        Assert.Equal(!ended && !login, buttons.Contains(SiteResources.StaffGenerateLogins));
        Assert.Equal(login, buttons.Contains(SiteResources.StaffRemoveLogins));
        Assert.Equal(ended && !history, buttons.Contains(SiteResources.StaffDeleteEmptyParticipants));
        Assert.Equal(!ended, actions.QuerySelector("#participants-file") is not null);
        Assert.Equal(!ended && !login, actions.QuerySelector("#logins-file") is not null);
    }

    [Fact]
    public async Task GlobalListHasNoActionsEvenForAdminWithContestFilter()
    {
        var actor = await _factory.AddUserAsync();
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantList>(
            "/jury/participants?Contest=1", [], Staff(actor.Id)));
        Assert.DoesNotContain(page.QuerySelectorAll("th"), item => item.TextContent.Trim() == SiteResources.StaffActions);
        Assert.Null(page.QuerySelector($"details[aria-label='{SiteResources.StaffParticipantsOperations}']"));
        Assert.DoesNotContain(page.QuerySelectorAll("button"), item => item.TextContent == SiteResources.StaffExportLogins);
    }
}
