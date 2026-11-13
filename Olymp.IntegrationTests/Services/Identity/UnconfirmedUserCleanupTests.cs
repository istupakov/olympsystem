using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services.Identity;

using Xunit;

namespace Olymp.IntegrationTests.Services.Identity;

[Trait("Category", "Integration")]
public class UnconfirmedUserCleanupTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("eligible", true)]
    [InlineData("confirmed", false)]
    [InlineData("recent", false)]
    [InlineData("boundary", false)]
    [InlineData("unknown-date", false)]
    [InlineData("submission", false)]
    [InlineData("message", false)]
    [InlineData("membership", false)]
    [InlineData("coached-user", false)]
    [InlineData("coached-participant", false)]
    [InlineData("admin", false)]
    [InlineData("jury", false)]
    [InlineData("coach", false)]
    [InlineData("manager", false)]
    public async Task PreviewSelectsOnlyOldUnconfirmedAccountsWithoutHistoryOrPermissions(string reason, bool eligible)
    {
        int id = await AddOldAccountAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ProtectAsync(scope.ServiceProvider, id, reason);
        var preview = await Service(scope.ServiceProvider).PrepareAsync(await AdminAsync(), TestContext.Current.CancellationToken);
        Assert.Equal(Now.AddMonths(-1), preview.RegisteredBefore);
        Assert.Equal(eligible, preview.Ids.Contains(id));
        Assert.True(await db.OlympUsers.AnyAsync(item => item.Id == id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("submission")]
    [InlineData("membership")]
    [InlineData("admin")]
    [InlineData("coach")]
    [InlineData("manager")]
    public async Task DeletionRechecksEligibilityAndSkipsChangedAccounts(string reason)
    {
        int id = await AddOldAccountAsync();
        UnconfirmedUserCleanupService.Preview preview;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            preview = await Service(scope.ServiceProvider).PrepareAsync(await AdminAsync(), TestContext.Current.CancellationToken);
            Assert.Contains(id, preview.Ids);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
            await ProtectAsync(scope.ServiceProvider, id, reason);
        await using var deletion = factory.Services.CreateAsyncScope();
        var result = await Service(deletion.ServiceProvider).DeleteAsync(await AdminAsync(), preview with { Ids = [id] }, TestContext.Current.CancellationToken);
        Assert.Equal(new UnconfirmedUserCleanupService.Result(0, 1), result);
        Assert.True(await deletion.ServiceProvider.GetRequiredService<OlympContext>().OlympUsers.AnyAsync(item => item.Id == id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletionUsesIdentityAndOnlyThePreviewedAccounts()
    {
        int id = await AddOldAccountAsync();
        UnconfirmedUserCleanupService.Preview preview;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var account = (await manager.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture)))!;
            Assert.True((await manager.AddClaimAsync(account, new Claim("test-preference", "value"))).Succeeded);
            Assert.True((await manager.SetAuthenticationTokenAsync(account, "test", "token", "value")).Succeeded);
            Assert.True((await manager.AddLoginAsync(account, new UserLoginInfo("test", Guid.NewGuid().ToString("N"), "Test"))).Succeeded);
            preview = await Service(scope.ServiceProvider).PrepareAsync(await AdminAsync(), TestContext.Current.CancellationToken);
            Assert.Contains(id, preview.Ids);
        }
        int later = await AddOldAccountAsync();
        await using var deletion = factory.Services.CreateAsyncScope();
        var result = await Service(deletion.ServiceProvider).DeleteAsync(await AdminAsync(), preview with { Ids = [id] }, TestContext.Current.CancellationToken);
        Assert.Equal(new UnconfirmedUserCleanupService.Result(1, 0), result);
        var db = deletion.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Users.AnyAsync(item => item.Id == id, TestContext.Current.CancellationToken));
        Assert.False(await db.UserClaims.AnyAsync(item => item.UserId == id, TestContext.Current.CancellationToken));
        Assert.False(await db.UserTokens.AnyAsync(item => item.UserId == id, TestContext.Current.CancellationToken));
        Assert.False(await db.UserLogins.AnyAsync(item => item.UserId == id, TestContext.Current.CancellationToken));
        Assert.True(await db.OlympUsers.AnyAsync(item => item.Id == later, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("user")]
    [InlineData(RoleNames.Jury)]
    public async Task DirectCallsRequireAdministrator(string? role)
    {
        var actor = new ClaimsPrincipal(role is null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
        await using var scope = factory.Services.CreateAsyncScope();
        var service = Service(scope.ServiceProvider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareAsync(actor, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(actor, new(Now, []), TestContext.Current.CancellationToken));
    }

    private async Task<int> AddOldAccountAsync()
    {
        var user = await factory.AddUserAsync(emailConfirmed: false);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var stored = await db.OlympUsers.SingleAsync(item => item.Id == user.Id, TestContext.Current.CancellationToken);
        stored.RegistrationDate = Now.AddMonths(-2);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task ProtectAsync(IServiceProvider services, int id, string reason)
    {
        var db = services.GetRequiredService<OlympContext>();
        var user = await db.OlympUsers.SingleAsync(item => item.Id == id, TestContext.Current.CancellationToken);
        var contest = TestEntities.CreateContest("Cleanup history", Now.AddDays(-2), Now.AddDays(-1));
        switch (reason)
        {
            case "confirmed": user.EmailConfirmed = true; break;
            case "recent": user.RegistrationDate = Now.AddDays(-2); break;
            case "boundary": user.RegistrationDate = Now.AddMonths(-1); break;
            case "unknown-date": user.RegistrationDate = null; break;
            case "submission": db.Submissions.Add(TestEntities.CreateSubmission(user, TestEntities.CreateProblem(contest), TestEntities.CreateCompilator(), Now)); break;
            case "message": db.Messages.Add(new Message { Contest = contest, UserId = id, UserText = "History" }); break;
            case "membership":
                var team = TestEntities.CreateCompetitor(contest, "Team");
                team.Members.Add(user);
                db.Competitors.Add(team);
                break;
            case "coached-user":
                var coached = await factory.AddUserAsync();
                (await db.OlympUsers.SingleAsync(item => item.Id == coached.Id, TestContext.Current.CancellationToken)).CoachId = id;
                break;
            case "coached-participant":
                var participant = TestEntities.CreateCompetitor(contest, "Coached participant");
                participant.CoachId = id;
                db.Competitors.Add(participant);
                break;
            case "admin":
            case "jury":
                string role = reason == "admin" ? RoleNames.Admin : RoleNames.Jury;
                var roles = services.GetRequiredService<RoleManager<IdentityRole<int>>>();
                if (!await roles.RoleExistsAsync(role)) Assert.True((await roles.CreateAsync(new(role))).Succeeded);
                Assert.True((await services.GetRequiredService<UserManager<User>>().AddToRoleAsync(user, role)).Succeeded);
                break;
            case "coach":
            case "manager":
                db.UserClaims.Add(new IdentityUserClaim<int> { UserId = id, ClaimType = reason == "coach" ? ClaimNames.CoachOrg : ClaimNames.ContestManage, ClaimValue = "1" });
                break;
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<ClaimsPrincipal> AdminAsync()
    {
        var account = await factory.AddUserAsync();
        return new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.Id.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Role, RoleNames.Admin)], "Test"));
    }
    private static UnconfirmedUserCleanupService Service(IServiceProvider services) => new(services.GetRequiredService<OlympContext>(),
        services.GetRequiredService<UserManager<User>>(), services.GetRequiredService<IAuthorizationService>(), new CleanupClock());
    private sealed class CleanupClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
