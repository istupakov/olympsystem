using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class LegacyProblemCleanupTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    [Fact]
    public async Task CleanupRequiresAdminRejectsAnyHistoryAndDeletesOnlyTheTwoProblemsAndTheirTests()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = await site.AddUserAsync(RoleNames.Admin);
        var jury = await site.AddUserAsync(RoleNames.Jury);
        var ordinary = await site.AddUserAsync();
        var contest = await site.AddContestAsync(jury);
        var otherContest = await site.AddContestAsync();
        Assert.Equal(ContestProblemService.LegacyCleanupContestId, contest.Id);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        db.Attach(otherContest);
        var first = TestEntities.CreateProblem(contest, 6);
        first.Id = 3554;
        var second = TestEntities.CreateProblem(contest, 9, first.Checker);
        second.Id = 3582;
        var original = TestEntities.CreateProblem(contest, 6, first.Checker);
        original.Id = 9;
        var neighbour = TestEntities.CreateProblem(otherContest, 6, first.Checker);
        neighbour.Id = 3561;
        db.Tests.AddRange(
            new Test { Problem = first, Number = 1, Input = [1], Output = [2] },
            new Test { Problem = first, Number = 2, Input = [3], Output = [4] },
            new Test { Problem = second, Number = 1, Input = [5], Output = [6] },
            new Test { Problem = original, Number = 1, Input = [7], Output = [8] },
            new Test { Problem = neighbour, Number = 1, Input = [9], Output = [10] });
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT Problems ON", token);
            await db.SaveChangesAsync(token);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT Problems OFF", token);
            await db.Database.CloseConnectionAsync();
        }
        db.ChangeTracker.Clear();
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        await Assert.ThrowsAsync<OperationValidationException>(() => service.CleanupLegacyProblemsAsync(Actor(jury, RoleNames.Jury), token));
        await Assert.ThrowsAsync<OperationValidationException>(() => service.CleanupLegacyProblemsAsync(Actor(ordinary, null), token));
        await Assert.ThrowsAsync<OperationValidationException>(() => service.CleanupLegacyProblemsAsync(new ClaimsPrincipal(), token));
        Assert.Equal(4, await db.Problems.CountAsync(token));
        Assert.Equal(5, await db.Tests.CountAsync(token));

        var submission = TestEntities.CreateSubmission(ordinary, first, TestEntities.CreateCompilator(), contest.StartTime);
        submission.User = null!;
        submission.Problem = null!;
        submission.IsHidden = true;
        submission.IsReference = true;
        var message = new Message { ContestId = contest.Id, Contest = null!, ProblemId = second.Id, JuryText = "Retain clarification" };
        db.Submissions.Add(submission);
        db.Messages.Add(message);
        await db.SaveChangesAsync(token);
        var error = await Assert.ThrowsAsync<OperationValidationException>(() => service.CleanupLegacyProblemsAsync(Actor(admin, RoleNames.Admin), token));
        Assert.Contains("3554", error.Message);
        Assert.Contains("3582", error.Message);
        Assert.Equal(4, await db.Problems.CountAsync(token));
        Assert.Equal(5, await db.Tests.CountAsync(token));
        Assert.True(await db.Submissions.AnyAsync(item => item.Id == submission.Id, token));
        Assert.True(await db.Messages.AnyAsync(item => item.Id == message.Id, token));

        db.Submissions.Remove(submission);
        await db.SaveChangesAsync(token);
        error = await Assert.ThrowsAsync<OperationValidationException>(() => service.CleanupLegacyProblemsAsync(Actor(admin, RoleNames.Admin), token));
        Assert.Contains("3582", error.Message);
        Assert.Equal(4, await db.Problems.CountAsync(token));
        Assert.Equal(5, await db.Tests.CountAsync(token));
        db.Messages.Remove(message);
        await db.SaveChangesAsync(token);

        await db.Problems.Where(item => item.Id == second.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ContestId, otherContest.Id), token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.CleanupLegacyProblemsAsync(Actor(admin, RoleNames.Admin), token));
        Assert.Equal(4, await db.Problems.CountAsync(token));
        await db.Problems.Where(item => item.Id == second.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ContestId, contest.Id), token);

        await service.CleanupLegacyProblemsAsync(Actor(admin, RoleNames.Admin), token);
        Assert.Equal(new[] { original.Id, neighbour.Id }, await db.Problems.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(token));
        Assert.Equal(2, await db.Tests.CountAsync(token));
        Assert.True(await db.Tests.AnyAsync(item => item.ProblemId == original.Id && item.Input == new byte[] { 7 }, token));
        Assert.True(await db.Tests.AnyAsync(item => item.ProblemId == neighbour.Id && item.Input == new byte[] { 9 }, token));
        await service.CleanupLegacyProblemsAsync(Actor(admin, RoleNames.Admin), token);
        Assert.Equal(2, await db.Tests.CountAsync(token));
    }

    private static ClaimsPrincipal Actor(OlympUser user, string? role)
    {
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test");
        if (role is not null) identity.AddClaim(new(ClaimTypes.Role, role));
        return new ClaimsPrincipal(identity);
    }
}
