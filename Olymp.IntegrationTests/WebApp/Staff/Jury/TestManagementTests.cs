using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class TestManagementTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(RoleNames.Admin, false, true)]
    [InlineData(RoleNames.Jury, true, true)]
    [InlineData(RoleNames.Jury, false, false)]
    public async Task TestActivityRequiresCurrentContestManagementPermission(string role, bool assigned, bool allowed)
    {
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var problem = TestEntities.CreateProblem(TestEntities.CreateContest("Test controls", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));
        var test = new Test { Problem = problem, Number = 1, IsActive = true, IsOpen = true, Input = [1, 2], Output = [3] };
        db.Tests.Add(test);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var claim = new IdentityUserClaim<int> { UserId = actor.Id, ClaimType = ClaimNames.ContestManage, ClaimValue = problem.ContestId.ToString() };
        if (assigned)
        {
            db.UserClaims.Add(claim);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var principal = Staff(actor.Id, role);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TestDetails>(
            $"/jury/problems/{problem.Id}/tests/{test.Id}", new() { ["ProblemId"] = problem.Id, ["Id"] = test.Id }, principal));
        Assert.Equal(allowed, page.QuerySelectorAll("button").Any(button => button.TextContent == SiteResources.StaffDisable));
        using var task = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Problems>(
            $"/jury/contests/{problem.ContestId}/problems?ProblemId={problem.Id}", new() { ["ContestId"] = problem.ContestId }, principal));
        Assert.Equal(allowed, task.QuerySelectorAll("button").Any(button => button.TextContent == SiteResources.StaffDisable));
        Assert.Equal(role == RoleNames.Admin, task.QuerySelectorAll("a").Any(link => link.GetAttribute("href") == $"jury/contests/{problem.ContestId}/submissions?Problem={problem.Id}&Retest=true"));
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        if (!allowed)
        {
            await Assert.ThrowsAsync<OperationValidationException>(() => service.SetTestActiveAsync(principal, problem.Id, test.Id, false, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<OperationValidationException>(() => service.SetProblemActiveAsync(principal, problem.ContestId, problem.Id, false, TestContext.Current.CancellationToken));
            return;
        }
        await service.SetTestActiveAsync(principal, problem.Id, test.Id, false, TestContext.Current.CancellationToken);
        var saved = await db.Tests.AsNoTracking().SingleAsync(item => item.Id == test.Id, TestContext.Current.CancellationToken);
        Assert.False(saved.IsActive);
        Assert.True(saved.IsOpen);
        Assert.Equal(new byte[] { 1, 2 }, saved.Input);
        await service.SetTestActiveAsync(principal, problem.Id, test.Id, true, TestContext.Current.CancellationToken);
        await service.SetProblemActiveAsync(principal, problem.ContestId, problem.Id, false, TestContext.Current.CancellationToken);
        Assert.False(await db.Problems.Where(item => item.Id == problem.Id).Select(item => item.IsActive).SingleAsync(TestContext.Current.CancellationToken));
        if (assigned)
        {
            db.UserClaims.Remove(claim);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<OperationValidationException>(() => service.SetTestActiveAsync(principal, problem.Id, test.Id, false, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<OperationValidationException>(() => service.SetProblemActiveAsync(principal, problem.ContestId, problem.Id, true, TestContext.Current.CancellationToken));
        }
        Assert.True(await db.Tests.Where(item => item.Id == test.Id).Select(item => item.IsActive).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActivityCanChangeDuringCheckingWithoutChangingRunningSubmissionOrOtherTests()
    {
        var (actor, submission) = await AddIpSubmissionAsync(null);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Submissions.Where(item => item.Id == submission.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StatusCode, 1), TestContext.Current.CancellationToken);
        var test = new Test { ProblemId = submission.ProblemId, Problem = null!, Number = 1, IsActive = true, Input = [], Output = [] };
        db.Tests.Add(test);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        var principal = Staff(actor.Id, RoleNames.Admin);
        await service.SetTestActiveAsync(principal, submission.ProblemId, test.Id, false, TestContext.Current.CancellationToken);
        await service.SetProblemActiveAsync(principal, submission.Problem.ContestId, submission.ProblemId, false, TestContext.Current.CancellationToken);
        Assert.False(await db.Tests.Where(item => item.Id == test.Id).Select(item => item.IsActive).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Submissions.Where(item => item.Id == submission.Id).Select(item => item.StatusCode).SingleAsync(TestContext.Current.CancellationToken));
        await service.SetProblemActiveAsync(principal, submission.Problem.ContestId, submission.ProblemId, true, TestContext.Current.CancellationToken);
        Assert.False(await db.Tests.Where(item => item.Id == test.Id).Select(item => item.IsActive).SingleAsync(TestContext.Current.CancellationToken));
    }
}
