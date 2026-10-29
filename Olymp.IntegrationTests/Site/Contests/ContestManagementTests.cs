using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class ContestManagementTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Theory]
    [InlineData("en", "The file contains duplicate participant names.", "Added participants: 2; already existed: 0.")]
    [InlineData("ru", "В файле повторяются имена участников.", "Добавлено участников: 2; уже существовало: 0.")]
    public async Task ParticipantImportMessagesUseRequestCulture(string culture, string error, string status)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        var response = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile",
            "First\nfirst"u8.ToArray(), "names.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        response = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile",
            "First\nSecond"u8.ToArray(), "names.csv");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(status, WebUtility.HtmlDecode(await client.GetStringAsync(path, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("en", "Start time must be before end time.", "The file is not a PDF.", "Settings saved.")]
    [InlineData("ru", "Начало должно быть раньше окончания.", "Файл не является PDF.", "Настройки сохранены.")]
    public async Task EditingMessagesUseRequestCulture(string culture, string timeError, string pdfError, string status)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var path = $"/Contests/Edit/{contest.Id}";
        var invalid = Settings(contest.Abbr);
        invalid["Input.EndTime"] = "2030-01-01T09:00";
        var response = await SiteFixture.PostAsync(client, path, invalid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(timeError, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        response = await SiteFixture.UploadAsync(client, path + "?handler=UploadPdf", "PdfFile", "not a PDF"u8.ToArray(), "statements.pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(pdfError, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        response = await SiteFixture.PostAsync(client, path, Settings(contest.Abbr));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(status, WebUtility.HtmlDecode(await client.GetStringAsync(path, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CreateUsesPrefixedFormAndRefreshesOwnerCookie()
    {
        var user = await site.AddUserAsync(RoleNames.Jury);
        using var client = await site.LoginAsync(user);
        var html = await client.GetStringAsync("/Contests/Edit", TestContext.Current.CancellationToken);
        Assert.Contains("name=\"Input.Name\"", html);
        Assert.Contains("type=\"datetime-local\"", html);
        Assert.DoesNotContain("name=\"ProblemsFile\"", html);
        var abbr = "school" + Guid.NewGuid().ToString("N");
        var invalid = Settings(abbr);
        invalid["Input.EndTime"] = "2030-01-01T09:00";
        var invalidResponse = await SiteFixture.PostAsync(client, "/Contests/Edit", invalid);
        Assert.Equal(HttpStatusCode.OK, invalidResponse.StatusCode);
        Assert.DoesNotContain("name=\"ProblemsFile\"", await invalidResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var response = await SiteFixture.PostAsync(client, "/Contests/Edit", Settings(abbr));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/contests/edit/", location);
        // Uses a real Identity cookie obtained before the claim existed.
        Assert.Contains("name=\"Input.Name\"", await client.GetStringAsync(location, TestContext.Current.CancellationToken));
        var list = await client.GetStringAsync("/Contests", TestContext.Current.CancellationToken);
        Assert.Contains($"href=\"{location}\"", list);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.SingleAsync(x => x.Abbr == abbr, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(contest.IsHidden);
        Assert.True(await db.UserClaims.AnyAsync(x => x.UserId == user.Id && x.ClaimType == ClaimNames.ContestManage && x.ClaimValue == contest.Id.ToString(), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnlyAdminAndClaimedJuryCanManageIncludingDirectPosts()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        var other = await site.AddUserAsync(RoleNames.Jury);
        using var otherClient = await site.LoginAsync(other);
        var edit = $"/Contests/Edit/{contest.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await otherClient.GetAsync(edit, TestContext.Current.CancellationToken)).StatusCode);
        var denied = await SiteFixture.PostAsync(otherClient, edit + "?handler=Unfreeze", [], "/Contests/Edit");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("AccessDenied", denied.Headers.Location!.ToString());
        using var scope = site.Services.CreateScope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<OlympContext>().Contests.SingleAsync(x => x.Id == contest.Id, cancellationToken: TestContext.Current.CancellationToken)).FreezeTime);
        using var ownerClient = await site.LoginAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync(edit, TestContext.Current.CancellationToken)).StatusCode);
        var admin = await site.AddUserAsync(RoleNames.Admin);
        using var adminClient = await site.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync(edit, TestContext.Current.CancellationToken)).StatusCode);
        var ordinary = await site.AddUserAsync();
        using var ordinaryClient = await site.LoginAsync(ordinary);
        Assert.Contains("AccessDenied", (await ordinaryClient.GetAsync("/Contests/Edit", TestContext.Current.CancellationToken)).Headers.Location!.ToString());
        // A claim without Jury does not grant management rights.
        Assert.False(Olymp.Site.Services.Authorization.ContestAccess.CanManage(
            new ClaimsPrincipal(new ClaimsIdentity([new(ClaimNames.ContestManage, contest.Id.ToString())], "test")), contest.Id));
    }

    [Fact]
    public async Task FormsValidateTimesUploadPdfAndUnfreeze()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Edit/{contest.Id}";
        var invalid = Settings(contest.Abbr);
        invalid["Input.EndTime"] = "2030-01-01T09:00";
        var response = await SiteFixture.PostAsync(client, path, invalid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Начало должно быть раньше окончания", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var valid = Settings(contest.Abbr);
        valid["Input.Description"] = "<p>Описание</p>";
        valid["Input.IsHidden"] = "false";
        valid["Input.IsOpen"] = "true";
        valid["Input.FreezeTime"] = "2030-01-01T14:00";
        valid["Input.ProblemShowTime"] = "2030-01-01T09:30";
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path, valid)).StatusCode);
        using (var settingsScope = site.Services.CreateScope())
        {
            var settings = await settingsScope.ServiceProvider.GetRequiredService<OlympContext>().Contests.SingleAsync(x => x.Id == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Новый контест", settings.Name);
            Assert.Equal("<p>Описание</p>", settings.Description);
            Assert.False(settings.IsHidden);
            Assert.True(settings.IsOpen);
            Assert.Equal(new DateTime(2030, 1, 1, 10, 0, 0), settings.StartTime.LocalDateTime);
            Assert.Equal(new DateTime(2030, 1, 1, 15, 0, 0), settings.EndTime.LocalDateTime);
            Assert.Equal(new DateTime(2030, 1, 1, 14, 0, 0), settings.FreezeTime!.Value.LocalDateTime);
            Assert.Equal(new DateTime(2030, 1, 1, 9, 30, 0), settings.ProblemShowTime!.Value.LocalDateTime);
        }
        response = await SiteFixture.UploadAsync(client, path + "?handler=UploadPdf", "PdfFile", "%PDF-1.4\ntest"u8.ToArray(), "statements.pdf");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        response = await client.GetAsync($"/Contests/DownloadProblems/{contest.Id}", TestContext.Current.CancellationToken);
        Assert.Equal("%PDF-1.4\ntest"u8.ToArray(), await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        int blobId;
        using (var before = site.Services.CreateScope())
            blobId = (await before.ServiceProvider.GetRequiredService<OlympContext>().Contests.Where(x => x.Id == contest.Id).Select(x => x.ProblemPdfId).SingleAsync(cancellationToken: TestContext.Current.CancellationToken))!.Value;
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=Unfreeze", [])).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=RemovePdf", [])).StatusCode);
        using var scope = site.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Contests.SingleAsync(x => x.Id == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(saved.FreezeTime);
        Assert.Null(saved.ProblemPdfId);
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Blobs.AnyAsync(x => x.Id == blobId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CsvIsIdempotentAndExistingLoginButtonsProduceUsableAccounts()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        foreach (var text in new[] { "\uFEFFИванов Иван\nПетров Пётр\n", " иВаНоВ иВаН \nПетров Пётр\nСидорова Анна\n" })
        {
            var response = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile", Encoding.UTF8.GetBytes(text), "names.csv");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
        var invalid = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile", Encoding.UTF8.GetBytes("Новый\nПовтор\n повтор \n"), "names.csv");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.Equal(3, await db.Competitors.CountAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(await db.Competitors.Where(x => x.ContestId == contest.Id).AllAsync(x => x.IsApproved && x.UserName == null, cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=CreateRandomLogins", [])).StatusCode);
        Competitor competitor;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            competitor = await db.Competitors.FirstAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.StartsWith("olymp", competitor.UserName);
            Assert.True(await db.UserClaims.AnyAsync(x => x.UserId == competitor.Id && x.ClaimType == ClaimNames.CompetitorContest, cancellationToken: TestContext.Current.CancellationToken));
            await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, false), cancellationToken: TestContext.Current.CancellationToken);
        }
        using var participantClient = await site.LoginAsync(competitor, competitor.PasswordHash!);
        Assert.Equal(HttpStatusCode.OK, (await participantClient.GetAsync($"/Contests/Submits/{contest.Id}", TestContext.Current.CancellationToken)).StatusCode);
        var previous = competitor.UserName;
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=CreateRandomLogins", [])).StatusCode);
        using (var scope = site.Services.CreateScope())
            Assert.Equal(previous, (await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.SingleAsync(x => x.Id == competitor.Id, cancellationToken: TestContext.Current.CancellationToken)).UserName);
    }

    [Fact]
    public async Task ParticipantPostsCannotModifyAnotherContestAndTeamImportIsRejected()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        var other = await site.AddContestAsync();
        int victim;
        using (var scope = site.Services.CreateScope())
        {
            var competitor = new Competitor
            {
                ContestId = other.Id,
                Contest = null!,
                Name = "Другой",
                IsApproved = true,
                Members = [],
                Messages = [],
                Submissions = []
            };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(competitor)).Succeeded);
            victim = competitor.Id;
        }
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        var response = await SiteFixture.PostAsync(client, path, new() { ["userId"] = victim.ToString(), ["action"] = "Disqualify" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var teamContest = await site.AddContestAsync(owner, "acm");
        // Log in again to pick up the newly granted claim.
        using var teamClient = await site.LoginAsync(owner);
        response = await SiteFixture.UploadAsync(teamClient, $"/Contests/Competitors/Manage/{teamContest.Id}?handler=UploadParticipants",
            "ParticipantsFile", Encoding.UTF8.GetBytes("Иванов"), "names.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var finalScope = site.Services.CreateScope();
        var db = finalScope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False((await db.Competitors.SingleAsync(x => x.Id == victim, cancellationToken: TestContext.Current.CancellationToken)).IsDisqualified);
        Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == teamContest.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static Dictionary<string, string> Settings(string abbr) => new()
    {
        ["Input.Name"] = "Новый контест",
        ["Input.Abbr"] = abbr,
        ["Input.StartTime"] = "2030-01-01T10:00",
        ["Input.EndTime"] = "2030-01-01T15:00",
        ["Input.IsHidden"] = "true",
        ["Input.IsOpen"] = "false"
    };
}
