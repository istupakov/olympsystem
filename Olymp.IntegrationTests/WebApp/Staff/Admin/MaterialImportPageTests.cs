using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Admin;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

public class MaterialImportPageTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task UploadPreviewShowsEncodedTextAndDeletionAndRequiresConfirmationToSave()
    {
        var actor = await _factory.AddUserAsync();
        await GrantRoleAsync(actor.Id, RoleNames.Admin);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Import page", DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(-5).AddHours(5));
        contest.FinalTableText = "<table>Old</table>";
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var data = ProblemArchives.Zip(($"{contest.Abbr}/contest.json", JsonSerializer.SerializeToUtf8Bytes(new { id = contest.Id })),
            ($"{contest.Abbr}/description.html", "<p>New description</p>"u8.ToArray()), ($"{contest.Abbr}/tables/final.html", []));
        string preview = await RenderAsync<MaterialImport>("/admin/contests/import", [], Staff(actor.Id), async component =>
        {
            await ActAsync(component, "SelectFile", new InputFileChangeEventArgs([new ArchiveFile(data)]));
            await ActAsync(component, "PreviewAsync");
            await ActAsync(component, "SaveAsync");
            Assert.Null(await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.Description).SingleAsync(TestContext.Current.CancellationToken));
        });
        using var html = await new HtmlParser().ParseDocumentAsync(preview);
        Assert.Contains(SiteResources.MaterialDeleted, html.Body!.TextContent);
        Assert.Null(html.QuerySelector("pre p"));
        Assert.Contains("<p>New description</p>", html.Body.TextContent);
        Assert.NotNull(html.QuerySelector("input[aria-label]"));
        string saved = await RenderAsync<MaterialImport>("/admin/contests/import", [], Staff(actor.Id), async component =>
        {
            await ActAsync(component, "SelectFile", new InputFileChangeEventArgs([new ArchiveFile(data)]));
            await ActAsync(component, "PreviewAsync");
            typeof(MaterialImport).GetField("_confirm", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, true);
            await ActAsync(component, "SaveAsync");
        });
        Assert.Contains(SiteResources.StaffSaved, WebUtility.HtmlDecode(saved));
        Assert.Equal("<p>New description</p>", await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.Description).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Null(await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.FinalTableId).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task RouteRequiresAdministrator(string? role)
    {
        var actor = await _factory.AddUserAsync();
        if (role is not null) await GrantRoleAsync(actor.Id, role);
        using var client = await _factory.LoginAsync(actor.UserName!);
        using var response = await client.GetAsync("/admin/contests/import", TestContext.Current.CancellationToken);
        if (role == RoleNames.Admin) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        }
    }

    private sealed class ArchiveFile(byte[] data) : IBrowserFile
    {
        public string Name => "materials.zip";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => data.Length;
        public string ContentType => "application/zip";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
            => data.Length <= maxAllowedSize ? new MemoryStream(data) : throw new IOException();
    }
}
