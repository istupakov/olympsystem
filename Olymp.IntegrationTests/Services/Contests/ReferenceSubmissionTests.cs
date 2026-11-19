using System.Security.Claims;

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
public class ReferenceSubmissionTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ClaimsPrincipal Actor(OlympUser user, string role = RoleNames.Jury) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, role)], "Test"));

    private async Task<(OlympUser Owner, Submission Submission)> SetupAsync()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var submission = TestEntities.CreateSubmission(owner, TestEntities.CreateProblem(contest),
            TestEntities.CreateCompilator(), DateTimeOffset.UtcNow, "int main() {}"u8.ToArray());
        db.Attach(owner);
        db.Attach(contest);
        submission.StatusCode = -1001;
        submission.Description = "Original description";
        submission.CheckResults.Add(new CheckResult { Submission = submission, CheckTime = DateTimeOffset.UtcNow, StatusCode = -1001, Log = "Original check" });
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(Token);
        return (owner, submission);
    }

    [Fact]
    public async Task AssignAndRemovePreserveSubmissionHistorySourceVisibilityAndVerdict()
    {
        var (owner, original) = await SetupAsync();
        string filename = ReferenceSubmissionService.DefaultFilename(original);
        Assert.Equal(original.Description, filename);
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ReferenceSubmissionService>();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await service.SetAsync(Actor(owner), original.Id, true, filename, Token);
        var saved = await db.Submissions.AsNoTracking().Include(s => s.CheckResults).SingleAsync(s => s.Id == original.Id, Token);
        Assert.True(saved.IsReference);
        Assert.Equal(filename, saved.Description);
        Assert.Equal(original.StatusCode, saved.StatusCode);
        Assert.Equal(original.SourceCode, saved.SourceCode);
        Assert.Equal(original.UserId, saved.UserId);
        Assert.Equal(original.CommitTime, saved.CommitTime);
        Assert.Equal(original.LastModification, saved.LastModification);
        Assert.Equal(original.IsHidden, saved.IsHidden);
        Assert.Equal("Original check", Assert.Single(saved.CheckResults).Log);
        Assert.Equal(1, await db.Submissions.CountAsync(s => s.ProblemId == original.ProblemId, Token));
        var export = await scope.ServiceProvider.GetRequiredService<ContestExportService>().ExportProblemsAsync(original.Problem.ContestId, Token);
        await using (var zip = export.Stream)
            Assert.Equal(filename, Assert.Single(Assert.Single((await ProblemArchive.ReadAsync(zip, new(), Token)).Problems).Solutions).Filename);
        await service.SetAsync(Actor(owner), original.Id, false, null, Token);
        saved = await db.Submissions.AsNoTracking().Include(s => s.User).Include(s => s.Compilator).SingleAsync(s => s.Id == original.Id, Token);
        Assert.False(saved.IsReference);
        Assert.Equal(filename, saved.Description);
        Assert.Equal(filename, ReferenceSubmissionService.DefaultFilename(saved));
        Assert.Single(await db.CheckResults.Where(check => check.SubmissionId == original.Id).ToListAsync(Token));
    }

    [Theory]
    [InlineData("validator.cpp")]
    [InlineData("VALIDATOR.CPP")]
    [InlineData("../file.cpp")]
    [InlineData("folder/file.cpp")]
    [InlineData("CON.cpp")]
    [InlineData("")]
    public async Task ReservedAndInvalidFilenamesAreRejected(string filename)
    {
        var (owner, submission) = await SetupAsync();
        using var scope = site.Services.CreateScope();
        await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider.GetRequiredService<ReferenceSubmissionService>()
            .SetAsync(Actor(owner), submission.Id, true, filename, Token));
    }

    [Fact]
    public async Task DuplicateNamesAreCaseInsensitiveAndAnotherNameCanBeUsed()
    {
        var (owner, original) = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var second = TestEntities.CreateSubmission(owner, original.Problem, original.Compilator, DateTimeOffset.UtcNow);
        second.StatusCode = 2;
        second.User = null!;
        second.Problem = null!;
        second.Compilator = null!;
        db.Submissions.Add(second);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<ReferenceSubmissionService>();
        await service.SetAsync(Actor(owner), original.Id, true, "main.cpp", Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SetAsync(Actor(owner), second.Id, true, "MAIN.CPP", Token));
        await service.SetAsync(Actor(owner), second.Id, true, "other.cpp", Token);
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("email")]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("validator")]
    public async Task InvalidAuthorAndSubmissionStateAreRechecked(string scenario)
    {
        var (owner, submission) = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        if (scenario == "visible") await db.Users.Where(user => user.Id == owner.Id).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsHidden, false), Token);
        if (scenario == "email") await db.Users.Where(user => user.Id == owner.Id).ExecuteUpdateAsync(set => set.SetProperty(user => user.Email, (string?)null), Token);
        if (scenario is "queued" or "running")
            await db.Submissions.Where(item => item.Id == submission.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.StatusCode, scenario == "queued" ? 0 : 1), Token);
        if (scenario == "validator") await db.Submissions.Where(item => item.Id == submission.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Description, "validator.cpp"), Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider.GetRequiredService<ReferenceSubmissionService>()
            .SetAsync(Actor(owner), submission.Id, true, "main.cpp", Token));
    }

    [Fact]
    public async Task ImportOnlyJuryCanChangeReferencesBeforeStartAndCannotAfterStartOrRevocation()
    {
        var (owner, submission) = await SetupAsync();
        var jury = await site.AddUserAsync(RoleNames.Jury);
        var clock = new ManualTimeProvider(submission.Problem.Contest.StartTime.AddTicks(-1));
        using var host = site.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var scope = host.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<ContestImportAccessService>();
        var service = scope.ServiceProvider.GetRequiredService<ReferenceSubmissionService>();
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SetAsync(Actor(jury), submission.Id, true, "main.cpp", Token));
        await access.SetAsync(Actor(owner), submission.Problem.ContestId, jury.Id, true, Token);
        await service.SetAsync(Actor(jury), submission.Id, true, "main.cpp", Token);
        clock.UtcNow = submission.Problem.Contest.StartTime;
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SetAsync(Actor(jury), submission.Id, false, null, Token));
        clock.UtcNow = submission.Problem.Contest.StartTime.AddTicks(-1);
        await access.SetAsync(Actor(owner), submission.Problem.ContestId, jury.Id, false, Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SetAsync(Actor(jury), submission.Id, false, null, Token));
        clock.UtcNow = submission.Problem.Contest.EndTime.AddDays(1);
        await service.SetAsync(Actor(owner), submission.Id, false, null, Token);
    }
}
