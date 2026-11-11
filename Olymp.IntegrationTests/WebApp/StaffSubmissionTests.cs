using System.Net;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Services.AntiCheat;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class StaffSubmissionTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData(RoleNames.Jury, -24)]
    [InlineData(RoleNames.Jury, 24)]
    [InlineData(RoleNames.Admin, -24)]
    public async Task AnyStaffCanSubmitToHiddenInactiveProblemWithoutAssignmentOrContestTimeRestrictions(string role, int hours)
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Staff submit", DateTimeOffset.Now.AddHours(hours), DateTimeOffset.Now.AddHours(hours + 5));
        contest.IsHidden = true;
        var problem = TestEntities.CreateProblem(contest);
        problem.IsActive = false;
        var compiler = TestEntities.CreateCompilator();
        db.Problems.Add(problem);
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var actor = Principal(author.Id, role);
        var service = scope.ServiceProvider.GetRequiredService<SubmissionService>();
        var input = new SubmissionInput { CompilatorId = compiler.Id, Code = "// Привет\nclass Program { static void Main() {} }" };
        var result = await service.SubmitJuryAsync(actor, contest.Id, problem.Id, input, TestContext.Current.CancellationToken);
        Assert.NotNull(result.Id);
        Assert.Null(result.Error);
        var submission = await db.Submissions.SingleAsync(item => item.Id == result.Id, TestContext.Current.CancellationToken);
        Assert.Equal(author.Id, submission.UserId);
        Assert.Equal(problem.Id, submission.ProblemId);
        Assert.Equal(compiler.Id, submission.CompilatorId);
        Assert.Equal(input.Code, submission.Text);
        Assert.Equal(0, submission.StatusCode);
        Assert.Equal(submission.CommitTime, submission.LastModification);
        Assert.False(submission.IsReference);
        Assert.False(submission.IsHidden);
        Assert.Null(submission.CommiterInfo);
        Assert.Empty(await db.UserClaims.Where(item => item.UserId == author.Id).ToListAsync(TestContext.Current.CancellationToken));
        var publicResult = await service.SubmitPublicAsync(actor, contest.Id, problem.Id, input, null, TestContext.Current.CancellationToken);
        Assert.False(publicResult.NotFound);
        Assert.Null(publicResult.Id);
        Assert.NotNull(publicResult.Error);
        var publicPermission = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(actor, problem, new SubmitSolutionRequirement());
        Assert.False(publicPermission.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySubmissionUsesTheAuthorizationHandler(bool jury)
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Authorization check", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        contest.IsOpen = true;
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        db.Problems.Add(problem);
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var gate = new RejectSubmissionHandler();
        await using var guardedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IAuthorizationHandler>(gate)));
        await using var guardedScope = guardedFactory.Services.CreateAsyncScope();
        var service = guardedScope.ServiceProvider.GetRequiredService<SubmissionService>();
        var actor = Principal(author.Id, RoleNames.Jury);
        var input = new SubmissionInput { CompilatorId = compiler.Id, Code = "// Valid source code" };
        var result = jury
            ? await service.SubmitJuryAsync(actor, contest.Id, problem.Id, input, TestContext.Current.CancellationToken)
            : await service.SubmitPublicAsync(actor, contest.Id, problem.Id, input, null, TestContext.Current.CancellationToken);

        Assert.Equal(jury, gate.ForJury);
        Assert.False(result.NotFound);
        Assert.Null(result.Id);
        Assert.NotNull(result.Error);
        Assert.Empty(await db.Submissions.Where(item => item.ProblemId == problem.Id).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("hidden")]
    [InlineData("guest")]
    [InlineData("compiler")]
    [InlineData("encoding")]
    [InlineData("length")]
    [InlineData("foreign-contest")]
    public async Task InvalidStaffSubmissionDoesNotWriteData(string reason)
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Staff validation", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        compiler.IsActive = reason != "compiler";
        db.Problems.Add(problem);
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (reason == "hidden")
            await db.Users.Where(item => item.Id == author.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsHidden, true), TestContext.Current.CancellationToken);
        var actor = reason == "guest" ? new ClaimsPrincipal(new ClaimsIdentity()) : Principal(author.Id, reason is "ordinary" or "hidden" ? null : RoleNames.Jury);
        var input = new SubmissionInput
        {
            CompilatorId = compiler.Id,
            Code = reason == "length" ? "short" : reason == "encoding" ? "// Unsupported 🙂" : "// Valid source code"
        };
        var result = await scope.ServiceProvider.GetRequiredService<SubmissionService>().SubmitJuryAsync(actor,
            reason == "foreign-contest" ? int.MaxValue : contest.Id, problem.Id, input, TestContext.Current.CancellationToken);
        Assert.Null(result.Id);
        Assert.True(result.NotFound || result.Error is not null);
        Assert.Empty(await db.Submissions.Where(item => item.ProblemId == problem.Id).ToListAsync(TestContext.Current.CancellationToken));
    }

    private static ClaimsPrincipal Principal(int id, string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()) };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        return new(new ClaimsIdentity(claims, "Test"));
    }

    private sealed class RejectSubmissionHandler : AuthorizationHandler<SubmitSolutionRequirement, Problem>
    {
        public bool? ForJury { get; private set; }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
            SubmitSolutionRequirement requirement, Problem resource)
        {
            ForJury = requirement.ForJury;
            context.Fail();
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Jury)]
    public async Task StaffDownloadCanReadHiddenInactiveSourcesWhilePublicDownloadCannot(string role)
    {
        var author = await factory.AddUserAsync();
        var staff = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        if (!await roles.RoleExistsAsync(role)) Assert.True((await roles.CreateAsync(new(role))).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(staff.Id.ToString()))!, role)).Succeeded);
        var contest = TestEntities.CreateContest("Hidden", DateTimeOffset.Now.AddDays(1), DateTimeOffset.Now.AddDays(2));
        contest.IsHidden = true;
        var problem = TestEntities.CreateProblem(contest);
        problem.IsActive = false;
        var submission = TestEntities.CreateSubmission(author, problem, TestEntities.CreateCompilator(), DateTimeOffset.Now);
        submission.User = null!;
        submission.IsHidden = true;
        submission.Text = "// Привет <script>\nclass Program {}";
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(staff.UserName!);
        using var download = await client.GetAsync($"/jury/submissions/{submission.Id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(submission.Text, await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text/plain", download.Content.Headers.ContentType!.MediaType);
        using var publicDownload = await client.GetAsync($"/submissions/{submission.Id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, publicDownload.StatusCode);
        using var missing = await client.GetAsync("/jury/submissions/2147483647/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var ordinary = await factory.LoginAsync(author.UserName!);
        using var denied = await ordinary.GetAsync($"/jury/submissions/{submission.Id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("AccessDenied", denied.Headers.Location!.OriginalString);
        using var guest = factory.CreateSiteClient();
        using var challenge = await guest.GetAsync($"/jury/submissions/{submission.Id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Contains("Login", challenge.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task AnalysisUsesAllVisibleAuthorsAndIncludesHiddenSubmissionsWithoutChangingLegacyScope()
    {
        var first = await factory.AddUserAsync();
        var second = await factory.AddUserAsync();
        var hidden = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Users.Where(item => item.Id == hidden.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsHidden, true), TestContext.Current.CancellationToken);
        var contest = TestEntities.CreateContest("Analysis", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        var other = TestEntities.CreateContest("Other", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        var problem = TestEntities.CreateProblem(contest);
        var unrelated = TestEntities.CreateProblem(other);
        var compiler = TestEntities.CreateCompilator();
        var submissions = new[] { first, second, hidden }.Select(user =>
        {
            var submission = TestEntities.CreateSubmission(user, problem, compiler, DateTimeOffset.Now, Encoding.ASCII.GetBytes(user.Id.ToString()));
            submission.User = null!;
            submission.IsHidden = user.Id == second.Id;
            return submission;
        }).ToArray();
        var foreign = TestEntities.CreateSubmission(first, unrelated, compiler, DateTimeOffset.Now);
        foreign.User = null!;
        db.Submissions.AddRange(submissions.Append(foreign));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var analyzer = new Analyzer();
        var service = new AntiCheatReportService(db, analyzer, analyzer);
        var query = db.Submissions.Where(item => item.Problem.ContestId == contest.Id);
        var reports = await service.FindPlagiarismAsync(query, 0.5f, TestContext.Current.CancellationToken);
        var pair = Assert.Single(Assert.Single(reports).Cases);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), new[] { pair.Submission1.UserId, pair.Submission2.UserId }.Order());
        Assert.Single(analyzer.Comparisons);
        var ai = await service.DetectAiAsync(query, 0.5f, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), ai.Select(item => item.Competitor.Id).Order());
        Assert.Equal(2, analyzer.Detections);
        Assert.Empty(await service.FindContestPlagiarismAsync(contest.Id, 0.5f, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SelectedPeriodComparesAgainstVisibleHistoryAndHonorsCancellation()
    {
        var first = await factory.AddUserAsync();
        var second = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("History", DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddDays(-1));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        var old = TestEntities.CreateSubmission(first, problem, compiler, DateTimeOffset.Now.AddDays(-1));
        var current = TestEntities.CreateSubmission(second, problem, compiler, DateTimeOffset.Now);
        old.User = current.User = null!;
        db.Submissions.AddRange(old, current);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var analyzer = new Analyzer();
        var service = new AntiCheatReportService(db, analyzer, analyzer);
        var reports = await service.FindPlagiarismAsync(db.Submissions.Where(item => item.Id == current.Id), 0.5f, TestContext.Current.CancellationToken);
        Assert.Single(Assert.Single(reports).Cases);
        Assert.Single(analyzer.Comparisons);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FindPlagiarismAsync(db.Submissions, 0.5f, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisCanBeCancelledDuringComputation(bool ai)
    {
        var first = await factory.AddUserAsync();
        var second = await factory.AddUserAsync();
        var third = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Cancellation", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        foreach (var author in new[] { first, second, third })
        {
            var submission = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now);
            submission.User = null!;
            db.Submissions.Add(submission);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var analyzer = new Analyzer { OnAnalysis = cancellation.Cancel };
        var service = new AntiCheatReportService(db, analyzer, analyzer);
        var selected = db.Submissions.Where(item => item.Problem.ContestId == contest.Id);
        if (ai)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DetectAiAsync(selected, 0.5f, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FindPlagiarismAsync(selected, 0.5f, cancellation.Token));
        Assert.Equal(1, analyzer.Comparisons.Count + analyzer.Detections);
    }

    private sealed class Analyzer : ISubmissionSimilarityService, IAIDetectionService
    {
        public List<(string, string)> Comparisons { get; } = [];
        public int Detections { get; private set; }
        public Action? OnAnalysis { get; init; }
        public float CompareSimilarity(string source1, string source2) { Comparisons.Add((source1, source2)); OnAnalysis?.Invoke(); return 0.9f; }
        public AIDetectionResult DetectAI(string source, string language) { Detections++; OnAnalysis?.Invoke(); return new(0.9, 1, 0, []); }
    }
}
