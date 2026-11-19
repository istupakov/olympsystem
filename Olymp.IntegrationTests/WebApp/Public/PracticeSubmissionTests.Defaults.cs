using System.Net;

using AngleSharp.Dom;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

public partial class PracticeSubmissionTests
{
    private static string RefreshUrl(IDocument page) => page.QuerySelector("#submissions a.ms-auto")!.GetAttribute("href")!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContestAndProblemDefaultsPreferContestModeThenOwnCoachedAndAll(bool open)
    {
        var data = await SeedAsync();
        var student = await factory.AddUserAsync();
        var stranger = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var token = TestContext.Current.CancellationToken;
        var contest = await db.Contests.SingleAsync(token);
        contest.IsOpen = open;
        var team = await db.Competitors.SingleAsync(token);
        var coachedUser = await db.OlympUsers.SingleAsync(user => user.Id == student.Id, token);
        coachedUser.CoachId = data.User.Id;
        var coachedTeam = TestEntities.CreateCompetitor(contest, "Coached team");
        coachedTeam.CoachId = data.User.Id;
        var publicTeam = TestEntities.CreateCompetitor(contest, "Public team");
        db.Competitors.AddRange(coachedTeam, publicTeam);
        await db.SaveChangesAsync(token);

        Submission Add(User author)
        {
            var item = TestEntities.CreateSubmission(author, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
            item.User = null!;
            item.Problem = null!;
            item.Compilator = null!;
            item.StatusCode = 2;
            db.Submissions.Add(item);
            return item;
        }
        var own = Add(open ? data.User : team);
        var coached = Add(open ? student : coachedTeam);
        var all = Add(open ? stranger : publicTeam);
        var otherKind = Add(open ? team : data.User);
        await db.SaveChangesAsync(token);
        using var client = await factory.LoginAsync(data.User.UserName!);
        string kind = open ? "training" : "contest";
        foreach (string path in new[] { $"/contests/{contest.Id}/submissions", ProblemPath(contest.Id, data.Problem.Id) })
        {
            string separator = path.Contains('?') ? "&" : "?";
            using var explicitKind = await GetAsync(client, path + separator + $"kind={(open ? "contest" : "training")}");
            Assert.NotNull(explicitKind.QuerySelector($"#submission-{otherKind.Id}"));
            using var emptyPair = await GetAsync(client, path + separator + $"scope=coached&kind={(open ? "contest" : "training")}");
            Assert.Empty(emptyPair.QuerySelectorAll("#submissions tbody tr"));
            Assert.Contains("scope=coached", RefreshUrl(emptyPair));
            Assert.Contains($"kind={(open ? "contest" : "training")}", RefreshUrl(emptyPair));
        }
        foreach (var (expectedScope, expectedSubmission) in new[] { ("own", own), ("coached", coached), ("all", all) })
        {
            foreach (string path in new[] { $"/contests/{contest.Id}/submissions", ProblemPath(contest.Id, data.Problem.Id) })
            {
                using var page = await GetAsync(client, path);
                Assert.Contains($"scope={expectedScope}", RefreshUrl(page));
                Assert.Contains($"kind={kind}", RefreshUrl(page));
                Assert.NotNull(page.QuerySelector($"#submission-{expectedSubmission.Id}"));
                Assert.Null(page.QuerySelector($"#submission-{otherKind.Id}"));
            }
            await db.Submissions.Where(item => item.Id == expectedSubmission.Id).ExecuteDeleteAsync(token);
        }
        foreach (string path in new[] { $"/contests/{contest.Id}/submissions", ProblemPath(contest.Id, data.Problem.Id) })
        {
            using var fallback = await GetAsync(client, path);
            Assert.Contains("scope=own", RefreshUrl(fallback));
            Assert.Contains($"kind={(open ? "contest" : "training")}", RefreshUrl(fallback));
            Assert.NotNull(fallback.QuerySelector($"#submission-{otherKind.Id}"));
        }
    }

    [Fact]
    public async Task GlobalDefaultRemainsPublicTrainingEvenWhenOnlyOtherPairsHaveSubmissions()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var token = TestContext.Current.CancellationToken;
        var team = await db.Competitors.SingleAsync(token);
        var training = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
        var competition = TestEntities.CreateSubmission(team, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
        foreach (var item in new[] { training, competition })
        {
            item.User = null!;
            item.Problem = null!;
            item.Compilator = null!;
            item.StatusCode = 2;
        }
        training.IsHidden = true;
        db.Submissions.AddRange(training, competition);
        await db.SaveChangesAsync(token);
        using var client = await factory.LoginAsync(data.User.UserName!);
        using var page = await GetAsync(client, "/submissions");
        Assert.Contains("scope=all", RefreshUrl(page));
        Assert.Contains("kind=training", RefreshUrl(page));
        Assert.Empty(page.QuerySelectorAll("#submissions tbody tr"));
        Assert.NotNull(page.QuerySelector("#submissions nav a[href*='kind=contest']"));
        Assert.NotNull(page.QuerySelector("#submissions nav a[aria-current='page'][href*='kind=training']"));
        using var explicitKind = await GetAsync(client, "/submissions?kind=contest");
        Assert.Contains("scope=all", RefreshUrl(explicitKind));
        Assert.NotNull(explicitKind.QuerySelector($"#submission-{competition.Id}"));
        using var explicitScope = await GetAsync(client, "/submissions?scope=own");
        Assert.Contains("kind=training", RefreshUrl(explicitScope));
        Assert.NotNull(explicitScope.QuerySelector($"#submission-{training.Id}"));
        await db.Submissions.Where(item => item.Id == competition.Id).ExecuteDeleteAsync(token);
        using var onlyOwn = await GetAsync(client, "/submissions");
        Assert.Empty(onlyOwn.QuerySelectorAll("#submissions tbody tr"));
        Assert.NotNull(onlyOwn.QuerySelector("#submissions nav a[href*='scope=own']"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitFiltersKeepEmptyResultsAndEmptyDefaultsKeepContestMode(bool open)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var token = TestContext.Current.CancellationToken;
        await db.Contests.Where(contest => contest.Id == data.Contest.Id).ExecuteUpdateAsync(set => set.SetProperty(contest => contest.IsOpen, open), token);
        using var client = await factory.LoginAsync(data.User.UserName!);
        foreach (string path in new[] { $"/contests/{data.Contest.Id}/submissions", ProblemPath(data.Contest.Id, data.Problem.Id) })
        {
            string separator = path.Contains('?') ? "&" : "?";
            using var empty = await GetAsync(client, path);
            Assert.Contains("scope=own", RefreshUrl(empty));
            Assert.Contains($"kind={(open ? "training" : "contest")}", RefreshUrl(empty));
            using var explicitFilters = await GetAsync(client, path + separator + "scope=coached&kind=contest");
            Assert.Contains("scope=coached", RefreshUrl(explicitFilters));
            Assert.Contains("kind=contest", RefreshUrl(explicitFilters));
            Assert.Empty(explicitFilters.QuerySelectorAll("#submissions tbody tr"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignParticipantCanSelectPublicTypeWhileOwnContestIgnoresFilters(bool open)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var token = TestContext.Current.CancellationToken;
        var foreignContest = await db.Contests.SingleAsync(token);
        foreignContest.IsOpen = open;
        var ownContest = TestEntities.CreateContest("Bound contest", foreignContest.StartTime, foreignContest.EndTime);
        var ownProblem = TestEntities.CreateProblem(ownContest);
        db.Problems.Add(ownProblem);
        await db.SaveChangesAsync(token);
        using var client = await TemporaryClientAsync(ownContest);
        var participant = await db.Competitors.SingleAsync(item => item.ContestId == ownContest.Id, token);
        var publicTeam = await db.Competitors.SingleAsync(item => item.ContestId == foreignContest.Id, token);
        Submission Add(User author, Problem problem)
        {
            var item = TestEntities.CreateSubmission(author, problem, data.Compiler, DateTimeOffset.UtcNow);
            item.User = null!;
            item.Problem = null!;
            item.Compilator = null!;
            item.StatusCode = 2;
            db.Submissions.Add(item);
            return item;
        }
        var training = Add(data.User, data.Problem);
        var competition = Add(publicTeam, data.Problem);
        var own = Add(participant, ownProblem);
        await db.SaveChangesAsync(token);
        foreach (string path in new[] { $"/contests/{foreignContest.Id}/submissions", ProblemPath(foreignContest.Id, data.Problem.Id) })
        {
            using var page = await GetAsync(client, path);
            Assert.Contains("scope=all", RefreshUrl(page));
            Assert.Contains($"kind={(open ? "training" : "contest")}", RefreshUrl(page));
            Assert.NotNull(page.QuerySelector($"#submission-{(open ? training.Id : competition.Id)}"));
            Assert.Null(page.QuerySelector("#submissions nav a[href*='scope=own']"));
            string separator = path.Contains('?') ? "&" : "?";
            using var explicitKind = await GetAsync(client, path + separator + "scope=own&kind=training");
            Assert.NotNull(explicitKind.QuerySelector($"#submission-{training.Id}"));
            Assert.Null(explicitKind.QuerySelector($"a[href='submissions/{training.Id}']"));
            Assert.Contains("scope=all", RefreshUrl(explicitKind));
            if (path.Contains("/problems", StringComparison.Ordinal))
                Assert.Contains("kind=training", explicitKind.QuerySelector("nav.problem-list a.list-group-item")!.GetAttribute("href")!);
        }
        foreach (string path in new[] { $"/contests/{ownContest.Id}/submissions", ProblemPath(ownContest.Id, ownProblem.Id) })
        {
            using var page = await GetAsync(client, path + (path.Contains('?') ? "&" : "?") + "scope=all&kind=training");
            Assert.NotNull(page.QuerySelector($"#submission-{own.Id}"));
            Assert.Empty(page.QuerySelectorAll("#submissions nav"));
            Assert.DoesNotContain("scope=", RefreshUrl(page));
            Assert.DoesNotContain("kind=", RefreshUrl(page));
        }
    }
}
