using System.Net;
using System.Security.Claims;

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
public class ContestRegistrationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", false)]
    [InlineData("en", false)]
    [InlineData("ru", true)]
    [InlineData("en", true)]
    public async Task RegistrationCopiesProfileAndShowsPendingStatus(string culture, bool outOfCompetition)
    {
        var contest = await AddContestAsync();
        var user = await AddProfileAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await PostAsync(client, contest.Id, outOfCompetition);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var participant = await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .Include(item => item.Members).SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
            Assert.Equal("Иван Иванов, Test university, ПМ-01", participant.Name);
            Assert.Equal(user.Id, Assert.Single(participant.Members).Id);
            Assert.Equal(user.OrganizationId, participant.OrganizationId);
            Assert.Equal(outOfCompetition, participant.IsOutOfCompetition);
            Assert.False(participant.IsApproved);
            Assert.Null(participant.UserName);
            Assert.Null(participant.PasswordHash);
        }
        using var page = await client.GetAsync(Path(contest.Id), TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? $"Регистрация – {contest.Name}" : $"Registration – {contest.Name}", document.Title);
        Assert.Contains(culture == "ru" ? "Ожидает подтверждения" : "Pending approval", document.QuerySelector("main")!.TextContent);
        Assert.NotNull(document.QuerySelector("form input[name='_handler'][value='cancel-registration']"));
        Assert.Null(document.QuerySelector("input[name='Input.AcceptRules']"));
        Assert.Null(document.QuerySelector("input#participant-name"));
        var name = Assert.Single(document.QuerySelectorAll("main .fw-semibold"));
        Assert.Equal("Иван Иванов, Test university, ПМ-01", name.TextContent);
        Assert.NotNull(name.ParentElement!.QuerySelector(".badge"));
        using var repeated = await PostAsync(client, contest.Id, outOfCompetition);
        Assert.NotEqual(HttpStatusCode.Redirect, repeated.StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .CountAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru", false)]
    [InlineData("en", false)]
    [InlineData("ru", null)]
    [InlineData("en", null)]
    public async Task MissingGroupForcesOutOfCompetitionRegardlessOfPostedValue(string culture, bool? postedValue)
    {
        var contest = await AddContestAsync();
        var user = await AddProfileAsync(group: false);
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await client.GetAsync(Path(contest.Id), TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.NotNull(document.QuerySelector("input[name='Input.OutOfCompetition'][checked][disabled]"));
        string label = document.QuerySelector("label[for='Input.OutOfCompetition']")!.TextContent;
        label = string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(culture == "ru"
            ? "Участвовать вне конкурса (для участия в основном конкурсе укажите группу в профиле)"
            : "Participate out of competition (enter your group in your profile to participate in the main competition)", label);
        Assert.NotNull(document.QuerySelector("a[href='account/manage']"));
        using var accepted = await PostAsync(client, contest.Id, postedValue);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        using var scope = factory.Services.CreateScope();
        var participant = await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.True(participant.IsOutOfCompetition);
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Organization")]
    [InlineData("Rules")]
    public async Task RegistrationRejectsMissingProfileDataOrRulesAcceptance(string missing)
    {
        var contest = await AddContestAsync();
        var user = await AddProfileAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.OlympUsers.SingleAsync(item => item.Id == user.Id, TestContext.Current.CancellationToken);
            if (missing == "FirstName") stored.FirstName = null;
            if (missing == "LastName") stored.LastName = null;
            if (missing == "Organization") stored.OrganizationId = null;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await PostAsync(client, contest.Id, false, acceptRules: missing != "Rules");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationRemainsAvailableDuringContest()
    {
        var contest = await AddContestAsync();
        await SetTimesAsync(contest.Id, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        var user = await AddProfileAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await PostAsync(client, contest.Id, false);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var page = await client.GetAsync(Path(contest.Id), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("cancel-registration", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationRemovesOnlyOwnRegistrationAndIsBlockedAfterStart()
    {
        var contest = await AddContestAsync();
        var first = await AddProfileAsync();
        var second = await AddProfileAsync();
        using var client = await factory.LoginAsync(first.UserName!);
        using var other = await factory.LoginAsync(second.UserName!);
        using var registered = await PostAsync(client, contest.Id, false);
        using var otherRegistered = await PostAsync(other, contest.Id, false);
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, otherRegistered.StatusCode);
        using var cancelled = await PostAsync(client, contest.Id, false, handler: "cancel-registration");
        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var participants = await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Include(item => item.Members)
                .Where(item => item.ContestId == contest.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(second.Id, Assert.Single(Assert.Single(participants).Members).Id);
        }
        await SetTimesAsync(contest.Id, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var blocked = await PostAsync(other, contest.Id, false, handler: "cancel-registration");
        Assert.NotEqual(HttpStatusCode.Redirect, blocked.StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.True(await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("team")]
    [InlineData("missing")]
    [InlineData("ended")]
    public async Task UnavailableContestsCannotBeRegistered(string reason)
    {
        var contest = await AddContestAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.Contests.SingleAsync(item => item.Id == contest.Id, TestContext.Current.CancellationToken);
            stored.IsHidden = reason == "hidden";
            if (reason == "team") stored.Abbr = "acm-test";
            if (reason == "ended")
            {
                stored.StartTime = DateTimeOffset.UtcNow.AddHours(-6);
                stored.EndTime = DateTimeOffset.UtcNow.AddHours(-1);
            }
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var user = await AddProfileAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await client.GetAsync(Path(reason == "missing" ? int.MaxValue : contest.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishedContestRejectsRegistrationPageAndPreviouslyOpenedForm(bool registered)
    {
        var contest = await AddContestAsync();
        var user = await AddProfileAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        if (registered)
        {
            using var registration = await PostAsync(client, contest.Id, false);
            Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
        }
        using var page = await client.GetAsync(Path(contest.Id), TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var form = document.QuerySelector("main form")!;
        var fields = form.QuerySelectorAll("input[type='hidden'][name]")
            .ToDictionary(input => input.GetAttribute("name")!, input => input.GetAttribute("value") ?? "");
        fields["Input.AcceptRules"] = "true";
        fields["Input.OutOfCompetition"] = "false";
        await SetTimesAsync(contest.Id, DateTimeOffset.UtcNow.AddHours(-6), DateTimeOffset.UtcNow.AddHours(-1));

        using var unavailable = await client.GetAsync(Path(contest.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        using var content = new FormUrlEncodedContent(fields);
        using var posted = await client.PostAsync(Path(contest.Id), content, TestContext.Current.CancellationToken);
        Assert.False(posted.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, posted.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(registered ? 1 : 0, await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .CountAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationRequiresPermanentAccountAndCompletedEmailMigration()
    {
        var contest = await AddContestAsync();
        using var guest = factory.CreateSiteClient();
        using var challenged = await guest.GetAsync(Path(contest.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, challenged.StatusCode);
        Assert.Contains("/Account/Login", challenged.Headers.Location!.OriginalString);
        var legacy = await factory.AddUserAsync(legacyLogin: true);
        using var oldClient = await factory.LoginAsync(legacy.UserName!);
        using var migration = await oldClient.GetAsync(Path(contest.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, migration.StatusCode);
        Assert.Contains("/Account/Manage/Email", migration.Headers.Location!.OriginalString);
        var competitor = TestEntities.CreateCompetitor(contest, $"temporary_{Guid.NewGuid():N}");
        competitor.Contest = null!;
        competitor.UserName = competitor.Name;
        competitor.IsApproved = true;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.CreateAsync(competitor, WebAppFactory.Password)).Succeeded);
            Assert.True((await users.AddClaimAsync(competitor, new Claim(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        }
        using var temporary = await factory.LoginAsync(competitor.UserName);
        using var forbidden = await temporary.GetAsync(Path(contest.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, forbidden.StatusCode);
        Assert.Contains("/Account/AccessDenied", forbidden.Headers.Location!.OriginalString);
    }

    private async Task<Contest> AddContestAsync()
    {
        var contest = TestEntities.CreateContest("Individual " + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(6));
        contest.Abbr = "nstu-test";
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        context.Contests.Add(contest);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return contest;
    }

    private async Task<OlympUser> AddProfileAsync(bool group = true)
    {
        var user = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var stored = await context.OlympUsers.SingleAsync(item => item.Id == user.Id, TestContext.Current.CancellationToken);
        stored.FirstName = "Иван";
        stored.LastName = "Иванов";
        stored.Details = group ? "ПМ-01" : null;
        stored.Organization = new Organization { Name = "Test university", Users = [] };
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        user.OrganizationId = stored.OrganizationId;
        return user;
    }

    private async Task SetTimesAsync(int id, DateTimeOffset start, DateTimeOffset end)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await context.Contests.SingleAsync(item => item.Id == id, TestContext.Current.CancellationToken);
        contest.StartTime = start;
        contest.EndTime = end;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string Path(int id) => $"/contests/{id}/registration";

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, int id, bool? outOfCompetition,
        bool acceptRules = true, string handler = "contest-registration")
    {
        using var page = await client.GetAsync(Path(id), TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var fields = new Dictionary<string, string>
        {
            ["_handler"] = handler,
            ["Input.AcceptRules"] = acceptRules.ToString()
        };
        if (outOfCompetition.HasValue)
            fields["Input.OutOfCompetition"] = outOfCompetition.Value.ToString();
        foreach (var input in document.QuerySelectorAll("input[type='hidden'][name]"))
        {
            string name = input.GetAttribute("name")!;
            if (name != "_handler")
                fields[name] = input.GetAttribute("value") ?? "";
        }
        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync(Path(id), content, TestContext.Current.CancellationToken);
    }
}
