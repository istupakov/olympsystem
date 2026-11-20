using System.Reflection;

using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;

using Xunit;

using JuryProblems = Olymp.WebApp.Components.Pages.Staff.Jury.Problems;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class ProblemWorkspaceTests
{
    [Fact]
    public async Task InvalidStatementWarningAppearsOnlyWhenExportIsRequested()
    {
        var token = TestContext.Current.CancellationToken;
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Export warning", Olymp.Statements.StatementFormat.Cutoff, Olymp.Statements.StatementFormat.Cutoff.AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(token);
        using var initial = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            $"/jury/contests/{contest.Id}/problems", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, Staff(author.Id)));
        Assert.DoesNotContain(SiteResources.StatementPrintWarning, initial.Body!.TextContent);
        using var requested = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            $"/jury/contests/{contest.Id}/problems", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, Staff(author.Id), page =>
            {
                typeof(JuryProblems).GetMethod("PreviewStatementExport", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
                return Task.CompletedTask;
            }));
        Assert.Contains(SiteResources.StatementPrintWarning, requested.Body!.TextContent);
        Assert.Contains(requested.QuerySelectorAll("a"), link => link.TextContent == SiteResources.StatementExportAnyway);
    }
}
