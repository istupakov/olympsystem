using System.Net;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Submissions;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

public partial class PracticeSubmissionTests
{
    [Theory]
    [InlineData("running", "en", true)]
    [InlineData("running", "ru", true)]
    [InlineData("start", "en", true)]
    [InlineData("before", "en", false)]
    [InlineData("end", "en", false)]
    [InlineData("open", "en", false)]
    [InlineData("hidden", "en", false)]
    public async Task ClosedContestRestrictsPersonalSourcesAndStaleSubmissionForms(string state, string culture, bool blocked)
    {
        var data = await SeedAsync();
        var start = DateTimeOffset.UtcNow;
        var clock = new RestrictionClock(start.AddTicks(-1));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var closed = TestEntities.CreateContest("Closed contest", start, start.AddHours(1));
        closed.IsOpen = state == "open";
        closed.IsHidden = state == "hidden";
        db.Contests.Add(closed);
        var ids = new List<int>();
        foreach (var time in new[] { start.AddDays(-1), start.AddMinutes(1) })
        {
            var submission = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, time);
            submission.User = null!;
            submission.Problem = null!;
            submission.Compilator = null!;
            submission.Text = Source;
            submission.StatusCode = -1;
            submission.CheckResults.Add(new CheckResult
            {
                Submission = submission,
                StatusCode = -1,
                CheckTime = time,
                Log = "PRIVATE_DIAGNOSTICS"
            });
            db.Submissions.Add(submission);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            ids.Add(submission.Id);
        }
        await using var site = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<TimeProvider>(clock)));
        using var client = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = data.User.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        string path = ProblemPath(data.Contest.Id, data.Problem.Id);
        using var initial = await GetAsync(client, path);
        var fields = Fields(initial, data.Compiler.Id, Source);
        clock.Now = state switch
        {
            "before" => start.AddTicks(-1),
            "start" => start,
            "end" => closed.EndTime,
            _ => start.AddMinutes(30)
        };
        string message = culture == "ru" ? "Во время закрытого соревнования" : "while a closed contest is running";
        foreach (int id in ids)
        {
            foreach (string endpoint in new[] { $"/submissions/{id}", $"/submissions/{id}/download" })
            {
                using var response = await client.GetAsync(endpoint, TestContext.Current.CancellationToken);
                bool download = endpoint.EndsWith("/download", StringComparison.Ordinal);
                Assert.Equal(blocked && download ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
                if (blocked)
                {
                    string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                    Assert.DoesNotContain(Source, body);
                    Assert.DoesNotContain("PRIVATE_DIAGNOSTICS", body);
                    if (download)
                    {
                        using var details = JsonDocument.Parse(body);
                        Assert.Contains(message, details.RootElement.GetProperty("detail").GetString()!);
                    }
                    else
                    {
                        Assert.Contains(message, body);
                        using var page = await ParseAsync(response);
                        Assert.NotNull(page.QuerySelector("main dl"));
                        Assert.Null(page.QuerySelector("main pre"));
                        Assert.Null(page.QuerySelector("#compilation-errors"));
                        Assert.Null(page.QuerySelector("main a[href$='/download']"));
                    }
                }
            }
        }
        using var history = await GetAsync(client, "/submissions");
        Assert.Equal(2, history.QuerySelectorAll("#submissions tbody tr").Length);
        Assert.NotNull(history.QuerySelector($"a[href='submissions/{ids[0]}']"));
        using var workspace = await GetAsync(client, path);
        Assert.Equal(!blocked, workspace.QuerySelector("#submission-code") is not null);
        using var content = new FormUrlEncodedContent(fields);
        using var posted = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
        Assert.Equal(blocked ? HttpStatusCode.BadRequest : HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Equal(blocked ? 2 : 3, await db.Submissions.CountAsync(TestContext.Current.CancellationToken));
        using (var serviceScope = site.Services.CreateScope())
        {
            var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, data.User.Id.ToString())], "Test"));
            var result = await serviceScope.ServiceProvider.GetRequiredService<SubmissionService>().SubmitAsync(
                actor, data.Contest.Id, data.Problem.Id, new SubmissionInput { CompilatorId = data.Compiler.Id, Code = Source },
                null, forJury: false, TestContext.Current.CancellationToken);
            Assert.Equal(!blocked, result.Id.HasValue);
            Assert.False(result.NotFound);
            Assert.Equal(blocked, result.Error is not null);
        }
        Assert.Equal(blocked ? 2 : 4, await db.Submissions.CountAsync(TestContext.Current.CancellationToken));
        clock.Now = closed.EndTime;
        using var restored = await GetAsync(client, $"/submissions/{ids[0]}");
        Assert.Equal(Source, restored.QuerySelector($"#submission-source-{ids[0]}")!.TextContent);
        Assert.Contains("PRIVATE_DIAGNOSTICS", restored.QuerySelector("#compilation-errors")!.TextContent);
        using var restoredDownload = await client.GetAsync($"/submissions/{ids[0]}/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, restoredDownload.StatusCode);
        Assert.Equal(Source, await restoredDownload.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var restoredPost = await PostAsync(client, path, data.Compiler.Id, Source);
        Assert.Equal(HttpStatusCode.Redirect, restoredPost.StatusCode);
        Assert.Equal(blocked ? 3 : 5, await db.Submissions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("coach", false)]
    [InlineData("member", false)]
    [InlineData(RoleNames.Jury, false)]
    [InlineData(RoleNames.Admin, false)]
    [InlineData("participant", true)]
    public async Task ClosedContestRestrictionAppliesToAllPersonalAccountsButNotParticipantOrStaffOperations(string actor, bool allowed)
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var closed = TestEntities.CreateContest("Running closed contest", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        db.Contests.Add(closed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string username = data.User.UserName!;
        User author = data.User;
        if (actor == "participant")
        {
            using var participantClient = await TemporaryClientAsync(closed);
            author = await db.Competitors.SingleAsync(item => item.ContestId == closed.Id, TestContext.Current.CancellationToken);
            username = author.UserName!;
            var problem = TestEntities.CreateProblem(closed);
            db.Problems.Add(problem);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            data.Problem = problem;
            data.Contest = closed;
        }
        else if (actor is "coach" or "member")
        {
            var team = await db.Competitors.SingleAsync(TestContext.Current.CancellationToken);
            if (actor == "coach")
                team.CoachId = data.User.Id;
            author = team;
        }
        else
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
            await roles.CreateAsync(new(actor));
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var storedUser = await users.FindByIdAsync(data.User.Id.ToString());
            Assert.True((await users.AddToRoleAsync(storedUser!, actor)).Succeeded);
        }
        var submission = TestEntities.CreateSubmission(author, data.Problem, data.Compiler, DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(username);
        foreach (string path in new[] { $"/submissions/{submission.Id}", $"/submissions/{submission.Id}/download" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            bool download = path.EndsWith("/download", StringComparison.Ordinal);
            Assert.Equal(!allowed && download ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
            if (!download)
            {
                using var page = await ParseAsync(response);
                Assert.Equal(allowed, page.QuerySelector($"#submission-source-{submission.Id}") is not null);
            }
        }
        if (allowed)
        {
            using var posted = await PostAsync(client, ProblemPath(data.Contest.Id, data.Problem.Id), data.Compiler.Id, Source);
            Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        }
        else
        {
            using var workspace = await GetAsync(client, ProblemPath(data.Contest.Id, data.Problem.Id));
            Assert.Null(workspace.QuerySelector("#submission-code"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, data.User.Id.ToString()) };
            bool staff = actor is RoleNames.Jury or RoleNames.Admin;
            if (staff)
                claims.Add(new(ClaimTypes.Role, actor));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            var service = scope.ServiceProvider.GetRequiredService<SubmissionService>();
            var input = new SubmissionInput { CompilatorId = data.Compiler.Id, Code = Source };
            var rejected = await service.SubmitAsync(principal, data.Contest.Id, data.Problem.Id, input,
                null, forJury: false, TestContext.Current.CancellationToken);
            Assert.Null(rejected.Id);
            Assert.NotNull(rejected.Error);
            Assert.Equal(1, await db.Submissions.CountAsync(TestContext.Current.CancellationToken));
            if (staff)
            {
                using var download = await client.GetAsync($"/jury/submissions/{submission.Id}/download", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.Equal(submission.Text, await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                var submitted = await service.SubmitAsync(principal, data.Contest.Id, data.Problem.Id, input,
                    null, forJury: true, TestContext.Current.CancellationToken);
                Assert.NotNull(submitted.Id);
                Assert.Null(submitted.Error);
            }
        }
        var stranger = await factory.AddUserAsync();
        using var strangerClient = await factory.LoginAsync(stranger.UserName!);
        using var denied = await strangerClient.GetAsync($"/submissions/{submission.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var deniedDownload = await strangerClient.GetAsync($"/submissions/{submission.Id}/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, deniedDownload.StatusCode);
    }

    [Fact]
    public async Task RestrictionRemainsUntilAllRunningClosedContestsAreOpenedOrHidden()
    {
        var data = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var now = DateTimeOffset.UtcNow;
        var first = TestEntities.CreateContest("First closed", now.AddHours(-1), now.AddHours(1));
        var second = TestEntities.CreateContest("Second closed", now.AddHours(-1), now.AddHours(1));
        var submission = TestEntities.CreateSubmission(data.User, data.Problem, data.Compiler, now);
        submission.User = null!;
        submission.Problem = null!;
        submission.Compilator = null!;
        submission.Text = Source;
        db.AddRange(first, second, submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await factory.LoginAsync(data.User.UserName!);
        string path = ProblemPath(data.Contest.Id, data.Problem.Id);
        string downloadPath = $"/submissions/{submission.Id}/download";
        using var blocked = await client.GetAsync(downloadPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        first.IsOpen = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var stillBlocked = await client.GetAsync(downloadPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, stillBlocked.StatusCode);
        using var workspace = await GetAsync(client, path);
        Assert.Null(workspace.QuerySelector("#submission-code"));
        second.IsHidden = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var restored = await client.GetAsync(downloadPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(Source, await restored.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var posted = await PostAsync(client, path, data.Compiler.Id, Source);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
    }

    private sealed class RestrictionClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
