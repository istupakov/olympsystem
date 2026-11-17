using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class LegacyProblemCleanupPageTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task OnlyAdminSeesCleanupInContestOneAndConfirmationPrecedesDeletion()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = await _factory.AddUserAsync();
        var jury = await _factory.AddUserAsync();
        await GrantRoleAsync(admin.Id, RoleNames.Admin);
        await GrantRoleAsync(jury.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Legacy cleanup", DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(-1).AddHours(5));
        var other = TestEntities.CreateContest("Other", contest.StartTime, contest.EndTime);
        other.Abbr = "other";
        db.Contests.AddRange(contest, other);
        await db.SaveChangesAsync(token);
        Assert.Equal(1, contest.Id);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.AddClaimAsync((await users.FindByIdAsync(jury.Id.ToString()))!, new Claim(ClaimNames.ContestManage, "1"))).Succeeded);
        var first = TestEntities.CreateProblem(contest, 6);
        first.Id = 3554;
        var second = TestEntities.CreateProblem(contest, 9, first.Checker);
        second.Id = 3582;
        db.Tests.AddRange(
            new Test { Problem = first, Number = 1, Input = [1], Output = [2] },
            new Test { Problem = second, Number = 1, Input = [3], Output = [4] });
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT Problems ON", token);
            await db.SaveChangesAsync(token);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT Problems OFF", token);
            await db.Database.CloseConnectionAsync();
        }
        string path = $"/jury/contests/{contest.Id}/edit";
        using var initial = await PageAsync(contest.Id, admin.Id, RoleNames.Admin);
        Assert.Contains(initial.QuerySelectorAll("button"), item => item.TextContent == SiteResources.StaffCleanupLegacyProblems);
        Assert.DoesNotContain(SiteResources.StaffCleanupLegacyProblemsConfirm, initial.Body!.TextContent);
        using var juryPage = await PageAsync(contest.Id, jury.Id, RoleNames.Jury);
        Assert.DoesNotContain(juryPage.QuerySelectorAll("button"), item => item.TextContent == SiteResources.StaffCleanupLegacyProblems);
        using var otherPage = await PageAsync(other.Id, admin.Id, RoleNames.Admin);
        Assert.DoesNotContain(otherPage.QuerySelectorAll("button"), item => item.TextContent == SiteResources.StaffCleanupLegacyProblems);
        using var confirmation = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(path, new() { ["Id"] = contest.Id }, Staff(admin.Id), component =>
        {
            SelectCleanup(component);
            return Task.CompletedTask;
        }));
        Assert.Contains(SiteResources.StaffCleanupLegacyProblemsConfirm, confirmation.Body!.TextContent);
        Assert.Contains(confirmation.QuerySelectorAll("button"), item => item.TextContent == SiteResources.StaffConfirm);
        Assert.Equal(2, await db.Problems.CountAsync(token));
        Assert.Equal(2, await db.Tests.CountAsync(token));
        using var completed = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(path, new() { ["Id"] = contest.Id }, Staff(admin.Id), async component =>
        {
            SelectCleanup(component);
            await ActAsync(component, "RunOperationAsync");
        }));
        Assert.Contains(SiteResources.StaffOperationCompleted, completed.Body!.TextContent);
        Assert.DoesNotContain(completed.QuerySelectorAll("button"), item => item.TextContent == SiteResources.StaffCleanupLegacyProblems);
        Assert.Equal(0, await db.Problems.CountAsync(token));
        Assert.Equal(0, await db.Tests.CountAsync(token));
    }

    private async Task<AngleSharp.Html.Dom.IHtmlDocument> PageAsync(int contestId, int userId, string role) =>
        await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>($"/jury/contests/{contestId}/edit", new() { ["Id"] = contestId }, Staff(userId, role)));

    private static void SelectCleanup(EditContest component)
    {
        var field = typeof(EditContest).GetField("_pendingOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(component, Enum.Parse(Nullable.GetUnderlyingType(field.FieldType)!, "CleanupLegacyProblems"));
    }
}
