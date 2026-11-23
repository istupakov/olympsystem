using System.IO.Compression;
using System.Net;
using System.Text.Json;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class IcpcResultsExportTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task PreviewUsesOfficialStandingWithTiesAndRejectsPendingSubmissions()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Results", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-2).AddHours(5));
        contest.Abbr = "acm-" + Guid.NewGuid().ToString("N");
        contest.FreezeTime = contest.StartTime.AddMinutes(10);
        contest.OfficialTableText = "<p>Not the exported standings</p>";
        var problem = TestEntities.CreateProblem(contest, 3);
        problem.Name = "Русское название";
        var compiler = TestEntities.CreateCompilator();
        var teams = Enumerable.Range(0, 8).Select(i => TestEntities.CreateCompetitor(contest, "Team " + i)).ToArray();
        teams[3].IsApproved = false;
        teams[4].IsOutOfCompetition = true;
        teams[5].IsDisqualified = true;
        teams[6].IsHidden = true;
        foreach (var team in teams)
        {
            db.Competitors.Add(team);
            var accepted = TestEntities.CreateSubmission(team, problem, compiler, contest.StartTime.AddMinutes(20));
            accepted.StatusCode = team == teams[2] ? 3 : 2;
            db.Submissions.Add(accepted);
        }
        // The third included solver has a larger penalty and therefore follows the tied leaders.
        var wrong = TestEntities.CreateSubmission(teams[7], problem, compiler, contest.StartTime.AddMinutes(15));
        wrong.StatusCode = 3;
        db.Submissions.Add(wrong);
        foreach (var submission in new[]
        {
            TestEntities.CreateSubmission(teams[2], problem, compiler, contest.StartTime.AddSeconds(-1)),
            TestEntities.CreateSubmission(teams[2], problem, compiler, contest.EndTime.AddSeconds(1)),
            TestEntities.CreateSubmission(teams[2], problem, compiler, contest.StartTime.AddMinutes(1))
        })
        {
            submission.StatusCode = 2;
            submission.IsHidden = submission.CommitTime > contest.StartTime && submission.CommitTime < contest.EndTime;
            db.Submissions.Add(submission);
        }
        var compileError = TestEntities.CreateSubmission(teams[0], problem, compiler, contest.StartTime.AddMinutes(10));
        compileError.StatusCode = -1;
        db.Submissions.Add(compileError);
        var afterAccepted = TestEntities.CreateSubmission(teams[0], problem, compiler, contest.StartTime.AddMinutes(30));
        afterAccepted.StatusCode = -1001;
        db.Submissions.Add(afterAccepted);
        await db.SaveChangesAsync(token);
        db.UserClaims.AddRange(teams.Select((t, i) => new IdentityUserClaim<int> { UserId = t.Id, ClaimType = ClaimNames.IcpcTeamId, ClaimValue = (101 + i).ToString() }));
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<IcpcResultsService>();
        var preview = await service.PreviewAsync(contest.Id, token);
        Assert.Equal([1, 1, 3, 4], preview.Results.Teams.Select(t => t.Rank));
        Assert.Equal([1, 1, 1, 0], preview.Results.Teams.Select(t => t.Solved));
        Assert.Equal(TimeSpan.FromMinutes(40), preview.Results.Teams[2].TotalTime);
        Assert.Equal(2, preview.Results.Teams[0].Problems[0].NumJudged);
        Assert.Equal(TimeSpan.FromMinutes(20), preview.Results.Teams[0].TotalTime);
        Assert.Empty(preview.InvalidTeamIds);
        Assert.Equal(preview.Snapshot, (await service.PreviewAsync(contest.Id, token)).Snapshot);
        using var document = await new HtmlParser().ParseDocumentAsync(await RenderAsync<IcpcResultsExport>(
            $"/jury/contests/{contest.Id}/export", new() { ["ContestId"] = contest.Id }, Staff(1),
            component => ActAsync(component, "PreviewAsync")), token);
        Assert.Equal(4, document.QuerySelectorAll("tbody tr").Length);
        Assert.NotNull(document.QuerySelector("button[value=csv]:not([disabled])"));
        wrong.StatusCode = 0;
        db.Update(wrong);
        await db.SaveChangesAsync(token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.PreviewAsync(contest.Id, token));
        wrong.StatusCode = 3;
        db.Update(wrong);
        teams[2].Name = "Renamed";
        db.Update(teams[2]);
        await db.SaveChangesAsync(token);
        Assert.NotEqual(preview.Snapshot, (await service.PreviewAsync(contest.Id, token)).Snapshot);
    }

    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task DownloadRevalidatesSnapshotAndProtectsPostWithoutManagementAssignment(string role)
    {
        var token = TestContext.Current.CancellationToken;
        var actor = await _factory.AddUserAsync();
        await GrantRoleAsync(actor.Id, role);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Export", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1).AddHours(5));
        contest.Abbr = "acm-" + Guid.NewGuid().ToString("N");
        contest.IsHidden = true;
        var team = TestEntities.CreateCompetitor(contest, "Empty team");
        db.Competitors.Add(team);
        await db.SaveChangesAsync(token);
        var service = scope.ServiceProvider.GetRequiredService<IcpcResultsService>();
        var preview = await service.PreviewAsync(contest.Id, token);
        Assert.Single(preview.InvalidTeamIds);
        using var client = await _factory.LoginAsync(actor.UserName!);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var fields = new Dictionary<string, string>
        {
            ["snapshot"] = preview.Snapshot,
            ["format"] = "ccs",
            [$"medal-{team.Id}"] = "",
            [$"site-{team.Id}"] = "Site, A",
            [$"citation-{team.Id}"] = ""
        };
        string path = $"/jury/contests/{contest.Id}/results/export";
        using var download = await client.PostAsync(path, new FormUrlEncodedContent(fields), token);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.True(download.Headers.CacheControl!.NoStore);
        using (var zip = new ZipArchive(new MemoryStream(await download.Content.ReadAsByteArrayAsync(token))))
        {
            using var json = JsonDocument.Parse(zip.GetEntry("awards.json")!.Open());
            Assert.Equal("Site, A", json.RootElement[0].GetProperty("citation").GetString());
        }
        fields["format"] = "csv";
        using var noId = await client.PostAsync(path, new FormUrlEncodedContent(fields), token);
        Assert.Equal(HttpStatusCode.Redirect, noId.StatusCode);
        db.UserClaims.Add(new IdentityUserClaim<int> { UserId = team.Id, ClaimType = ClaimNames.IcpcTeamId, ClaimValue = "123" });
        await db.SaveChangesAsync(token);
        fields["snapshot"] = (await service.PreviewAsync(contest.Id, token)).Snapshot;
        using var csv = await client.PostAsync(path, new FormUrlEncodedContent(fields), token);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.EndsWith("123,1,,0,0,0,\"Site, A\",\r\n", await csv.Content.ReadAsStringAsync(token));
        team.IsOutOfCompetition = true;
        db.Update(team);
        await db.SaveChangesAsync(token);
        using var stale = await client.PostAsync(path, new FormUrlEncodedContent(fields), token);
        Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.Contains("ResultsError", stale.Headers.Location!.OriginalString);
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        using var crossSite = await client.PostAsync(path, new FormUrlEncodedContent(fields), token);
        Assert.Equal(HttpStatusCode.BadRequest, crossSite.StatusCode);
        using var anonymous = _factory.CreateSiteClient();
        using var denied = await anonymous.PostAsync(path, new FormUrlEncodedContent(fields), token);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("Login", denied.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("school-", -1)]
    [InlineData("acm-", 1)]
    public async Task OnlyFinishedAcmContestsAreAvailable(string prefix, int days)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Unavailable", DateTimeOffset.UtcNow.AddDays(days), DateTimeOffset.UtcNow.AddDays(days).AddHours(5));
        contest.Abbr = prefix + Guid.NewGuid().ToString("N");
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider.GetRequiredService<IcpcResultsService>()
            .PreviewAsync(contest.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingMalformedAndDuplicateExternalIdsBlockCsvButKeepAllCcsTeams()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("IDs", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1).AddHours(5));
        contest.Abbr = "acm-" + Guid.NewGuid().ToString("N");
        var teams = Enumerable.Range(0, 5).Select(i => TestEntities.CreateCompetitor(contest, "ID team " + i)).ToArray();
        db.Competitors.AddRange(teams);
        await db.SaveChangesAsync(token);
        db.UserClaims.AddRange(new[] { (0, "0123"), (1, "123"), (2, "bad"), (3, "456"), (3, "789") }
            .Select(c => new IdentityUserClaim<int> { UserId = teams[c.Item1].Id, ClaimType = ClaimNames.IcpcTeamId, ClaimValue = c.Item2 }));
        await db.SaveChangesAsync(token);
        var preview = await scope.ServiceProvider.GetRequiredService<IcpcResultsService>().PreviewAsync(contest.Id, token);
        Assert.Equal(5, preview.Results.Teams.Count);
        Assert.Equal(5, preview.InvalidTeamIds.Count);
        Assert.All(preview.Results.Teams, t => Assert.Null(t.IcpcId));
    }
}
