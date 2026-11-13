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
using Olymp.WebApp.Components.Pages.Staff.Admin;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public class ParticipantDetailsTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task AdminOpensEditingExplicitlyAndKeepsReturnContext()
    {
        var (participant, member) = await AddParticipantAsync();
        string returnTo = $"/jury/participants?Member={member.Id}&row=8";
        string path = Path(participant) + "?returnTo=" + Uri.EscapeDataString(returnTo);
        using var view = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantDetails>(path, Parameters(participant), Staff(member.Id)));
        Assert.Null(view.QuerySelector("#participant-name"));
        Assert.Contains("Legacy roster", view.Body!.TextContent);
        Assert.Contains(view.QuerySelectorAll("a"), link => link.TextContent == SiteResources.StaffEdit && link.GetAttribute("href")!.Contains("edit=true", StringComparison.OrdinalIgnoreCase));
        Assert.Null(view.QuerySelector("details[open]"));
        using var editor = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantDetails>(path + "&edit=true", Parameters(participant), Staff(member.Id)));
        Assert.NotNull(editor.QuerySelector("#participant-name"));
        var cancel = Assert.Single(editor.QuerySelectorAll("a"), link => link.TextContent == SiteResources.StaffCancel);
        string url = cancel.GetAttribute("href")!;
        Assert.DoesNotContain("edit=", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Uri.EscapeDataString(returnTo), url);
    }

    [Fact]
    public async Task EditQueryDoesNotGrantJuryAccessToParticipantEditing()
    {
        var (participant, member) = await AddParticipantAsync();
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantDetails>(
            Path(participant) + "?edit=true", Parameters(participant), Staff(member.Id, RoleNames.Jury), async component =>
            {
                typeof(ParticipantDetails).GetField("_participantName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "Forbidden edit");
                await ActAsync(component, "RenameParticipantAsync");
            }));
        Assert.Null(page.QuerySelector("#participant-name"));
        Assert.Contains("Legacy roster", page.Body!.TextContent);
        Assert.Contains(SiteResources.StaffAccessDenied, page.Body.TextContent);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(participant.Name, await db.Competitors.Where(item => item.Id == participant.Id).Select(item => item.Name).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JuryReadsParticipantRosterAndCredentialsWithoutAdminLinksOrEditing()
    {
        var (participant, member) = await AddParticipantAsync();
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantDetails>(
            Path(participant), Parameters(participant), Staff(member.Id, RoleNames.Jury)));
        Assert.Contains(participant.Name, page.Body!.TextContent);
        Assert.Contains("Legacy roster", page.Body.TextContent);
        Assert.Contains("assigned-password", page.Body.TextContent);
        Assert.Contains(page.QuerySelectorAll("a"), link => link.GetAttribute("href") == $"jury/contests/{participant.ContestId}/messages?AuthorId={participant.Id}");
        Assert.DoesNotContain(page.QuerySelectorAll("a"), link => link.GetAttribute("href")?.StartsWith("admin/", StringComparison.Ordinal) == true);
        Assert.Null(page.QuerySelector("#participant-name"));
        Assert.Empty(page.QuerySelectorAll("button"));
        Assert.DoesNotContain(member.Email!, page.Body.TextContent);
    }

    [Fact]
    public async Task AssignedJuryManagesStatusesButCannotRenameOrBlockLogin()
    {
        var (participant, member) = await AddParticipantAsync();
        await SetAssignmentAsync(member.Id, participant.ContestId, true);
        var actor = Staff(member.Id, RoleNames.Jury);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantDetails>(
            Path(participant), Parameters(participant), actor));
        Assert.Contains(page.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffDisqualify);
        Assert.DoesNotContain(page.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffBan);
        await RunActionAsync(participant, actor, "Disqualify");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.True((await db.Competitors.SingleAsync(item => item.Id == participant.Id, TestContext.Current.CancellationToken)).IsDisqualified);
        var service = scope.ServiceProvider.GetRequiredService<AdminParticipantService>();
        await Assert.ThrowsAsync<OperationValidationException>(() => service.RenameAsync(actor, participant.Id, "Denied", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SetLoginBlockedAsync(actor, participant.Id, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManagementRechecksRevokedAssignmentAndRouteContest()
    {
        var (participant, member) = await AddParticipantAsync();
        await SetAssignmentAsync(member.Id, participant.ContestId, true);
        var actor = Staff(member.Id, RoleNames.Jury);
        string html = await RenderAsync<ParticipantDetails>(Path(participant), Parameters(participant), actor, async page =>
        {
            await SetAssignmentAsync(member.Id, participant.ContestId, false);
            await AskAndExecuteAsync(page, "Disqualify");
        });
        Assert.Contains(SiteResources.StaffAccessDenied, System.Net.WebUtility.HtmlDecode(html));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False((await db.Competitors.SingleAsync(item => item.Id == participant.Id, TestContext.Current.CancellationToken)).IsDisqualified);
        var parameters = Parameters(participant);
        parameters["ContestId"] = -1;
        string wrongContest = await RenderAsync<ParticipantDetails>($"/jury/contests/-1/participants/{participant.Id}", parameters, Staff(member.Id), async page =>
        {
            typeof(ParticipantDetails).GetField("_participantName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, "Wrong contest");
            await ActAsync(page, "RenameParticipantAsync");
            await AskAndExecuteAsync(page, "Ban");
        });
        Assert.DoesNotContain("assigned-password", wrongContest);
        Assert.Equal(participant.Name, await db.Competitors.Where(item => item.Id == participant.Id).Select(item => item.Name).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Null(await db.Competitors.Where(item => item.Id == participant.Id).Select(item => item.LockoutEnd).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdminEditsParticipantAndInvalidatesSessionWhenBlockingLogin()
    {
        var (participant, member) = await AddParticipantAsync();
        string html = await RenderAsync<ParticipantDetails>(Path(participant), Parameters(participant), Staff(member.Id), async page =>
        {
            typeof(ParticipantDetails).GetField("_participantName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, "Updated participant");
            await ActAsync(page, "RenameParticipantAsync");
            await AskAndExecuteAsync(page, "Ban");
        });
        Assert.Contains("Updated participant", html);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var updated = await db.Competitors.SingleAsync(item => item.Id == participant.Id, TestContext.Current.CancellationToken);
        Assert.True(updated.LockoutEnabled);
        Assert.True(updated.LockoutEnd > DateTimeOffset.UtcNow);
        Assert.NotEqual(participant.SecurityStamp, updated.SecurityStamp);
        await scope.ServiceProvider.GetRequiredService<AdminParticipantService>().SetLoginBlockedAsync(Staff(member.Id), participant.Id, false, TestContext.Current.CancellationToken);
        Assert.Null(updated.LockoutEnd);
    }

    [Fact]
    public async Task PermanentAccountLinksToSeparateParticipationAndCoachingLists()
    {
        var (participant, member) = await AddParticipantAsync();
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditUser>(
            $"/admin/users/{member.Id}/edit", new() { ["Id"] = member.Id }, Staff(member.Id)));
        Assert.Contains(page.QuerySelectorAll("a"), link => link.GetAttribute("href") == $"jury/participants?Member={member.Id}" && link.TextContent.Contains('1'));
        Assert.Contains(page.QuerySelectorAll("a"), link => link.GetAttribute("href") == $"jury/participants?Coach={member.Id}" && link.TextContent.Contains('1'));
        Assert.Contains(page.QuerySelectorAll("a"), link => link.GetAttribute("href") == $"admin/users?Coach={member.Id}" && link.TextContent.Contains('0'));
        string oldCard = await RenderAsync<EditUser>($"/admin/users/{participant.Id}/edit", new() { ["Id"] = participant.Id }, Staff(member.Id));
        Assert.DoesNotContain("assigned-password", oldCard);
        Assert.Contains(SiteResources.StaffUserNotFound, System.Net.WebUtility.HtmlDecode(oldCard));
    }

    [Fact]
    public async Task GlobalListCombinesExactRelationshipsAndSearchAndUsesParticipantCards()
    {
        var (participant, member) = await AddParticipantAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var list = new ParticipantList { Member = member.Id, Coach = member.Id, Contest = participant.ContestId, CoachSearch = member.Name, MemberSearch = member.Name };
        Assert.Equal(participant.Id, Assert.Single(await list.Query(db).ToListAsync(TestContext.Current.CancellationToken)).Id);
        list.Member = -1;
        Assert.Empty(await list.Query(db).ToListAsync(TestContext.Current.CancellationToken));
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantList>(
            $"/jury/participants?Member={member.Id}", [], Staff(member.Id, RoleNames.Jury)));
        Assert.NotNull(page.QuerySelector("#participant-contest"));
        Assert.DoesNotContain(page.QuerySelectorAll("a"), link => link.GetAttribute("href")?.EndsWith("/participants/export", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(page.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffApproveAll);
    }

    [Fact]
    public async Task SubmissionLinksUseTheAuthorsOwnContestForParticipantCards()
    {
        var (participant, member) = await AddParticipantAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Another contest", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var submission = TestEntities.CreateSubmission(participant, TestEntities.CreateProblem(contest), TestEntities.CreateCompilator(), DateTimeOffset.UtcNow);
        submission.User = null!;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var actor = Staff(member.Id, RoleNames.Jury);
        var row = Assert.Single(await ReadStaffRowsAsync($"/jury/contests/{contest.Id}/submissions?AuthorId={participant.Id}", contest.Id, actor));
        Assert.Equal(participant.ContestId, row.AuthorContestId);
        Assert.False(row.IsParticipant);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionDetails>(
            $"/jury/submissions/{submission.Id}", new() { ["Id"] = submission.Id }, actor));
        Assert.Contains(page.QuerySelectorAll("a"), link => link.GetAttribute("href")?.StartsWith(Path(participant).TrimStart('/'), StringComparison.Ordinal) == true);
        Assert.DoesNotContain(page.QuerySelectorAll("a"), link => link.GetAttribute("href")?.StartsWith("admin/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ContestListRetainsApprovalForAssignedJury()
    {
        var (participant, member) = await AddParticipantAsync();
        await SetAssignmentAsync(member.Id, participant.ContestId, true);
        await RenderAsync<ParticipantList>($"/jury/contests/{participant.ContestId}/participants?Member={member.Id}", new() { ["ContestId"] = participant.ContestId }, Staff(member.Id, RoleNames.Jury), async page =>
        {
            var action = typeof(ParticipantList).GetNestedType("ParticipantAction", BindingFlags.NonPublic)!;
            await ActAsync(page, "Ask", Enum.Parse(action, "Disapprove"), participant);
        });
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Competitors.Where(item => item.Id == participant.Id).Select(item => item.IsApproved).SingleAsync(TestContext.Current.CancellationToken));
    }

    private async Task<(Competitor Participant, OlympUser Member)> AddParticipantAsync()
    {
        var member = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Participant details", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
        var participant = TestEntities.CreateCompetitor(contest, $"Participant {Guid.NewGuid():N}");
        participant.CoachId = member.Id;
        participant.UserName = $"participant-{Guid.NewGuid():N}";
        participant.PasswordHash = "assigned-password";
        participant.SecurityStamp = Guid.NewGuid().ToString();
        participant.IsApproved = true;
        participant.Members.Add((await db.OlympUsers.FindAsync([member.Id], TestContext.Current.CancellationToken))!);
        participant.MemberInfos = $"{member.Name};{member.Id};Legacy roster;";
        db.Competitors.Add(participant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (participant, member);
    }

    private async Task SetAssignmentAsync(int userId, int contestId, bool enabled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        if (enabled) db.UserClaims.Add(new IdentityUserClaim<int> { UserId = userId, ClaimType = ClaimNames.ContestManage, ClaimValue = contestId.ToString() });
        else await db.UserClaims.Where(item => item.UserId == userId && item.ClaimType == ClaimNames.ContestManage).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
    private Task<string> RunActionAsync(Competitor participant, ClaimsPrincipal actor, string action) => RenderAsync<ParticipantDetails>(Path(participant), Parameters(participant), actor, page => AskAndExecuteAsync(page, action));
    private static async Task AskAndExecuteAsync(ParticipantDetails page, string action)
    {
        var type = typeof(ParticipantDetails).GetNestedType("ParticipantAction", BindingFlags.NonPublic)!;
        await ActAsync(page, "Ask", Enum.Parse(type, action));
        await ActAsync(page, "ExecuteAsync");
    }
    private static string Path(Competitor participant) => $"/jury/contests/{participant.ContestId}/participants/{participant.Id}";
    private static Dictionary<string, object?> Parameters(Competitor participant) => new() { ["Id"] = participant.Id, ["ContestId"] = participant.ContestId };
}
