using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class ContestImportAccessTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ClaimsPrincipal Actor(OlympUser user, string? role = RoleNames.Jury) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) }
            .Concat(role is null ? [] : new[] { new Claim(ClaimTypes.Role, role) }), "Test"));

    [Fact]
    public async Task AssignmentIsScopedRevocableAndRequiresJuryWithoutGrantingManagement()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var jury = await site.AddUserAsync(RoleNames.Jury);
        var ordinary = await site.AddUserAsync();
        var contest = await site.AddContestAsync(owner);
        var other = await site.AddContestAsync(owner);
        var clock = new ManualTimeProvider(contest.StartTime.AddMinutes(-1));
        using var host = site.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var scope = host.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<ContestImportAccessService>();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        await Assert.ThrowsAsync<OperationValidationException>(() => access.SetAsync(Actor(jury), contest.Id, jury.Id, true, Token));
        await Assert.ThrowsAsync<OperationValidationException>(() => access.SetAsync(Actor(owner), contest.Id, ordinary.Id, true, Token));
        await access.SetAsync(Actor(owner), contest.Id, jury.Id, true, Token);
        await access.SetAsync(Actor(owner), contest.Id, jury.Id, true, Token);
        Assert.Equal(jury.Id, Assert.Single(await access.ListAsync(Actor(owner), contest.Id, Token)).UserId);
        Assert.True((await authorization.AuthorizeAsync(Actor(jury), contest.Id, PolicyNames.ImportProblems)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(Actor(jury), contest.Id, PolicyNames.ManageContest)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(Actor(jury), other.Id, PolicyNames.ImportProblems)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(Actor(jury, null), contest.Id, PolicyNames.ImportProblems)).Succeeded);
        await Assert.ThrowsAsync<OperationValidationException>(() => access.SetAsync(Actor(jury), contest.Id, ordinary.Id, true, Token));
        await access.SetAsync(Actor(owner), contest.Id, jury.Id, false, Token);
        Assert.False((await authorization.AuthorizeAsync(Actor(jury), contest.Id, PolicyNames.ImportProblems)).Succeeded);
        Assert.Empty(await access.ListAsync(Actor(owner), contest.Id, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenImportCannotContinueAfterStartOrRevocation(bool revoke)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var jury = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        var clock = new ManualTimeProvider(contest.StartTime.AddTicks(-1));
        using var host = site.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var scope = host.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<ContestImportAccessService>();
        await access.SetAsync(Actor(owner), contest.Id, jury.Id, true, Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        using var zip = new MemoryStream(ProblemArchives.ProblemZip());
        var preview = await service.PrepareAsync(Actor(jury), contest.Id, zip, Token);
        var row = Assert.Single(preview.Rows);
        await service.EnsureValidatorAsync(Actor(jury), preview, row, Token);
        if (revoke) await access.SetAsync(Actor(owner), contest.Id, jury.Id, false, Token);
        else clock.UtcNow = contest.StartTime;
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(Actor(jury), preview, row, Token));
        await Assert.ThrowsAsync<OperationValidationException>(() => service.RefreshAsync(Actor(jury), preview, row, Token));
        await Assert.ThrowsAsync<OperationValidationException>(() => service.EnsureValidatorAsync(Actor(jury), preview, row, Token));
        await Assert.ThrowsAsync<OperationValidationException>(() => service.RunSolutionsAsync(Actor(jury), preview, row, token: Token));
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        clock.UtcNow = contest.EndTime.AddDays(1);
        Assert.True((await authorization.AuthorizeAsync(Actor(owner), contest.Id, PolicyNames.ImportProblems)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(Actor(owner, RoleNames.Admin), contest.Id, PolicyNames.ImportProblems)).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Problems.AnyAsync(problem => problem.ContestId == contest.Id, Token));
    }

    [Fact]
    public async Task AssignedJuryCanSaveProblemsBeforeStart()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var jury = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var host = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<TimeProvider>(new ManualTimeProvider(contest.StartTime.AddMinutes(-1)))));
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ContestImportAccessService>()
            .SetAsync(Actor(owner), contest.Id, jury.Id, true, Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        using var zip = new MemoryStream(ProblemArchives.ProblemZip());
        var preview = await service.PrepareAsync(Actor(jury), contest.Id, zip, Token);
        var row = Assert.Single(preview.Rows);
        await service.EnsureValidatorAsync(Actor(jury), preview, row, Token);
        int id = await service.SaveAsync(Actor(jury), preview, row, Token);
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(2, await db.Tests.CountAsync(test => test.ProblemId == id, Token));
    }
}
