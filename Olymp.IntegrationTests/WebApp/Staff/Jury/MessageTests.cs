using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

using Xunit;

using JuryMessages = Olymp.WebApp.Components.Pages.Staff.Jury.Messages;
using JuryRunners = Olymp.WebApp.Components.Pages.Staff.Jury.Runners;
using JuryStanding = Olymp.WebApp.Components.Pages.Staff.Jury.Standing;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class MessageTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task MessageAuthorSelectionShowsAccountAndResetPreservesOtherFilters()
    {
        var author = await _factory.AddUserAsync();
        var other = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Author filter", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(4));
        var problem = TestEntities.CreateProblem(contest);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Messages.AddRange(
            new Message { Contest = null!, ContestId = contest.Id, ProblemId = problem.Id, UserId = author.Id, UserText = "Selected question" },
            new Message { Contest = null!, ContestId = contest.Id, ProblemId = problem.Id, UserId = other.Id, UserText = "Other question" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string path = $"/jury/contests/{contest.Id}/messages?AuthorId={author.Id}&Author=unrelated&Problem={problem.Id}&Status=unanswered";
        using var selected = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryMessages>(
            path, new() { ["ContestId"] = contest.Id }, Staff(author.Id)));
        var field = selected.QuerySelector("#message-author")!;
        Assert.True(field.HasAttribute("readonly"));
        Assert.Equal($"{author.Name} · ID {author.Id}", field.GetAttribute("value"));
        Assert.Contains("Selected question", selected.Body!.TextContent);
        Assert.DoesNotContain("Other question", selected.Body.TextContent);
        string reset = Assert.Single(selected.QuerySelectorAll("a[data-clear-filter]"), item => item.GetAttribute("aria-label") == string.Format(SiteResources.StaffRemoveFilter, SiteResources.SubmissionAuthor)).GetAttribute("href")!;
        Assert.DoesNotContain("Author", reset);
        Assert.Contains($"Problem={problem.Id}", reset);
        Assert.Contains("Status=unanswered", reset, StringComparison.OrdinalIgnoreCase);
        using var cleared = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryMessages>(
            reset, new() { ["ContestId"] = contest.Id }, Staff(author.Id)));
        Assert.False(cleared.QuerySelector("#message-author")!.HasAttribute("readonly"));
        Assert.Contains("Selected question", cleared.Body!.TextContent);
        Assert.Contains("Other question", cleared.Body.TextContent);
    }

    [Fact]
    public async Task MessageCrudPreservesQuestionsAndDetectsConcurrentEdits()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Messages", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        var problem = TestEntities.CreateProblem(contest);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var question = new Message { Contest = null!, ContestId = contest.Id, UserId = author.Id, UserText = "Question preserved", SendTime = DateTimeOffset.UtcNow };
        db.Messages.Add(question);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string announcementText = $"Announcement {Guid.NewGuid():N}";
        await RenderAsync<JuryMessages>($"/jury/contests/{contest.Id}/messages", new() { ["ContestId"] = contest.Id }, Staff(author.Id), async page =>
        {
            typeof(JuryMessages).GetMethod("NewAnnouncement", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            SetEditor(page, announcementText, problem.Id);
            await ActAsync(page, "SaveAsync");
            var announcement = await db.Messages.SingleAsync(item => item.JuryText == announcementText, TestContext.Current.CancellationToken);
            Assert.Null(announcement.UserId);
            Assert.Equal(problem.Id, announcement.ProblemId);
            typeof(JuryMessages).GetMethod("Edit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [question]);
            SetEditor(page, "<b>First reply</b>");
            await ActAsync(page, "SaveAsync");
            var answered = await db.Messages.AsNoTracking().SingleAsync(item => item.Id == question.Id, TestContext.Current.CancellationToken);
            Assert.Equal("<b>First reply</b>", answered.JuryText);
            typeof(JuryMessages).GetMethod("Edit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [answered]);
            SetEditor(page, "My unsaved reply");
            await db.Messages.Where(item => item.Id == question.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.JuryText, "Another jury reply"), TestContext.Current.CancellationToken);
            await ActAsync(page, "SaveAsync");
            Assert.Equal("Another jury reply", await db.Messages.Where(item => item.Id == question.Id).Select(item => item.JuryText).SingleAsync(TestContext.Current.CancellationToken));
            var current = await db.Messages.AsNoTracking().SingleAsync(item => item.Id == question.Id, TestContext.Current.CancellationToken);
            typeof(JuryMessages).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, current);
            await ActAsync(page, "DeleteAsync");
            var cleared = await db.Messages.AsNoTracking().SingleAsync(item => item.Id == question.Id, TestContext.Current.CancellationToken);
            Assert.Null(cleared.JuryText);
            Assert.Equal(question.UserText, cleared.UserText);
            Assert.Equal(question.SendTime, cleared.SendTime);
            typeof(JuryMessages).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, announcement);
            await ActAsync(page, "DeleteAsync");
            Assert.False(await db.Messages.AnyAsync(item => item.Id == announcement.Id, TestContext.Current.CancellationToken));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessageChangesRequireStoredContestAssignment(bool assigned)
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Message access", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (assigned)
        {
            db.UserClaims.Add(new IdentityUserClaim<int> { UserId = author.Id, ClaimType = ClaimNames.ContestManage, ClaimValue = contest.Id.ToString() });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        string marker = Guid.NewGuid().ToString("N");
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryMessages>(
            $"/jury/contests/{contest.Id}/messages?Announcements=true", new() { ["ContestId"] = contest.Id }, Staff(author.Id, RoleNames.Jury), async page =>
            {
                typeof(JuryMessages).GetMethod("NewAnnouncement", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
                SetEditor(page, marker);
                await ActAsync(page, "SaveAsync");
            }));
        Assert.Equal(assigned, await db.Messages.AnyAsync(item => item.ContestId == contest.Id && item.JuryText == marker, TestContext.Current.CancellationToken));
        if (!assigned)
            Assert.Contains(SiteResources.StaffAccessDenied, html.Body!.TextContent);
    }

    private static void SetEditor(JuryMessages page, string text, int? problemId = null)
    {
        var editor = typeof(JuryMessages).GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        editor.GetType().GetProperty("Text")!.SetValue(editor, text);
        editor.GetType().GetProperty("ProblemId")!.SetValue(editor, problemId);
    }

}
