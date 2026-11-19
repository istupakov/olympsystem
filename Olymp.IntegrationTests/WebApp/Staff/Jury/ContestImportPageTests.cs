using System.Reflection;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ContestImportPageTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnchangedProblemShowsNoChangesDisablesSavingAndDoesNotRequireExitConfirmation()
    {
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, RoleNames.Admin);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT 1 FROM Checkers WHERE Id = 4) BEGIN SET IDENTITY_INSERT Checkers ON; INSERT INTO Checkers (Id, Description) VALUES (4, N'Strict'); SET IDENTITY_INSERT Checkers OFF; END", Token);
        var contest = TestEntities.CreateContest("Unchanged import", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var problem = TestEntities.CreateProblem(contest);
        problem.Checker = null!;
        problem.CheckerId = 4;
        problem.IsActive = false;
        problem.SlowTimeLimit = 1;
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestImport>(
            $"/jury/contests/{contest.Id}/import", new() { [nameof(ContestImport.ContestId)] = contest.Id }, Staff(user.Id), async page =>
            {
                byte[] zip = ProblemArchives.Zip(("A/problem.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id })));
                await ActAsync(page, "UploadAsync", new InputFileChangeEventArgs([new MemoryFile(zip)]));
                Assert.False(Preview(page).Rows[0].HasChanges);
                Assert.Empty(Preview(page).Rows[0].Errors);
                await ActAsync(page, "Finish");
                Assert.Null(Field(page, "_exitTarget"));
            }));
        Assert.Contains(SiteResources.ImportUnchanged, html.Body!.TextContent);
        var save = Assert.Single(html.QuerySelectorAll("button"), b => b.TextContent == SiteResources.ImportSaveProblem);
        Assert.True(save.HasAttribute("disabled"));
        Assert.Equal("Crlf", html.QuerySelector("#import-line-endings option[selected]")!.GetAttribute("value"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportUploadRequiresManagementAccess(bool admin)
    {
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, admin ? RoleNames.Admin : RoleNames.Jury);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Import", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        db.Contests.Add(contest);
        await db.SaveChangesAsync(Token);
        var html = await RenderAsync<ContestImport>($"/jury/contests/{contest.Id}/import", new() { [nameof(ContestImport.ContestId)] = contest.Id },
            Staff(user.Id, admin ? RoleNames.Admin : RoleNames.Jury));
        Assert.Equal(admin, html.Contains("id=\"import-zip\"", StringComparison.Ordinal));
        if (!admin) Assert.Contains(SiteResources.StaffAccessDenied, html);
    }

    [Fact]
    public async Task CancelledFilePickerPreservesSortedPreviewLimitsSelectionAndValidatorState()
    {
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, RoleNames.Admin);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Import", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        db.Contests.Add(contest);
        await db.SaveChangesAsync(Token);
        var html = await RenderAsync<ContestImport>($"/jury/contests/{contest.Id}/import", new() { [nameof(ContestImport.ContestId)] = contest.Id }, Staff(user.Id), async page =>
        {
            await UploadAsync(page);
            var preview = Preview(page);
            Assert.Equal([0, 1, 3], preview.Rows.Select(r => r.Parameters.Number));
            var first = preview.Rows[0];
            first.TimeLimit = 7; first.SlowTimeLimit = 11; first.Solutions[0].Selected = true;
            await SelectAsync(page, preview.Rows[1].Folder);
            await SelectAsync(page, first.Folder);
            await ActAsync(page, "UploadAsync", new InputFileChangeEventArgs([]));
            Assert.Same(preview, Preview(page));
            Assert.Null(Field(page, "_error"));
            await ActAsync(page, "RefreshAsync");
            Assert.Same(first, Field(page, "_selected"));
            Assert.Equal(7, first.TimeLimit); Assert.Equal(11, first.SlowTimeLimit);
            Assert.True(first.Solutions[0].Selected);
            await ActAsync(page, "Finish");
        });
        Assert.Contains(SiteResources.ImportLeaveConfirm, html);
        Assert.DoesNotContain(SiteResources.ImportFailed, html);
        Assert.False(await db.Problems.AnyAsync(p => p.ContestId == contest.Id, Token));
    }

    [Fact]
    public async Task ConfirmationSavesOnlyTheSelectedProblemAndSavingAgainRetainsItsId()
    {
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, RoleNames.Admin);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT 1 FROM Checkers WHERE Id = 4) BEGIN SET IDENTITY_INSERT Checkers ON; INSERT INTO Checkers (Id, Description) VALUES (4, N'Strict'); SET IDENTITY_INSERT Checkers OFF; END", Token);
        var contest = TestEntities.CreateContest("Import", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        db.Contests.Add(contest);
        await db.SaveChangesAsync(Token);
        await RenderAsync<ContestImport>($"/jury/contests/{contest.Id}/import", new() { [nameof(ContestImport.ContestId)] = contest.Id }, Staff(user.Id), async page =>
        {
            await UploadAsync(page);
            var preview = Preview(page);
            var row = preview.Rows[0];
            Assert.True(row.ValidatorChecked);
            await ActAsync(page, "ConfirmSaveAsync");
            Assert.True(Assert.IsType<bool>(Field(page, "_confirmSave")));
            Assert.False(await db.Problems.AnyAsync(p => p.ContestId == contest.Id, Token));
            await ActAsync(page, "SaveAsync");
            Assert.True(row.IsSaved);
            Assert.All(preview.Rows.Skip(1), r => Assert.False(r.IsSaved));
            int? id = row.Id;
            row.TimeLimit = 5;
            await ActAsync(page, "LimitsChanged");
            Assert.False(row.IsSaved);
            await ActAsync(page, "ConfirmSaveAsync");
            await ActAsync(page, "SaveAsync");
            Assert.Equal(id, row.Id);
            Assert.Equal(1, await db.Problems.CountAsync(p => p.ContestId == contest.Id, Token));
            Assert.Equal(5, await db.Problems.Where(p => p.Id == id).Select(p => p.TimeLimit).SingleAsync(Token));
        });
    }

    private static object? Field(ContestImport page, string name) => typeof(ContestImport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
    private static ContestImportPreview Preview(ContestImport page) => Assert.IsType<ContestImportPreview>(Field(page, "_preview"));
    private static async Task SelectAsync(ContestImport page, string folder)
    {
        typeof(ContestImport).GetProperty(nameof(ContestImport.TaskFolder))!.SetValue(page, folder);
        await ActAsync(page, "OnParametersSetAsync");
    }
    private static Task UploadAsync(ContestImport page) => ActAsync(page, "UploadAsync", new InputFileChangeEventArgs([new MemoryFile(ProblemArchives.Zip(
        ("B/problem.json", "{\"number\":3,\"name\":\"Last\",\"isActive\":false}"u8.ToArray()),
        ("A/problem.json", "{\"number\":1,\"name\":\"Middle\",\"isActive\":false}"u8.ToArray()),
        ("C/problem.json", "{\"number\":0,\"name\":\"First\",\"isActive\":false}"u8.ToArray()),
        ("C/solutions/a.cpp", "source"u8.ToArray())))]));

    private sealed class MemoryFile(byte[] data) : IBrowserFile
    {
        public string Name => "import.zip";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => data.Length;
        public string ContentType => "application/zip";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream(data);
    }
}
