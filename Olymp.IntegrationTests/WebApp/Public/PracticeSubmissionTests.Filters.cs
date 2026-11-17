using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

public partial class PracticeSubmissionTests
{
    [Fact]
    public async Task IndependentSelectorsKeepClickedChoiceAndSwitchOtherChoiceOnlyForEmptyCombinations()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var team = await db.Competitors.Include(item => item.Members).SingleAsync(TestContext.Current.CancellationToken);
        team.Members.Clear();
        team.CoachId = data.User.Id;
        Submission Add(User user)
        {
            var submission = TestEntities.CreateSubmission(user, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
            submission.User = null!;
            submission.Problem = null!;
            submission.Compilator = null!;
            submission.StatusCode = 2;
            db.Submissions.Add(submission);
            return submission;
        }
        var training = Add(data.User);
        var competition = Add(team);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        foreach (string path in new[] { "/submissions", $"/contests/{data.Contest.Id}/submissions", ProblemPath(data.Contest.Id, data.Problem.Id) })
        {
            using var initial = await GetAsync(client, path);
            Assert.Equal(2, initial.QuerySelectorAll("#submissions nav").Length);
            Assert.Equal(5, initial.QuerySelectorAll("#submissions nav a").Length);
            Assert.Equal($"submission-{training.Id}", Assert.Single(initial.QuerySelectorAll("#submissions tbody tr")).Id);
            Assert.Empty(initial.QuerySelectorAll("#submissions tbody .badge"));
            string coachedUrl = initial.QuerySelector("#submissions nav a[href*='scope=coached']")!.GetAttribute("href")!;
            Assert.Contains("kind=contest", coachedUrl);
            Assert.DoesNotContain("page=", coachedUrl);
            if (path.Contains("problemId", StringComparison.Ordinal)) Assert.Contains($"problemId={data.Problem.Id}", coachedUrl);
            using var coached = await GetAsync(client, coachedUrl);
            Assert.Equal($"submission-{competition.Id}", Assert.Single(coached.QuerySelectorAll("#submissions tbody tr")).Id);
            string trainingUrl = coached.QuerySelector("#submissions nav a[href*='kind=training']")!.GetAttribute("href")!;
            Assert.Contains("scope=own", trainingUrl);
            using var own = await GetAsync(client, trainingUrl);
            Assert.Equal($"submission-{training.Id}", Assert.Single(own.QuerySelectorAll("#submissions tbody tr")).Id);
            string allUrl = coached.QuerySelector("#submissions nav a[href*='scope=all']")!.GetAttribute("href")!;
            Assert.Contains("kind=contest", allUrl);
            using var all = await GetAsync(client, allUrl);
            Assert.Equal($"submission-{competition.Id}", Assert.Single(all.QuerySelectorAll("#submissions tbody tr")).Id);
            Assert.Empty(all.QuerySelectorAll("#submissions tbody a[href^='submissions/']"));
        }
    }

    [Fact]
    public async Task HiddenAuthorHasOnlyTypeSelectorAndChangingTypeResetsPagination()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Users.Where(item => item.Id == data.User.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsHidden, true), TestContext.Current.CancellationToken);
        var team = await db.Competitors.SingleAsync(TestContext.Current.CancellationToken);
        team.IsHidden = true;
        for (int i = 0; i < 22; i++)
        {
            var submission = TestEntities.CreateSubmission(i == 0 ? team : data.User, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
            submission.User = null!;
            submission.Problem = null!;
            submission.Compilator = null!;
            db.Submissions.Add(submission);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        using var first = await GetAsync(client, "/submissions?scope=own&kind=training");
        var selector = Assert.Single(first.QuerySelectorAll("#submissions nav.btn-group"));
        Assert.Equal(2, selector.QuerySelectorAll("a").Length);
        string next = first.QuerySelector("a[rel='next']")!.GetAttribute("href")!;
        Assert.Contains("scope=own", next);
        Assert.Contains("kind=training", next);
        using var second = await GetAsync(client, next);
        string contest = second.QuerySelector("#submissions nav a[href*='kind=contest']")!.GetAttribute("href")!;
        Assert.DoesNotContain("page=", contest);
        using var selected = await GetAsync(client, contest);
        Assert.Single(selected.QuerySelectorAll("#submissions tbody tr"));
    }
}
