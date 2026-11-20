using System.Net;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class ParticipantExportTests
{
    [Fact]
    public async Task AllMissingLoginsWarnWithoutPreventingDownload()
    {
        var staff = await _factory.AddUserAsync();
        await GrantRoleAsync(staff.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Missing logins", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var participant = TestEntities.CreateCompetitor(contest, "No login");
        participant.PasswordHash = "must-not-export-without-login";
        db.Competitors.Add(participant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        string path = $"/jury/contests/{contest.Id}/participants";
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantList>(path,
            new() { ["ContestId"] = contest.Id }, Staff(staff.Id, RoleNames.Jury), component => ActAsync(component, "PrepareExportAsync")));
        Assert.Equal(string.Format(SiteResources.StaffMissingLogins, 1), page.QuerySelector(".card .alert-warning")!.TextContent);
        Assert.NotNull(page.QuerySelector(".card a[data-enhance-nav='false']"));
        using var client = await _factory.LoginAsync(staff.UserName!);
        using var response = await client.GetAsync($"{path}/export?hasLogin=false", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        string csv = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(participant.Name, csv);
        Assert.DoesNotContain(participant.PasswordHash, csv);
    }

    [Fact]
    public async Task PeopleCsvExpandsRosterAndCombinesRolesOnlyForSameAccount()
    {
        var staff = await _factory.AddUserAsync();
        var member = await _factory.AddUserAsync();
        await GrantRoleAsync(staff.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var organization = new Organization { Name = "Selected university", Users = [] };
        var contest = TestEntities.CreateContest("People", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var coach = (await db.OlympUsers.FindAsync([staff.Id], TestContext.Current.CancellationToken))!;
        coach.LastName = "Coach";
        coach.FirstName = "Full";
        coach.Patronymic = "Name";
        var linked = (await db.OlympUsers.FindAsync([member.Id], TestContext.Current.CancellationToken))!;
        linked.LastName = "Linked";
        linked.FirstName = "Person";
        linked.Patronymic = null;
        var first = TestEntities.CreateCompetitor(contest, "Selected first team");
        first.Organization = organization;
        first.Coach = coach;
        first.Members.Add(linked);
        first.Members.Add(coach);
        first.MemberInfos = $"Roster alias;{linked.Id};Coach alias;{coach.Id};Same name;";
        var second = TestEntities.CreateCompetitor(contest, "Selected second team");
        second.Organization = organization;
        second.Coach = coach;
        second.Members.Add(linked);
        second.MemberInfos = $"Roster alias;{linked.Id};Same name;";
        var excluded = TestEntities.CreateCompetitor(contest, "Filtered team");
        excluded.MemberInfos = "Excluded person;";
        excluded.UserName = "not-selected-login";
        db.Competitors.AddRange(first, second, excluded);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = await _factory.LoginAsync(staff.UserName!);
        string url = $"/jury/contests/{contest.Id}/participants/export?people=true&search=Selected&organization={organization.Id}&approved=true&unofficial=false&disqualified=false&hidden=false&hasLogin=false&coach={staff.Id}&member={member.Id}";
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        byte[] data = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(data.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var lines = Encoding.UTF8.GetString(data).TrimStart('\uFEFF').Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal($"\"{SiteResources.StaffFullName}\";\"{SiteResources.StaffOrganization}\";\"{SiteResources.StaffPersonRole}\"", lines[0]);
        Assert.Single(lines, line => line == $"\"Coach Full Name\";\"{organization.Name}\";\"{SiteResources.StaffPersonParticipantAndCoach}\"");
        Assert.Single(lines, line => line == $"\"Linked Person\";\"{organization.Name}\";\"{SiteResources.StaffPersonParticipant}\"");
        Assert.Equal(2, lines.Count(line => line.StartsWith("\"Same name\";", StringComparison.Ordinal)));
        Assert.DoesNotContain("Filtered", string.Join('\n', lines));
        Assert.DoesNotContain("Roster alias", string.Join('\n', lines));
        Assert.True(response.Headers.CacheControl!.NoStore);

        using var empty = await client.GetAsync(url + "&coachSearch=missing", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Single((await empty.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task IndividualWithoutLinkedAccountIsExportedAsPerson()
    {
        var staff = await _factory.AddUserAsync();
        await GrantRoleAsync(staff.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Individual", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        contest.Abbr = "school-people";
        db.Competitors.Add(TestEntities.CreateCompetitor(contest, "Individual name"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await _factory.LoginAsync(staff.UserName!);
        using var response = await client.GetAsync($"/jury/contests/{contest.Id}/participants/export?people=true", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Contains($"\"Individual name\";\"\";\"{SiteResources.StaffPersonParticipant}\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
