using System.IO.Compression;
using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ContestExportDownloadTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task StaffCanDownloadHiddenContestAndAllContestsWithoutManagementAssignment(string role)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, role);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Hidden archive", DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(-1).AddHours(5));
        contest.Abbr = "export-" + Guid.NewGuid().ToString("N");
        contest.IsHidden = true;
        db.Contests.Add(contest);
        await db.SaveChangesAsync(token);
        using var client = await _factory.LoginAsync(user.UserName!);
        foreach (string url in new[] { $"/jury/contests/{contest.Id}/export", "/jury/contests/export?Search=missing&Hidden=false" })
        {
            using var response = await client.GetAsync(url, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(token)));
            Assert.NotNull(zip.GetEntry(contest.Abbr + "/contest.json"));
        }
        using var missing = await client.GetAsync($"/jury/contests/{int.MaxValue}/export", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var details = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestDetails>(
            $"/jury/contests/{contest.Id}", new() { ["Id"] = contest.Id }, Staff(user.Id, role)));
        Assert.NotNull(details.QuerySelector($"a[href='jury/contests/{contest.Id}/export'][data-enhance-nav='false']"));
        foreach (string view in new[] { "", "archive" })
        {
            using var list = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestList>(
                "/jury/contests/" + view, new() { ["View"] = view.Length == 0 ? null : view }, Staff(user.Id, role)));
            Assert.NotNull(list.QuerySelector("a[href='jury/contests/export'][data-enhance-nav='false']"));
        }
    }

    [Fact]
    public async Task AnonymousOrdinaryAndIncompleteAccountsCannotDownloadEitherMode()
    {
        var ordinary = await _factory.AddUserAsync();
        var incomplete = await _factory.AddUserAsync();
        await GrantRoleAsync(incomplete.Id, RoleNames.Jury);
        using var anonymous = _factory.CreateSiteClient();
        using var signedIn = await _factory.LoginAsync(ordinary.UserName!);
        using var needsMigration = await _factory.LoginAsync(incomplete.UserName!);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OlympContext>().Users.Where(item => item.Id == incomplete.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NormalizedEmail, (string?)null), TestContext.Current.CancellationToken);
        foreach (var client in new[] { anonymous, signedIn, needsMigration })
            foreach (string url in new[] { "/jury/contests/1/export", "/jury/contests/export" })
            {
                using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.NotEqual("application/zip", response.Content.Headers.ContentType?.MediaType);
            }
    }

    [Fact]
    public async Task InvalidArchiveReturnsLocalizedErrorInsteadOfPartialZip()
    {
        var token = TestContext.Current.CancellationToken;
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Invalid path", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        contest.Abbr = "../invalid";
        db.Contests.Add(contest);
        await db.SaveChangesAsync(token);
        using var client = await _factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
        using var response = await client.GetAsync($"/jury/contests/{contest.Id}/export", token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        string body = await response.Content.ReadAsStringAsync(token);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        Assert.Contains("Не удалось", document.RootElement.GetProperty("title").GetString());
        Assert.Contains($"contestId={contest.Id}", document.RootElement.GetProperty("detail").GetString());
        contest.Abbr = "valid-" + Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(token);
    }
}
