using System.Data;
using System.Data.Common;
using System.Net;
using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Shared;
using Olymp.Site;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class RegistrationIntegrityTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Theory]
    [InlineData("Approve")]
    [InlineData("Disapprove")]
    [InlineData("Disqualify")]
    [InlineData("Delete")]
    [InlineData("ApproveAll")]
    public async Task ParticipantManagementReportsIdentityFailures(string action)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        int id;
        using (var scope = site.Services.CreateScope())
        {
            var competitor = Participant(contest.Id);
            competitor.IsApproved = action is not ("Approve" or "ApproveAll");
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(competitor)).Succeeded);
            id = competitor.Id;
        }
        using var factory = WithFailures();
        using var client = factory.CreateClient(ClientOptions());
        await LoginAsync(client, owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}" + (action == "ApproveAll" ? "?handler=ApproveAll" : "");
        var response = await SiteFixture.PostAsync(client, path, new() { ["userId"] = id.ToString(), ["action"] = action });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Injected failure", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var final = site.Services.CreateScope();
        var saved = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors.SingleAsync(x => x.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(action is not ("Approve" or "ApproveAll"), saved.IsApproved);
        Assert.False(saved.IsDisqualified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationReportsFailedCreationAndDeletion(bool team)
    {
        var setup = await SetupAsync(team);
        using var factory = WithFailures();
        using var client = factory.CreateClient(ClientOptions());
        await LoginAsync(client, team ? setup.Coach : setup.Member);
        var path = team ? TeamPath(setup) : $"/Contests/Registration/{setup.Contest.Id}";
        var response = await SiteFixture.PostAsync(client, team ? path : path + "?handler=Create",
            team ? TeamValues(setup.Member.Id) : new() { ["ooc"] = "true" }, path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Injected failure", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        int id;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == setup.Contest.Id, cancellationToken: TestContext.Current.CancellationToken));
            db.Attach(setup.Member);
            var competitor = Participant(setup.Contest.Id);
            competitor.OrganizationId = setup.OrgId;
            competitor.CoachId = team ? setup.Coach.Id : null;
            competitor.MemberInfos = $"Первый;{setup.Member.Id};Второй;;Третий;";
            competitor.Members.Add(setup.Member);
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(competitor)).Succeeded);
            id = competitor.Id;
        }
        path = team ? $"/Contests/Registration/Teams/{setup.Contest.Id}" : $"/Contests/Registration/{setup.Contest.Id}";
        response = await SiteFixture.PostAsync(client, path + "?handler=Delete", new() { ["teamId"] = id.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var final = site.Services.CreateScope();
        Assert.True(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(x => x.Id == id && x.Members.Any(m => m.Id == setup.Member.Id), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationHandlesDatabaseExceptionWithoutSqlServerErrorCodes()
    {
        var setup = await SetupAsync(team: true);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OlympContext>(options => options.AddInterceptors(new OtherProviderFailure(deleting: false)))));
        using var client = factory.CreateClient(ClientOptions());
        await LoginAsync(client, setup.Coach);
        var path = TeamPath(setup);
        var response = await SiteFixture.PostAsync(client, path, TeamValues(setup.Member.Id), path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Не удалось сохранить регистрацию", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var scope = site.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(x => x.ContestId == setup.Contest.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteHandlesDatabaseUpdateExceptionWithoutGuessingItsCause()
    {
        var setup = await SetupAsync(team: true);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OlympContext>(options => options.AddInterceptors(new OtherProviderFailure(deleting: true)))));
        using var client = factory.CreateClient(ClientOptions());
        await LoginAsync(client, setup.Coach);
        var path = TeamPath(setup);
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path, TeamValues(setup.Member.Id), path)).StatusCode);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var id = await db.Competitors.Where(x => x.ContestId == setup.Contest.Id).Select(x => x.Id).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var response = await SiteFixture.PostAsync(client, $"/Contests/Registration/Teams/{setup.Contest.Id}?handler=Delete",
            new() { ["teamId"] = id.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Не удалось удалить участника", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(await db.Competitors.AnyAsync(x => x.Id == id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TeamCannotReuseMemberOrRepeatIdWithLeadingZeros()
    {
        var setup = await SetupAsync(team: true);
        using var client = await site.LoginAsync(setup.Coach);
        var path = TeamPath(setup);
        var repeated = TeamValues(setup.Member.Id);
        repeated["Input.Participant2"] = "000" + setup.Member.Id;
        Assert.Equal(HttpStatusCode.OK, (await SiteFixture.PostAsync(client, path, repeated, path)).StatusCode);
        using (var scope = site.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(x => x.ContestId == setup.Contest.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path, TeamValues(setup.Member.Id), path)).StatusCode);
        var response = await SiteFixture.PostAsync(client, path, TeamValues(setup.Member.Id), path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("уже зарегистрированы", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        using var final = site.Services.CreateScope();
        Assert.Equal(1, await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors.CountAsync(x => x.ContestId == setup.Contest.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentTeamRequestsCannotEnrollMemberTwice()
    {
        var setup = await SetupAsync(team: true);
        var barrier = new SqlReadBarrier("[Participations]");
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OlympContext>(options => options.AddInterceptors(barrier))));
        using var first = factory.CreateClient(ClientOptions());
        using var second = factory.CreateClient(ClientOptions());
        await LoginAsync(first, setup.Coach);
        await LoginAsync(second, setup.Coach);
        var path = TeamPath(setup);
        var one = TeamValues(setup.Member.Id);
        var two = TeamValues(setup.Member.Id);
        one["__RequestVerificationToken"] = await SiteFixture.TokenAsync(first, path);
        two["__RequestVerificationToken"] = await SiteFixture.TokenAsync(second, path);
        var responses = await Task.WhenAll(first.PostAsync(path, new FormUrlEncodedContent(one), TestContext.Current.CancellationToken),
            second.PostAsync(path, new FormUrlEncodedContent(two), TestContext.Current.CancellationToken)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        foreach (var response in responses)
            if (response.StatusCode == HttpStatusCode.InternalServerError)
                Assert.Fail(string.Join('\n', (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Split('\n').Take(10)));
        Assert.Equal(2, barrier.Arrivals);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Redirect);
        Assert.All(responses, x => Assert.True(x.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK or HttpStatusCode.BadRequest));
        using var scope = site.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .CountAsync(x => x.ContestId == setup.Contest.Id && x.Members.Any(m => m.Id == setup.Member.Id), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentIndividualRequestsCannotRegisterTwice()
    {
        var setup = await SetupAsync(team: false);
        var barrier = new SqlReadBarrier("[Participations]", existsQuery: false);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OlympContext>(options => options.AddInterceptors(barrier))));
        using var first = factory.CreateClient(ClientOptions());
        using var second = factory.CreateClient(ClientOptions());
        await LoginAsync(first, setup.Member);
        await LoginAsync(second, setup.Member);
        var path = $"/Contests/Registration/{setup.Contest.Id}";
        var one = new Dictionary<string, string> { ["ooc"] = "true", ["__RequestVerificationToken"] = await SiteFixture.TokenAsync(first, path) };
        var two = new Dictionary<string, string> { ["ooc"] = "true", ["__RequestVerificationToken"] = await SiteFixture.TokenAsync(second, path) };
        var responses = await Task.WhenAll(first.PostAsync(path + "?handler=Create", new FormUrlEncodedContent(one), TestContext.Current.CancellationToken),
            second.PostAsync(path + "?handler=Create", new FormUrlEncodedContent(two), TestContext.Current.CancellationToken)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        foreach (var response in responses)
            if (response.StatusCode == HttpStatusCode.InternalServerError)
                Assert.Fail(string.Join('\n', (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Split('\n').Take(10)));
        Assert.True(barrier.Arrivals >= 2);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Redirect);
        Assert.All(responses, x => Assert.True(x.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest));
        using var scope = site.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .CountAsync(x => x.ContestId == setup.Contest.Id && x.Members.Any(m => m.Id == setup.Member.Id), cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<(OlympUser Member, OlympUser Coach, Contest Contest, int OrgId)> SetupAsync(bool team)
    {
        var member = await site.AddUserAsync();
        var coach = await site.AddUserAsync();
        var contest = await site.AddContestAsync(prefix: team ? "acm" : "school");
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var org = new Organization { Name = "Test org", ACMName = "NSTU", Users = [] };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        await db.OlympUsers.Where(x => x.Id == member.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, org.Id)
            .SetProperty(x => x.FirstName, "Иван").SetProperty(x => x.LastName, "Иванов").SetProperty(x => x.Details, "A1"));
        await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, false)
            .SetProperty(x => x.StartTime, DateTimeOffset.Now.AddHours(1)));
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().AddClaimAsync(coach, new(ClaimNames.CoachOrg, org.Id.ToString()))).Succeeded);
        return (member, coach, contest, org.Id);
    }

    private WebApplicationFactory<SharedResource> WithFailures() => site.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => services.AddScoped<UserManager<User>, FailingUserManager>()));
    private static Competitor Participant(int contestId) => new()
    {
        Name = "Test participant",
        ContestId = contestId,
        Contest = null!,
        Members = [],
        Messages = [],
        Submissions = []
    };
    private static WebApplicationFactoryClientOptions ClientOptions() => new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") };
    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, OlympUser user) => SiteFixture.PostAsync(client, "/Identity/Account/Login", new()
    {
        ["Input.Username"] = user.UserName!,
        ["Input.Password"] = SiteFixture.Password
    });
    private static string TeamPath((OlympUser Member, OlympUser Coach, Contest Contest, int OrgId) setup) =>
        $"/Contests/Registration/Teams/Create/{setup.Contest.Id}?orgId={setup.OrgId}";
    private static Dictionary<string, string> TeamValues(int memberId) => new()
    {
        ["Input.TeamPrefix"] = "NSTU",
        ["Input.Participant1"] = memberId.ToString(),
        ["Input.Participant2"] = "Второй",
        ["Input.Participant3"] = "Третий",
        ["Input.Confirmation"] = "true"
    };

    private sealed class FailingUserManager(IUserStore<User> store, IOptions<IdentityOptions> options,
        IPasswordHasher<User> hasher, IEnumerable<IUserValidator<User>> userValidators,
        IEnumerable<IPasswordValidator<User>> passwordValidators, ILookupNormalizer normalizer,
        IdentityErrorDescriber errors, IServiceProvider services, ILogger<UserManager<User>> logger)
        : UserManager<User>(store, options, hasher, userValidators, passwordValidators, normalizer, errors, services, logger)
    {
        private static Task<IdentityResult> Failure() => Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Injected failure" }));
        public override Task<IdentityResult> UpdateAsync(User user) => user is Competitor ? Failure() : base.UpdateAsync(user);
        public override Task<IdentityResult> DeleteAsync(User user) => user is Competitor ? Failure() : base.DeleteAsync(user);
        public override Task<IdentityResult> CreateAsync(User user) => user is Competitor ? Failure() : base.CreateAsync(user);
    }

    private sealed class OtherProviderException() : DbException("Synthetic database error");

    private sealed class OtherProviderFailure(bool deleting) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (deleting && command.CommandText.Contains("DELETE FROM", StringComparison.Ordinal))
                throw new DbUpdateException("Synthetic write failure", new OtherProviderException());
            if (!deleting && command.Transaction?.IsolationLevel == IsolationLevel.Serializable
                && command.CommandText.Contains("SELECT CASE", StringComparison.Ordinal)
                && command.CommandText.Contains("[Participations]", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic provider wrapper", new OtherProviderException());
            return ValueTask.FromResult(result);
        }
    }
}
