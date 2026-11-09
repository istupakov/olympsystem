using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.WebApp.Components.Staff;

using Xunit;

using ContestInput = Olymp.WebApp.Components.Pages.Staff.Jury.EditContest.InputModel;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class StaffEditingTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData(null)]
    [InlineData("user")]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task StaffRoutesRequireTheirRole(string? role)
    {
        var user = role is null ? null : await factory.AddUserAsync();
        if (role is RoleNames.Jury or RoleNames.Admin)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
            Assert.True((await roles.CreateAsync(new(role))).Succeeded);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(user!.Id.ToString()))!, role)).Succeeded);
        }
        using var client = user is null ? factory.CreateSiteClient() : await factory.LoginAsync(user.UserName!);
        foreach (string path in new[] { "/jury/contests", "/jury/contests/new", "/jury/contests/1/edit",
                     "/jury/contests/1/participants", "/jury/contests/1/submissions", "/jury/submissions/1",
                     "/jury/submissions", "/jury/runners", "/jury/contests/1/messages", "/jury/contests/1/standing",
                     "/jury/contests/1/anticheat", "/jury/anticheat", "/admin/users", "/admin/users/1/edit",
                     "/admin/news", "/admin/news/new", "/admin/news/1/edit",
                     "/admin/organizations", "/admin/organizations/new", "/admin/organizations/1/edit" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            bool allowed = role == RoleNames.Admin || (role == RoleNames.Jury && path.StartsWith("/jury/", StringComparison.Ordinal));
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, response.StatusCode);
            if (!allowed)
            {
                Assert.Contains(user is null ? "/Account/Login" : "/Account/AccessDenied", response.Headers.Location!.OriginalString);
                using var deniedPage = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, deniedPage.StatusCode);
            }
        }
    }

    [Fact]
    public async Task ContestManagementUsesStoredAssignmentEvenWhenPrincipalHasNoClaim()
    {
        var user = await factory.AddUserAsync();
        var other = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = NewContest();
        context.Contests.Add(contest);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var principal = Principal(user.Id, RoleNames.Jury);
        Assert.False(await StaffContestAccess.CanManageAsync(principal, contest.Id, context));
        var claim = new IdentityUserClaim<int>
        {
            UserId = user.Id,
            ClaimType = ClaimNames.ContestManage,
            ClaimValue = contest.Id.ToString(CultureInfo.InvariantCulture)
        };
        context.UserClaims.Add(claim);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(await StaffContestAccess.CanManageAsync(principal, contest.Id, context));
        Assert.False(await StaffContestAccess.CanManageAsync(Principal(other.Id, RoleNames.Jury), contest.Id, context));
        Assert.False(await StaffContestAccess.CanManageAsync(Principal(user.Id, null), contest.Id, context));
        Assert.True(await StaffContestAccess.CanManageAsync(Principal(other.Id, RoleNames.Admin), contest.Id, context));
        context.UserClaims.Remove(claim);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.False(await StaffContestAccess.CanManageAsync(principal, contest.Id, context));
    }

    [Fact]
    public void MovingStartPreservesDurationAndRelativeIntervals()
    {
        var input = ValidInput();
        input.EarlyPrinting = input.Freeze = true;
        var contest = NewContest();
        input.ApplyTo(contest);
        var (start, end, printing, freeze) = (contest.StartTime, contest.EndTime, contest.ProblemShowTime, contest.FreezeTime);
        input = ContestInput.From(contest);
        input.StartTime = input.StartTime!.Value.AddHours(2);
        Assert.Empty(Validate(input));
        input.ApplyTo(contest);
        Assert.Equal(start.AddHours(2), contest.StartTime);
        Assert.Equal(end.AddHours(2), contest.EndTime);
        Assert.Equal(printing!.Value.AddHours(2), contest.ProblemShowTime);
        Assert.Equal(freeze!.Value.AddHours(2), contest.FreezeTime);
    }

    [Fact]
    public void LongerDurationMovesEndAndFreezeAndDisablingOptionsClearsDates()
    {
        var input = ValidInput();
        input.EarlyPrinting = input.Freeze = true;
        input.Duration = "29:15";
        var contest = NewContest();
        Assert.Empty(Validate(input));
        input.ApplyTo(contest);
        Assert.Equal(TimeSpan.FromHours(29.25), contest.EndTime - contest.StartTime);
        Assert.Equal(TimeSpan.FromMinutes(30), contest.StartTime - contest.ProblemShowTime);
        Assert.Equal(TimeSpan.FromHours(1), contest.EndTime - contest.FreezeTime);
        input.EarlyPrinting = input.Freeze = false;
        input.ApplyTo(contest);
        Assert.Null(contest.ProblemShowTime);
        Assert.Null(contest.FreezeTime);
    }

    [Theory]
    [InlineData("0:00", "0:30", "1:00", false)]
    [InlineData("5:60", "0:30", "1:00", false)]
    [InlineData("5:00", "-0:30", "1:00", false)]
    [InlineData("5:00", "0:30", "6:00", false)]
    [InlineData("5:00", "0:30", "1:00", true)]
    [InlineData("999999999999999999:00", "0:30", "1:00", false)]
    public void InvalidScheduleIsRejected(string duration, string printing, string freeze, bool open)
    {
        var input = ValidInput();
        input.EarlyPrinting = input.Freeze = true;
        input.Duration = duration;
        input.PrintingAdvance = printing;
        input.FreezeAdvance = freeze;
        input.IsOpen = open;
        Assert.NotEmpty(Validate(input));
    }

    [Theory]
    [InlineData("0:00", "0:30", "1:00", nameof(ContestInput.Duration))]
    [InlineData("5:60", "0:30", "1:00", nameof(ContestInput.Duration))]
    [InlineData("5:00", "invalid", "1:00", nameof(ContestInput.PrintingAdvance))]
    [InlineData("5:00", "0:30", "6:00", nameof(ContestInput.FreezeAdvance))]
    public void InvalidIntervalReportsOnlyTheAffectedField(string duration, string printing, string freeze, string field)
    {
        var input = ValidInput();
        input.EarlyPrinting = input.Freeze = true;
        input.Duration = duration;
        input.PrintingAdvance = printing;
        input.FreezeAdvance = freeze;
        var error = Assert.Single(Validate(input));
        Assert.Equal([field], error.MemberNames);
    }

    private static List<ValidationResult> Validate(object input)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(input, new(input), results, validateAllProperties: true);
        return results;
    }

    private static ContestInput ValidInput() => new()
    {
        Name = "Test contest",
        Abbr = "test_contest",
        StartTime = new DateTime(2030, 1, 2, 10, 0, 0)
    };

    private static Contest NewContest() => new()
    {
        Name = "Staff test",
        Abbr = $"staff_{Guid.NewGuid():N}",
        Problems = [],
        Competitors = [],
        Messages = []
    };

    private static ClaimsPrincipal Principal(int id, string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)) };
        if (role is not null)
            claims.Add(new(ClaimTypes.Role, role));
        return new(new ClaimsIdentity(claims, "Test"));
    }
}
