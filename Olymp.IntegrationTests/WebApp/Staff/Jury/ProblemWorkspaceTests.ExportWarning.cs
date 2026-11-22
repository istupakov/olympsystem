using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;

using Xunit;

using ExportPage = Olymp.WebApp.Components.Pages.Staff.Jury.ContestExport;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class ProblemWorkspaceTests
{
    [Fact]
    public async Task InvalidStatementWarningAppearsOnlyWhenPdfIsRequested()
    {
        var token = TestContext.Current.CancellationToken;
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Export warning", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)), new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)).AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(token);
        using var initial = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ExportPage>(
            $"/jury/contests/{contest.Id}/export", new() { [nameof(ExportPage.ContestId)] = contest.Id }, Staff(author.Id)));
        Assert.DoesNotContain(SiteResources.StatementPrintWarning, initial.Body!.TextContent);
        using var requested = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ExportPage>(
            $"/jury/contests/{contest.Id}/export", new() { [nameof(ExportPage.ContestId)] = contest.Id }, Staff(author.Id), page => ActAsync(page, "PreviewPdf")));
        Assert.Contains(SiteResources.StatementPrintWarning, requested.Body!.TextContent);
        var exportLink = Assert.Single(requested.QuerySelectorAll("a"), link => link.TextContent == SiteResources.StatementExportAnyway);
        Assert.Equal($"jury/contests/{contest.Id}/statements/pdf", exportLink.GetAttribute("href"));
    }
}
