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

}
