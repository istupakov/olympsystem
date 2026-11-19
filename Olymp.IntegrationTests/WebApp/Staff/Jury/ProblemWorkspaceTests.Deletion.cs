using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;

using Xunit;

using JuryProblems = Olymp.WebApp.Components.Pages.Staff.Jury.Problems;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class ProblemWorkspaceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeletionRefreshesSelectedProblem(bool explicitSelection, bool hasRemainingProblem)
    {
        var author = await _factory.AddUserAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Deletion refresh", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var problem = TestEntities.CreateProblem(contest);
        problem.Name = "Deleted problem";
        db.Problems.Add(problem);
        if (hasRemainingProblem)
        {
            var remaining = TestEntities.CreateProblem(contest);
            remaining.Number = problem.Number + 1;
            remaining.Name = "Remaining problem";
            db.Problems.Add(remaining);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string path = $"/jury/contests/{contest.Id}/problems" + (explicitSelection ? $"?problemId={problem.Id}&Scope=reference" : "");
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            path, new() { [nameof(JuryProblems.ContestId)] = contest.Id }, Staff(author.Id), async page =>
            {
                await ActAsync(page, "PreviewDeletionAsync");
                await ActAsync(page, "DeleteProblemAsync");
            }));
        Assert.DoesNotContain(problem.Name, html.Body!.TextContent);
        Assert.Equal(hasRemainingProblem, html.Body.TextContent.Contains("Remaining problem", StringComparison.Ordinal));
        Assert.DoesNotContain(html.QuerySelectorAll("button"), b => b.TextContent == SiteResources.StaffConfirm);
        Assert.False(await db.Problems.AnyAsync(p => p.Id == problem.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "clarification")]
    [InlineData(true, "participant")]
    [InlineData(false, null)]
    public async Task DeletionPreviewRequiresManagementAndBlocksOnlyParticipantMessages(bool canManage, string? message)
    {
        var author = await _factory.AddUserAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Deletion", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var problem = TestEntities.CreateProblem(contest);
        db.Tests.Add(new Test { Problem = problem, Number = 1, Input = [1], Output = [2] });
        if (message is not null) db.Messages.Add(new Message
        {
            Contest = contest,
            Problem = problem,
            JuryText = "Clarification",
            User = message == "participant" ? TestEntities.CreateCompetitor(contest, "Participant") : null
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            $"/jury/contests/{contest.Id}/problems", new() { [nameof(JuryProblems.ContestId)] = contest.Id },
            Staff(author.Id, canManage ? RoleNames.Admin : RoleNames.Jury), page => ActAsync(page, "PreviewDeletionAsync")));
        Assert.Equal(canManage, html.QuerySelectorAll("button").Any(b => b.TextContent == SiteResources.StaffDeleteProblem));
        if (canManage)
        {
            var confirm = Assert.Single(html.QuerySelectorAll("button"), b => b.TextContent == SiteResources.StaffConfirm);
            Assert.Equal(message == "participant", confirm.HasAttribute("disabled"));
            Assert.Contains(string.Format(SiteResources.StaffDeleteProblemConfirm, problem.NameWithNumber, 1, 0, 0, message is null ? 0 : 1), html.Body!.TextContent);
            Assert.Equal(message == "participant", html.Body!.TextContent.Contains(SiteResources.StaffProblemDeletionBlocked, StringComparison.Ordinal));
        }
        Assert.True(await db.Problems.AnyAsync(p => p.Id == problem.Id, TestContext.Current.CancellationToken));
    }
}
