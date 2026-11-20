using System.Text;
using System.Text.Json;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Statements;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class ContestImportPageTests
{
    [Fact]
    public async Task PreviewUsesParticipantRenderingRetainedExamplesAndChangedLimitsAfterSelection()
    {
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, RoleNames.Admin);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Preview", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)), new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)).AddHours(5));
        var problem = TestEntities.CreateProblem(contest, 2);
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        problem.Tests.Add(new() { Problem = problem, Number = 1, IsOpen = true, IsActive = true, Input = "public\nexample"u8.ToArray(), Output = "YES"u8.ToArray() });
        problem.Tests.Add(new() { Problem = problem, Number = 2, IsOpen = false, IsActive = true, Input = "secret"u8.ToArray(), Output = [] });
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        byte[] zip = ProblemArchives.Zip(
            ("A/problem.json", "{\"number\":1,\"name\":\"First\"}"u8.ToArray()),
            ("B/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id, name = "JSON title" })),
            ("B/statement.md", Encoding.UTF8.GetBytes(problem.Text)));
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestImport>(
            $"/jury/contests/{contest.Id}/import", new() { ["ContestId"] = contest.Id }, Staff(user.Id), async page =>
            {
                await ActAsync(page, "UploadAsync", new InputFileChangeEventArgs([new MemoryFile(zip)]));
                await SelectAsync(page, "B");
                Preview(page).Rows.Single(r => r.Folder == "B").TimeLimit = 7;
                await ActAsync(page, "LimitsChanged");
            }));
        Assert.Contains("2. Двоичная строка", html.QuerySelector("#problem-detail h2")!.TextContent);
        Assert.Contains("JSON title", html.Body!.TextContent);
        Assert.Equal(StatementDocument.Parse(problem.Text).Html(), html.QuerySelector(".statement-markdown")!.InnerHtml);
        Assert.Contains("7", html.QuerySelector(".problem-statement li")!.TextContent);
        Assert.Single(html.QuerySelectorAll(".problem-examples tbody tr"));
        Assert.Contains("public\nexample", html.QuerySelector(".problem-examples")!.TextContent);
        Assert.DoesNotContain("secret", html.QuerySelector(".problem-statement")!.TextContent);
        Assert.Contains(SiteResources.ImportRetained, html.Body.TextContent);
        Assert.Contains(problem.Text, html.QuerySelector("details pre")?.TextContent ?? html.Body.TextContent);
    }
}
