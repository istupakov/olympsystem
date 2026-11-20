using System.Reflection;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ContestPdfUploadTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadPreservesSettingsAndRequiresConfirmationBeforeReplacement(bool hasPdf)
    {
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("PDF upload", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        byte[] original = "%PDF-1.4\noriginal"u8.ToArray(), uploaded = "%PDF-1.4\nuploaded"u8.ToArray();
        if (hasPdf) contest.ProblemPdf = new Blob { Data = original };
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        int? originalBlob = contest.ProblemPdfId;
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(
            $"/jury/contests/{contest.Id}/edit", new() { [nameof(EditContest.Id)] = contest.Id }, Staff(actor.Id), async component =>
            {
                var input = (EditContest.InputModel)typeof(EditContest).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
                input.Name = "Unsaved settings";
                await ActAsync(component, "SelectPdf", new InputFileChangeEventArgs([new MemoryFile(uploaded)]));
                await ActAsync(component, "UploadPdfAsync", false);
                if (hasPdf)
                {
                    Assert.True((bool)typeof(EditContest).GetField("_confirmPdf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!);
                    Assert.Equal(original, await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.ProblemPdf!.Data).SingleAsync(TestContext.Current.CancellationToken));
                    await ActAsync(component, "UploadPdfAsync", true);
                }
                Assert.Equal("Unsaved settings", input.Name);
            }));

        Assert.Contains(SiteResources.StaffPdfUploaded, page.Body!.TextContent);
        Assert.NotNull(page.QuerySelector("#contest-pdf"));
        Assert.Contains(page.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffReplacePdf);
        Assert.Equal("PDF upload", await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.Name).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(uploaded, await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.ProblemPdf!.Data).SingleAsync(TestContext.Current.CancellationToken));
        if (hasPdf) Assert.Equal(originalBlob, await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.ProblemPdfId).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("signature")]
    [InlineData("size")]
    public async Task InvalidFileLeavesExistingPdfIntact(string invalid)
    {
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        byte[] original = "%PDF-1.4\noriginal"u8.ToArray();
        var contest = TestEntities.CreateContest("Invalid PDF", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        contest.ProblemPdf = new Blob { Data = original };
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        byte[] bytes = invalid == "empty" ? [] : "not a PDF"u8.ToArray();
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(
            $"/jury/contests/{contest.Id}/edit", new() { [nameof(EditContest.Id)] = contest.Id }, Staff(actor.Id), async component =>
            {
                await ActAsync(component, "SelectPdf", new InputFileChangeEventArgs([new MemoryFile(bytes, invalid == "size" ? 21 * 1024 * 1024 : null)]));
                await ActAsync(component, "UploadPdfAsync", false);
                if (invalid == "signature") await ActAsync(component, "UploadPdfAsync", true);
            }));
        Assert.Contains(string.Format(SiteResources.StaffPdfInvalid, 20), page.Body!.TextContent);
        Assert.Equal(original, await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.ProblemPdf!.Data).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UploadRechecksRevokedManagementAccess()
    {
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Revoked PDF upload", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.UserClaims.Add(new() { UserId = actor.Id, ClaimType = ClaimNames.ContestManage, ClaimValue = contest.Id.ToString() });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(
            $"/jury/contests/{contest.Id}/edit", new() { [nameof(EditContest.Id)] = contest.Id }, Staff(actor.Id, RoleNames.Jury), async component =>
            {
                await db.UserClaims.Where(item => item.UserId == actor.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
                await ActAsync(component, "SelectPdf", new InputFileChangeEventArgs([new MemoryFile("%PDF-1.4\ntest"u8.ToArray())]));
                await ActAsync(component, "UploadPdfAsync", false);
            }));
        Assert.Contains(SiteResources.StaffAccessDenied, page.Body!.TextContent);
        Assert.Null(await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.ProblemPdfId).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRequiresConfirmationAndPreservesUnsavedContestSettings(bool confirm)
    {
        var token = TestContext.Current.CancellationToken;
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("PDF deletion", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        contest.ProblemPdf = new Blob { Data = "%PDF-1.4\noriginal"u8.ToArray() };
        db.Contests.Add(contest);
        await db.SaveChangesAsync(token);
        int pdfId = contest.ProblemPdfId!.Value;
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(
            $"/jury/contests/{contest.Id}/edit", new() { [nameof(EditContest.Id)] = contest.Id }, Staff(actor.Id), async component =>
            {
                var input = (EditContest.InputModel)typeof(EditContest).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
                input.Name = "Unsaved settings";
                await ActAsync(component, "RemovePdfAsync", false);
                Assert.NotNull(typeof(EditContest).GetField("_pdfRemoval", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component));
                Assert.True(await db.Blobs.AnyAsync(item => item.Id == pdfId, token));
                if (confirm) await ActAsync(component, "RemovePdfAsync", true);
                Assert.Equal("Unsaved settings", input.Name);
            }));
        Assert.Equal(!confirm, await db.Blobs.AnyAsync(item => item.Id == pdfId, token));
        Assert.Equal("PDF deletion", await db.Contests.Where(item => item.Id == contest.Id).Select(item => item.Name).SingleAsync(token));
        Assert.Contains(confirm ? SiteResources.StaffPdfRemoved : SiteResources.StaffRemovePdfConfirm, page.Body!.TextContent);
        if (confirm) Assert.DoesNotContain(page.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffRemovePdf);
    }

    private sealed class MemoryFile(byte[] data, long? declaredSize = null) : IBrowserFile
    {
        public string Name => "statements.pdf";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => declaredSize ?? data.Length;
        public string ContentType => "application/pdf";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream(data);
    }
}
