using System.IO.Compression;
using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ContestExportDownloadTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(RoleNames.Jury, false)]
    [InlineData(RoleNames.Admin, false)]
    [InlineData(RoleNames.Jury, true)]
    [InlineData(RoleNames.Admin, true)]
    public async Task StaffDownloadsPrintableMarkdownWithoutManagementAssignment(string role, bool pdf)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, role);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Русский турнир", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)), new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)).AddHours(5));
        contest.Abbr = "tex-" + Guid.NewGuid().ToString("N");
        contest.IsHidden = true;
        var problem = TestEntities.CreateProblem(contest, 2);
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        problem.Tests.Add(new Test { Problem = problem, Number = 1, IsActive = true, IsOpen = true, Input = "1\n2\n"u8.ToArray(), Output = "YES\n"u8.ToArray() });
        problem.Tests.Add(new Test { Problem = problem, Number = 2, IsActive = true, IsOpen = false, Input = "secret"u8.ToArray(), Output = [] });
        db.Problems.Add(problem);
        await db.SaveChangesAsync(token);
        using var client = await _factory.LoginAsync(user.UserName!);
        using var response = await client.GetAsync($"/jury/contests/{contest.Id}/statements/{(pdf ? "pdf" : "export")}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(pdf ? "application/pdf" : "application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal($"{contest.Abbr}-statements.{(pdf ? "pdf" : "zip")}", response.Content.Headers.ContentDisposition!.FileNameStar);
        byte[] data = await response.Content.ReadAsByteArrayAsync(token);
        if (pdf)
        {
            Assert.True(data.AsSpan().StartsWith("%PDF-"u8));
            Assert.Null(await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.ProblemPdfId).SingleAsync(token));
            return;
        }
        using var zip = new ZipArchive(new MemoryStream(data));
        Assert.NotNull(zip.GetEntry("statements.tex"));
        Assert.NotNull(zip.GetEntry("olymp.sty"));
        Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("samples/", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task ContestAttachmentNamesUseAbbreviation(string role)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, role);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Attachment names", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        contest.Abbr = "контест-" + Guid.NewGuid().ToString("N");
        contest.ProblemPdf = new Blob { Data = [1, 2, 3] };
        contest.TestZip = new Blob { Data = [4, 5, 6] };
        contest.FinalTableText = "<table>Final</table>";
        contest.OfficialTableText = "<table>Official</table>";
        db.Contests.Add(contest);
        await db.SaveChangesAsync(token);
        using var client = await _factory.LoginAsync(user.UserName!);
        foreach (var (kind, extension) in new[] { ("problems", "pdf"), ("tests", "zip"), ("final", "html"), ("official", "html") })
        {
            using var response = await client.GetAsync($"/jury/contests/{contest.Id}/files/{kind}", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal($"{contest.Abbr}-{kind}.{extension}", response.Content.Headers.ContentDisposition!.FileNameStar);
        }
    }

    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task StaffCanDownloadOneContestAndOnlyAdminCanDownloadAll(string role)
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
        using var oldExport = await client.GetAsync("/jury/contests/export", token);
        Assert.NotEqual("application/zip", oldExport.Content.Headers.ContentType?.MediaType);
        Assert.Null(oldExport.Content.Headers.ContentDisposition);
        foreach (string url in new[] { $"/jury/contests/{contest.Id}/export/download", "/admin/contests/export?Search=missing&Hidden=false" })
        {
            using var response = await client.GetAsync(url, token);
            if (role == RoleNames.Jury && url.Contains("export?", StringComparison.Ordinal))
            {
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Contains("AccessDenied", response.Headers.Location!.OriginalString);
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal(url.StartsWith("/admin/", StringComparison.Ordinal) ? "contests-export.zip" : $"{contest.Abbr}-export.zip",
                response.Content.Headers.ContentDisposition.FileNameStar);
            using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(token)));
            Assert.NotNull(zip.GetEntry(contest.Abbr + "/contest.json"));
        }
        using var missing = await client.GetAsync($"/jury/contests/{int.MaxValue}/export/download", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var details = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestDetails>(
            $"/jury/contests/{contest.Id}", new() { ["Id"] = contest.Id }, Staff(user.Id, role)));
        Assert.NotNull(details.QuerySelector($"a[href='jury/contests/{contest.Id}/export']"));
        foreach (string view in new[] { "", "archive" })
        {
            using var list = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestList>(
                "/jury/contests/" + view, new() { ["View"] = view.Length == 0 ? null : view }, Staff(user.Id, role)));
            Assert.Null(list.QuerySelector("a[href='jury/contests/export'][data-enhance-nav='false']"));
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
            foreach (string url in new[] { "/jury/contests/1/export", "/jury/contests/1/export/download", "/admin/contests/export", "/jury/contests/1/statements/export", "/jury/contests/1/statements/pdf" })
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
        using var response = await client.GetAsync($"/jury/contests/{contest.Id}/export/download", token);
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
