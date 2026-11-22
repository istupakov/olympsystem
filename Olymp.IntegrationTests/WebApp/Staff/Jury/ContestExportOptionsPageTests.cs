using System.IO.Compression;
using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ContestExportOptionsPageTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task ContestPageUsesSharedUncheckedOptionsAndHasOnlyGeneratedPdfOutsideArchive()
    {
        var actor = await _factory.AddUserAsync();
        await GrantRoleAsync(actor.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Export options", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        contest.Abbr = "options-" + Guid.NewGuid().ToString("N");
        var problem = TestEntities.CreateProblem(contest);
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        db.Problems.Add(problem);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestExport>(
            $"/jury/contests/{contest.Id}/export", new() { ["ContestId"] = contest.Id }, Staff(actor.Id, RoleNames.Jury)));
        var form = page.QuerySelector("form")!;
        Assert.Equal($"jury/contests/{contest.Id}/export/download", form.GetAttribute("action"));
        Assert.Equal("get", form.GetAttribute("method"));
        var fields = form.QuerySelectorAll("input[type=checkbox]");
        Assert.Equal(10, fields.Length);
        Assert.All(fields, input => Assert.False(input.HasAttribute("checked")));
        Assert.Equal(["Markdown", "Pdf", "Tex", "Tests", "TestZip", "ReferenceSolutions", "JurySolutions", "Participants", "ResultsAndMessages", "Tables"], fields.Select(f => f.GetAttribute("name")));
        Assert.All(fields, input => Assert.NotNull(page.QuerySelector($"label[for='{input.Id}']")));
        Assert.DoesNotContain(page.QuerySelectorAll("a"), a => a.GetAttribute("href")?.EndsWith("/problems/export", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(page.QuerySelectorAll("a"), a => a.GetAttribute("href")?.EndsWith("/statements/export", StringComparison.Ordinal) == true);
        Assert.Single(page.QuerySelectorAll("button"), b => b.TextContent == SiteResources.DownloadStatementPdf);
        using var shared = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestExportForm>(
            "/admin/backups", new() { ["DownloadUrl"] = "admin/contests/export" }, Staff(actor.Id)));
        Assert.Equal("admin/contests/export", shared.QuerySelector("form")!.GetAttribute("action"));
        Assert.Equal(10, shared.QuerySelectorAll("input[type=checkbox]").Length);
    }

    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task QueryFlagsControlActualDownloadsForOneAndAllContests(string role)
    {
        var token = TestContext.Current.CancellationToken;
        var actor = await _factory.AddUserAsync();
        await GrantRoleAsync(actor.Id, role);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Download choices", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        contest.Abbr = "download-" + Guid.NewGuid().ToString("N");
        contest.ProblemPdf = new Blob { Data = "%PDF-stored"u8.ToArray() };
        contest.FinalTableText = "<table>Final</table>";
        var problem = TestEntities.CreateProblem(contest, 7);
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        problem.Tests.Add(new Test { Problem = problem, Number = 1, IsActive = true, IsOpen = true, Input = "1"u8.ToArray(), Output = "2"u8.ToArray() });
        db.Problems.Add(problem);
        await db.SaveChangesAsync(token);
        using var client = await _factory.LoginAsync(actor.UserName!);
        string url = role == RoleNames.Admin ? "/admin/contests/export" : $"/jury/contests/{contest.Id}/export/download";
        using var minimal = await client.GetAsync(url, token);
        Assert.Equal(HttpStatusCode.OK, minimal.StatusCode);
        using var basic = new ZipArchive(new MemoryStream(await minimal.Content.ReadAsByteArrayAsync(token)));
        Assert.Null(basic.GetEntry($"{contest.Abbr}/problem07/tests/input001.txt"));
        Assert.Null(basic.GetEntry($"{contest.Abbr}/statements.pdf"));
        Assert.Null(basic.GetEntry($"{contest.Abbr}/tex/statements.tex"));
        Assert.Null(basic.GetEntry($"{contest.Abbr}/tables/final.html"));
        Assert.Null(basic.GetEntry($"{contest.Abbr}/problem07/statement.md"));
        using var selected = await client.GetAsync(url + "?Tests=true&Tex=true&Pdf=true&ResultsAndMessages=true&Tables=true&Markdown=true", token);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await selected.Content.ReadAsByteArrayAsync(token)));
        Assert.NotNull(archive.GetEntry($"{contest.Abbr}/tables/final.html"));
        Assert.NotNull(archive.GetEntry($"{contest.Abbr}/problem07/statement.md"));
        Assert.NotNull(archive.GetEntry($"{contest.Abbr}/problem07/tests/input001.txt"));
        Assert.NotNull(archive.GetEntry($"{contest.Abbr}/statements.pdf"));
        Assert.NotNull(archive.GetEntry($"{contest.Abbr}/tex/problem07.tex"));
        Assert.NotNull(archive.GetEntry($"{contest.Abbr}/results.jsonl"));
        Assert.NotNull(archive.GetEntry($"{contest.Abbr}/messages.jsonl"));
        Assert.Null(archive.GetEntry($"{contest.Abbr}/participants.jsonl"));
        Assert.DoesNotContain(archive.Entries, e => e.Name.EndsWith(".zip", StringComparison.Ordinal));
        using var invalid = await client.GetAsync(url + "?Tests=invalid", token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
