using System.Reflection;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public class ContestFilterTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContestSelectionUpdatesDependentFieldsWithoutApplyingOrLosingDraft(bool submissions)
    {
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var first = TestEntities.CreateContest("First contest", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        var second = TestEntities.CreateContest("Second contest", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        var problem = TestEntities.CreateProblem(first);
        var nextProblem = TestEntities.CreateProblem(second);
        var organization = new Organization { Name = "Second contest organization", Users = [] };
        var participant = TestEntities.CreateCompetitor(second, "Participant");
        participant.Organization = organization;
        db.Problems.AddRange(problem, nextProblem);
        db.Competitors.Add(participant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        string path = submissions ? $"/jury/submissions?Contest={first.Id}&Problem={problem.Id}&IpAddress=203.0.113.42" : $"/jury/participants?Contest={first.Id}";
        string html = submissions
            ? await RenderAsync<SubmissionList>(path, [], Staff(actor.Id), page => ChangeContestAsync(page, second.Id, true, nextProblem.Id))
            : await RenderAsync<ParticipantList>(path, [], Staff(actor.Id), page => ChangeContestAsync(page, second.Id, false, organization.Id));
        using var document = await new HtmlParser().ParseDocumentAsync(html);
        Assert.Null(document.QuerySelector($"nav[aria-label='{SiteResources.StaffContestSections}']"));
    }

    private static async Task ChangeContestAsync(ComponentBase page, int contest, bool submissions, int dependentId)
    {
        var type = page.GetType();
        var navigation = (NavigationManager)type.GetProperty("Navigation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        string originalUri = navigation.Uri;
        var filters = type.GetField("_filters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var draftType = filters.GetType();
        draftType.GetProperty("Contest")!.SetValue(filters, contest);
        string search = submissions ? "Author" : "Search";
        draftType.GetProperty(search)!.SetValue(filters, "draft text");
        await ActAsync(page, "SelectContestAsync");
        Assert.Equal(originalUri, navigation.Uri);
        Assert.Equal("draft text", draftType.GetProperty(search)!.GetValue(filters));
        var options = type.GetField(submissions ? "_problems" : "_organizations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        if (submissions) Assert.Equal(dependentId, Assert.Single((List<Problem>)options).Id);
        else Assert.Equal(dependentId, Assert.Single((List<Organization>)options).Id);
        if (submissions)
        {
            await ActAsync(page, "RefreshGrid");
            Assert.Same(filters, type.GetField("_filters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            Assert.Equal(originalUri, navigation.Uri);
            Assert.Null(draftType.GetProperty("Problem")!.GetValue(filters));
            Assert.NotEqual(contest, ((SubmissionList)page).Contest);
        }
        else Assert.NotEqual(contest, ((ParticipantList)page).Contest);
        await ActAsync(page, "ApplyFilters");
        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.Equal(contest.ToString(), query["Contest"].ToString());
        Assert.Equal("draft text", query[search].ToString());
        if (submissions)
        {
            Assert.False(query.ContainsKey("Problem"));
            Assert.Equal("203.0.113.42", query["IpAddress"].ToString());
        }
    }

    [Fact]
    public async Task ClearingContestAlsoClearsProblemAndKeepsIndependentFilters()
    {
        var (author, submission) = await AddIpSubmissionAsync("203.0.113.42");
        using var document = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionList>(
            $"/jury/submissions?Contest={submission.Problem.ContestId}&Problem={submission.ProblemId}&ProblemSearch=task&IpAddress=203.0.113.42&AuthorId={author.Id}", [], Staff(author.Id)));
        string url = document.QuerySelector("[data-clear-filter='Contest']")!.GetAttribute("href")!;
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        Assert.False(query.ContainsKey("Contest"));
        Assert.False(query.ContainsKey("Problem"));
        Assert.False(query.ContainsKey("ProblemSearch"));
        Assert.Equal(author.Id.ToString(), query["AuthorId"].ToString());
        Assert.Equal("203.0.113.42", query["IpAddress"].ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessagesResetStaysInCurrentContestAndTab(bool announcements)
    {
        var (author, submission) = await AddIpSubmissionAsync(null);
        int contest = submission.Problem.ContestId;
        using var document = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Messages>(
            $"/jury/contests/{contest}/messages?Announcements={announcements}&Problem={submission.ProblemId}&Status=unanswered&Author=test",
            new() { ["ContestId"] = contest }, Staff(author.Id)));
        var reset = Assert.Single(document.QuerySelectorAll("a"), item => item.TextContent == SiteResources.StaffReset);
        Assert.Equal($"jury/contests/{contest}/messages" + (announcements ? "?Announcements=true" : ""), reset.GetAttribute("href"));
    }
}
