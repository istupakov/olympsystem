using System.Net;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class FailureTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Fact]
    public async Task FailedOwnerClaimRollsBackContestAndFailedParticipantRollsBackWholeCsv()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var failing = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<UserManager<User>, FailingUserManager>()));
        using var client = failing.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = owner.UserName!,
            ["Input.Password"] = SiteFixture.Password
        })).StatusCode);
        var abbr = "failure" + Guid.NewGuid().ToString("N");
        var response = await SiteFixture.PostAsync(client, "/Contests/Edit", new()
        {
            ["Input.Name"] = "Не сохранять",
            ["Input.Abbr"] = abbr,
            ["Input.StartTime"] = "2030-01-01T10:00",
            ["Input.EndTime"] = "2030-01-01T15:00"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("name=\"ProblemsFile\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        response = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile",
            Encoding.UTF8.GetBytes("Сохранён первым\nСбой\n"), "names.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Contests.AnyAsync(x => x.Abbr == abbr, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportedParticipantsReceiveUploadedLoginsAndAdminCanViewTheirCards()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants",
            "ParticipantsFile", "Первый\nВторой"u8.ToArray(), "names.csv")).StatusCode);
        var free = "free" + Guid.NewGuid().ToString("N");
        int competitorId;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.True(await db.Competitors.Where(x => x.ContestId == contest.Id).AllAsync(x => x.UserName == null, cancellationToken: TestContext.Current.CancellationToken));
            competitorId = await db.Competitors.Where(x => x.ContestId == contest.Id).Select(x => x.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        var response = await SiteFixture.UploadAsync(client, path + "?handler=UploadLogins", "LoginsFile",
            Encoding.UTF8.GetBytes($"{free};12345678\n{free}b;23456789"), "logins.csv");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var admin = await site.AddUserAsync(RoleNames.Admin);
        using var adminClient = await site.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync($"/Users/Manage/{competitorId}", TestContext.Current.CancellationToken)).StatusCode);
    }

    private sealed class FailingUserManager(IUserStore<User> store, IOptions<IdentityOptions> options,
        IPasswordHasher<User> hasher, IEnumerable<IUserValidator<User>> userValidators,
        IEnumerable<IPasswordValidator<User>> passwordValidators, ILookupNormalizer normalizer,
        IdentityErrorDescriber errors, IServiceProvider services, ILogger<UserManager<User>> logger)
        : UserManager<User>(store, options, hasher, userValidators, passwordValidators, normalizer, errors, services, logger)
    {
        public override Task<IdentityResult> AddClaimAsync(User user, Claim claim) => claim.Type == ClaimNames.ContestManage
            ? Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Injected claim failure" }))
            : base.AddClaimAsync(user, claim);

        public override Task<IdentityResult> CreateAsync(User user) => user.Name == "Сбой"
            ? Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Injected participant failure" }))
            : base.CreateAsync(user);
    }
}
