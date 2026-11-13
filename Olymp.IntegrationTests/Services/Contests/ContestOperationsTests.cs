using System.IO.Compression;
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
public class ContestOperationsTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    [Theory]
    [InlineData(RoleNames.Admin, false, true)]
    [InlineData(RoleNames.Jury, true, true)]
    [InlineData(RoleNames.Jury, false, false)]
    public async Task PublicationRequiresEndAndManagementAndContainsOnlyActiveTests(string role, bool assigned, bool allowed)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await site.AddUserAsync(role);
        var contest = await site.AddContestAsync(assigned ? user : null);
        var actor = Actor(user, role);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var active = TestEntities.CreateProblem(contest);
        active.Contest = null!;
        var inactive = TestEntities.CreateProblem(contest, 2);
        inactive.Contest = null!;
        inactive.IsActive = false;
        db.Tests.AddRange(
            new Test { Problem = active, Number = 1, IsActive = true, Input = [1, 2], Output = [3] },
            new Test { Problem = active, Number = 2, IsActive = false, Input = [4], Output = [5] },
            new Test { Problem = inactive, Number = 1, IsActive = true, Input = [6], Output = [7] });
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        await Assert.ThrowsAsync<OperationValidationException>(() => service.PublishTestZipAsync(actor, contest.Id, token));
        await db.Contests.Where(item => item.Id == contest.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.EndTime, DateTimeOffset.UtcNow.AddMinutes(-1)), token);
        db.ChangeTracker.Clear();
        if (!allowed)
        {
            await Assert.ThrowsAsync<OperationValidationException>(() => service.PublishTestZipAsync(actor, contest.Id, token));
            await Assert.ThrowsAsync<OperationValidationException>(() => service.RemoveTestZipAsync(actor, contest.Id, token));
            return;
        }
        await service.PublishTestZipAsync(actor, contest.Id, token);
        byte[] bytes = await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.TestZip!.Data).SingleAsync(token);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(2, zip.Entries.Count);
        Assert.All(zip.Entries, entry => Assert.Contains("/1. ", entry.FullName));
        Assert.All(zip.Entries, entry => Assert.EndsWith("01.txt", entry.Name));
        await service.PublishTestZipAsync(actor, contest.Id, token);
        int blobId = await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.TestZipId!.Value).SingleAsync(token);
        await service.RemoveTestZipAsync(actor, contest.Id, token);
        Assert.Null(await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.TestZipId).SingleAsync(token));
        Assert.False(await db.Blobs.AnyAsync(item => item.Id == blobId, token));
        Assert.Equal(3, await db.Tests.CountAsync(item => item.Problem.ContestId == contest.Id, token));
    }

    [Theory]
    [InlineData(RoleNames.Admin, false, true)]
    [InlineData(RoleNames.Jury, true, true)]
    [InlineData(RoleNames.Jury, false, false)]
    public async Task FinishRequiresEndAndManagementAndRetainsParticipantHistory(string role, bool assigned, bool allowed)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await site.AddUserAsync(role);
        var contest = await site.AddContestAsync(assigned ? user : null);
        var actor = Actor(user, role);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var participant = TestEntities.CreateCompetitor(contest, "Retained participant");
        participant.Contest = null!;
        db.Competitors.Add(participant);
        await db.SaveChangesAsync(token);
        var submission = TestEntities.CreateSubmission(participant, TestEntities.CreateProblem(contest), TestEntities.CreateCompilator(), DateTimeOffset.UtcNow);
        submission.Problem.Contest = null!;
        submission.User = null!;
        var message = new Message { Contest = null!, ContestId = contest.Id, UserId = participant.Id, UserText = "Keep question" };
        db.Submissions.Add(submission);
        db.Messages.Add(message);
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        var admin = await site.AddUserAsync(RoleNames.Admin);
        await service.AssignLoginsAsync(Actor(admin, RoleNames.Admin), contest.Id,
            new Dictionary<string, string> { ["finish" + Guid.NewGuid().ToString("N")] = "FinishTest123!" }, token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.FinishContestAsync(actor, contest.Id, token));
        Assert.False(await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.IsOpen).SingleAsync(token));
        Assert.NotNull(await db.Competitors.Where(item => item.Id == participant.Id).Select(item => item.UserName).SingleAsync(token));
        await db.Contests.Where(item => item.Id == contest.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.EndTime, DateTimeOffset.UtcNow.AddMinutes(-1)), token);
        db.ChangeTracker.Clear();
        if (!allowed)
        {
            await Assert.ThrowsAsync<OperationValidationException>(() => service.FinishContestAsync(actor, contest.Id, token));
            return;
        }
        await service.FinishContestAsync(actor, contest.Id, token);
        db.ChangeTracker.Clear();
        var saved = await db.Contests.SingleAsync(item => item.Id == contest.Id, token);
        Assert.True(saved.IsOpen);
        Assert.Null(saved.FreezeTime);
        var account = await db.Competitors.SingleAsync(item => item.Id == participant.Id, token);
        Assert.Null(account.UserName);
        Assert.Null(account.PasswordHash);
        Assert.False(await db.UserClaims.AnyAsync(item => item.UserId == participant.Id && item.ClaimType == ClaimNames.CompetitorContest, token));
        Assert.True(await db.Submissions.AnyAsync(item => item.Id == submission.Id, token));
        Assert.True(await db.Messages.AnyAsync(item => item.Id == message.Id, token));
        await service.FinishContestAsync(actor, contest.Id, token);
    }

    private static ClaimsPrincipal Actor(OlympUser user, string role) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, role)], "Test"));
}
