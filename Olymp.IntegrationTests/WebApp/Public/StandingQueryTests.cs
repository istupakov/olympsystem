using System.Security.Claims;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

[Trait("Category", "Integration")]
public class StandingQueryTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(2025, false)]
    [InlineData(2026, false)]
    [InlineData(2026, true)]
    public async Task SlimLoadPreservesCellsOrderHighlightsAndRenderedTable(int year, bool school)
    {
        var token = TestContext.Current.CancellationToken;
        var account = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var member = await db.OlympUsers.SingleAsync(item => item.Id == account.Id, token);
        var start = new DateTimeOffset(year, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var contest = TestEntities.CreateContest("Standing query", start, start.AddHours(5));
        contest.IsOpen = true;
        contest.Abbr = school ? "school-query" : "acm-query";
        contest.FreezeTime = start.AddHours(1);
        var compiler = TestEntities.CreateCompilator();
        var problems = new[] { TestEntities.CreateProblem(contest), TestEntities.CreateProblem(contest, 2) };
        problems[1].IsActive = false;
        foreach (var problem in problems) contest.Problems.Add(problem);
        var users = Enumerable.Range(0, 6).Select(index => TestEntities.CreateCompetitor(contest, $"Team {index}")).ToArray();
        users[0].Members.Add(member);
        users[0].CoachId = member.Id;
        users[1].IsOutOfCompetition = true;
        users[2].IsDisqualified = true;
        users[3].IsHidden = true;
        users[4].IsApproved = false;
        foreach (var user in users) contest.Competitors.Add(user);
        int[] statuses = [-1001, 2, 2, -1, -10, 0, 1, 2];
        foreach (var author in users.Cast<User>().Append(member))
        {
            for (int i = 0; i < 8; i++)
            {
                var time = start.AddSeconds(i == 0 ? 119 : i == 7 ? 3601 : i * 600);
                foreach (var problem in problems)
                {
                    var submission = TestEntities.CreateSubmission(author, problem, compiler, time, new byte[2048]);
                    submission.StatusCode = statuses[i];
                    submission.Score = school ? i * 10 : null;
                    submission.IsHidden = i == 2;
                    problem.Submissions.Add(submission);
                }
            }
        }
        db.Contests.Add(contest);
        await db.SaveChangesAsync(token);

        var original = await db.Contests.AsNoTrackingWithIdentityResolution()
            .Include(item => item.Competitors).ThenInclude(item => item.Members)
            .Include(item => item.Problems).ThenInclude(item => item.Submissions).ThenInclude(item => item.User)
            .AsSplitQuery().SingleAsync(item => item.Id == contest.Id, token);
        var slim = await StandingQuery.LoadAsync(db, contest.Id, token);
        Assert.All(slim.Problems.SelectMany(item => item.Submissions), item => Assert.Empty(item.SourceCode));
        Assert.All(slim.Competitors, team => Assert.Same(team, slim.Problems.SelectMany(item => item.Submissions).First(item => item.UserId == team.Id).User));
        var graphUsers = slim.Competitors.Cast<User>().Concat(slim.Competitors.SelectMany(team => team.Members))
            .Concat(slim.Problems.SelectMany(problem => problem.Submissions).Select(submission => submission.User));
        foreach (var group in graphUsers.GroupBy(user => user.Id))
            Assert.All(group, user => Assert.Same(group.First(), user));
        var viewer = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, member.Id.ToString())], "test"));
        foreach (bool official in new[] { false, true })
            foreach (bool notFreeze in new[] { false, true })
                foreach (var virtualStart in new DateTimeOffset?[] { null, start.AddMinutes(10) })
                    foreach (var now in new[] { contest.FreezeTime.Value, contest.FreezeTime.Value.AddTicks(1), start.AddHours(6) })
                    {
                        var expected = Standing.Create(original, !official, official, notFreeze, virtualStart, now);
                        var actual = Standing.Create(slim, !official, official, notFreeze, virtualStart, now);
                        Assert.Equal(Snapshot(expected), Snapshot(actual));
                        Assert.Equal(StandingHighlights.GetUserIds(expected.Results.Select(row => row.User), viewer).Order(),
                            StandingHighlights.GetUserIds(actual.Results.Select(row => row.User), viewer).Order());
                    }
        var oldTable = Standing.Create(original, true, false, false, null, start.AddHours(6));
        var newTable = Standing.Create(slim, true, false, false, null, start.AddHours(6));
        Assert.Equal(await RenderAsync<StandingTable>("/contests/1/standing", new() { [nameof(StandingTable.Standing)] = oldTable }),
            await RenderAsync<StandingTable>("/contests/1/standing", new() { [nameof(StandingTable.Standing)] = newTable }));
    }

    private static string Snapshot(Standing standing) => JsonSerializer.Serialize(new
    {
        standing.IsFrozen,
        standing.IsOfficial,
        standing.VirtualStartTime,
        Rows = standing.Results.Select(row => new
        {
            row.User.Id,
            row.User.Name,
            row.Score,
            row.SummaryTime,
            row.LastSuccessTime,
            row.StartTime,
            row.IsVirtual,
            Cells = row.ProblemResults.Select(cell => new
            {
                cell.Problem.Id,
                cell.IsSuccess,
                cell.CommitNumber,
                cell.UnknownCommitNumber,
                cell.LastUnknownCommitTime,
                cell.LastCommitTime,
                cell.IsFirstSuccess,
                cell.Score,
                cell.MaxScore
            })
        })
    });
}
