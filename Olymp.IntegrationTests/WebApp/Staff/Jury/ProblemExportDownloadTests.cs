using System.Net;

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
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ProblemExportDownloadTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task MissingEmptyAndIncompatibleContestsReturnErrorsBeforeSendingZip()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = await _factory.AddUserAsync();
        await GrantRoleAsync(admin.Id, RoleNames.Admin);
        using var client = await _factory.LoginAsync(admin.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
        using var missing = await client.GetAsync($"/jury/contests/{int.MaxValue}/problems/export", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Export errors", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        db.Contests.Add(contest);
        await db.SaveChangesAsync(token);
        string url = $"/jury/contests/{contest.Id}/problems/export";
        using var empty = await client.GetAsync(url, token);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("application/problem+json", empty.Content.Headers.ContentType!.MediaType);
        var problem = TestEntities.CreateProblem(contest);
        problem.Tests.Add(new Test { Problem = problem, Number = 1, Input = [], Output = [], IsOpen = false });
        problem.Tests.Add(new Test { Problem = problem, Number = 2, Input = [], Output = [], IsOpen = true });
        db.Problems.Add(problem);
        await db.SaveChangesAsync(token);
        using var invalid = await client.GetAsync(url, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        Assert.Null(invalid.Content.Headers.ContentDisposition);
        using var body = System.Text.Json.JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(token));
        Assert.Contains("открытые тесты", body.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(RoleNames.Admin, false, true)]
    [InlineData(RoleNames.Jury, true, true)]
    [InlineData(RoleNames.Jury, false, true)]
    [InlineData(null, false, false)]
    public async Task DownloadRequiresJuryRoleAndSettingsButtonRequiresContestManagement(string? role, bool assigned, bool allowed)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await _factory.AddUserAsync();
        if (role is not null) await GrantRoleAsync(user.Id, role);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Problem export", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        contest.IsHidden = true;
        var problem = TestEntities.CreateProblem(contest);
        problem.IsActive = false;
        db.Problems.Add(problem);
        await db.SaveChangesAsync(token);
        if (assigned)
        {
            db.UserClaims.Add(new IdentityUserClaim<int> { UserId = user.Id, ClaimType = ClaimNames.ContestManage, ClaimValue = contest.Id.ToString() });
            await db.SaveChangesAsync(token);
        }
        string url = $"/jury/contests/{contest.Id}/problems/export";
        using var client = await _factory.LoginAsync(user.UserName!);
        using var response = await client.GetAsync(url, token);
        if (!allowed)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("AccessDenied", response.Headers.Location!.OriginalString);
            Assert.NotEqual("application/zip", response.Content.Headers.ContentType?.MediaType);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal($"{contest.Abbr}-problems.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
            Assert.True(response.Headers.CacheControl!.NoStore);
            using var archive = new MemoryStream(await response.Content.ReadAsByteArrayAsync(token));
            Assert.Equal(problem.Id, Assert.Single((await ProblemArchive.ReadAsync(archive, new(), token)).Problems).Manifest.Id);
        }
        if (role is null) return;
        using var problems = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Problems>(
            $"/jury/contests/{contest.Id}/problems", new() { ["ContestId"] = contest.Id }, Staff(user.Id, role)));
        using var settings = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(
            $"/jury/contests/{contest.Id}/edit", new() { ["Id"] = contest.Id }, Staff(user.Id, role)));
        string selector = $"a[href='{url.TrimStart('/')}'][data-enhance-nav='false']";
        Assert.Null(problems.QuerySelector(selector));
        Assert.Null(settings.QuerySelector(selector));
        if (assigned)
        {
            await db.UserClaims.Where(claim => claim.UserId == user.Id && claim.ClaimType == ClaimNames.ContestManage).ExecuteDeleteAsync(token);
            using var revoked = await client.GetAsync(url, token);
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }
        using var anonymous = _factory.CreateSiteClient();
        using var guest = await anonymous.GetAsync(url, token);
        Assert.Equal(HttpStatusCode.Redirect, guest.StatusCode);
        Assert.Contains("Login", guest.Headers.Location!.OriginalString);
    }
}
