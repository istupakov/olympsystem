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
public class ProblemDeletionTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ClaimsPrincipal Actor(OlympUser user) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));

    [Fact]
    public async Task DeletesPersonalSolutionsReferencesValidatorAndHistoryOnlyForSelectedProblem()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        db.Attach(owner);
        var problem = TestEntities.CreateProblem(contest);
        var retained = TestEntities.CreateProblem(contest, 2, problem.Checker);
        var compiler = TestEntities.CreateCompilator();
        foreach (string? filename in new[] { null, "author.cpp", "validator.cpp" })
        {
            var submission = TestEntities.CreateSubmission(owner, problem, compiler, DateTimeOffset.UtcNow);
            submission.Description = filename;
            submission.IsReference = filename is not null;
            submission.StatusCode = 2;
            submission.CheckResults.Add(new() { Submission = submission, CheckTime = DateTimeOffset.UtcNow, StatusCode = 2, Log = "History" });
            db.Submissions.Add(submission);
        }
        db.Tests.AddRange(new Test { Problem = problem, Number = 1, Input = [1], Output = [2] },
            new Test { Problem = retained, Number = 1, Input = [3], Output = [4] });
        db.Messages.AddRange(new Message { Contest = contest, Problem = problem, JuryText = "Clarification" },
            new Message { Contest = contest, Problem = problem, User = owner, UserText = "Staff message" },
            new Message { Contest = contest, Problem = retained, JuryText = "Retained clarification" });
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        var preview = await service.PreviewDeletionAsync(Actor(owner), contest.Id, problem.Id, Token);
        Assert.True(preview.CanDelete);
        Assert.Equal(1, preview.Tests);
        Assert.Equal(3, preview.Submissions);
        Assert.Equal(3, preview.Checks);
        Assert.Equal(2, preview.Messages);
        await service.DeleteProblemAsync(Actor(owner), contest.Id, problem.Id, Token);
        Assert.False(await db.Problems.AnyAsync(p => p.Id == problem.Id, Token));
        Assert.False(await db.Submissions.AnyAsync(s => s.ProblemId == problem.Id, Token));
        Assert.False(await db.CheckResults.AnyAsync(Token));
        Assert.Equal(retained.Id, await db.Messages.Select(m => m.ProblemId).SingleAsync(Token));
        Assert.Equal(retained.Id, await db.Tests.Select(t => t.ProblemId).SingleAsync(Token));
        Assert.True(await db.Compilators.AnyAsync(c => c.Id == compiler.Id, Token));
        Assert.True(await db.Users.AnyAsync(u => u.Id == owner.Id, Token));
    }

    [Theory]
    [InlineData("participant")]
    [InlineData("participant-message")]
    [InlineData("checking")]
    public async Task RechecksBlockersAfterPreviewAndLeavesAllDataUntouched(string blocker)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        var problem = TestEntities.CreateProblem(contest);
        db.Tests.Add(new Test { Problem = problem, Number = 1, Input = [1], Output = [2] });
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        Assert.True((await service.PreviewDeletionAsync(Actor(owner), contest.Id, problem.Id, Token)).CanDelete);
        if (blocker == "participant-message")
            db.Messages.Add(new() { Contest = contest, Problem = problem, User = TestEntities.CreateCompetitor(contest, "Message author"), UserText = "History" });
        else
        {
            User author = owner;
            if (blocker == "participant") author = TestEntities.CreateCompetitor(contest, "Participant");
            else db.Attach(owner);
            var submission = TestEntities.CreateSubmission(author, problem, TestEntities.CreateCompilator(), DateTimeOffset.UtcNow);
            submission.IsReference = true;
            submission.IsHidden = true;
            submission.StatusCode = blocker == "checking" ? 1 : 2;
            db.Submissions.Add(submission);
        }
        await db.SaveChangesAsync(Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.DeleteProblemAsync(Actor(owner), contest.Id, problem.Id, Token));
        Assert.True(await db.Problems.AnyAsync(p => p.Id == problem.Id, Token));
        Assert.Equal(1, await db.Tests.CountAsync(t => t.ProblemId == problem.Id, Token));
        Assert.Equal(1, await db.Messages.CountAsync(m => m.ProblemId == problem.Id, Token)
            + await db.Submissions.CountAsync(s => s.ProblemId == problem.Id, Token));
        var updated = await service.PreviewDeletionAsync(Actor(owner), contest.Id, problem.Id, Token);
        Assert.Equal(blocker == "checking", updated.CanDelete);
    }

    [Fact]
    public async Task RequiresContestManagementAndRechecksRevokedPermission()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var outsider = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        var problem = TestEntities.CreateProblem(contest);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        await Assert.ThrowsAsync<OperationValidationException>(() => service.PreviewDeletionAsync(Actor(outsider), contest.Id, problem.Id, Token));
        await Assert.ThrowsAsync<OperationValidationException>(() => service.DeleteProblemAsync(Actor(outsider), contest.Id, problem.Id, Token));
        Assert.True((await service.PreviewDeletionAsync(Actor(owner), contest.Id, problem.Id, Token)).CanDelete);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.DeleteProblemAsync(Actor(owner), contest.Id, int.MaxValue, Token));
        await db.UserClaims.Where(c => c.UserId == owner.Id && c.ClaimType == ClaimNames.ContestManage).ExecuteDeleteAsync(Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.DeleteProblemAsync(Actor(owner), contest.Id, problem.Id, Token));
        Assert.True(await db.Problems.AnyAsync(p => p.Id == problem.Id, Token));
    }
}
