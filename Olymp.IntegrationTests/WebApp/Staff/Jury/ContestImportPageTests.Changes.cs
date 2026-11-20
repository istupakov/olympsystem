using System.Text;
using System.Text.Json;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class ContestImportPageTests
{
    [Fact]
    public async Task SaveConfirmationListsStatementTestAndParameterChanges()
    {
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, RoleNames.Admin);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT 1 FROM Checkers WHERE Id = 4) BEGIN SET IDENTITY_INSERT Checkers ON; INSERT INTO Checkers (Id, Description) VALUES (4, N'Strict'); SET IDENTITY_INSERT Checkers OFF; END", Token);
        var contest = TestEntities.CreateContest("Changes", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)), new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(7)).AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        problem.Checker = null!;
        problem.CheckerId = 4;
        problem.MemoryLimit = 256;
        problem.SlowTimeLimit = 1;
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        problem.Tests.Add(new() { Problem = problem, Number = 2, IsActive = true, Input = "old"u8.ToArray(), Output = [] });
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        string source = problem.Text.Replace("Двоичная строка", "Новая задача", StringComparison.Ordinal);
        byte[] zip = ProblemArchives.Zip(
            ("A/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id, timeLimit = 3, memoryLimit = 512 })),
            ("A/statement.md", Encoding.UTF8.GetBytes(source)),
            ("A/tests/input1.txt", "new"u8.ToArray()), ("A/tests/output1.txt", "answer"u8.ToArray()));
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestImport>(
            $"/jury/contests/{contest.Id}/import", new() { [nameof(ContestImport.ContestId)] = contest.Id }, Staff(user.Id), async page =>
            {
                await ActAsync(page, "UploadAsync", new InputFileChangeEventArgs([new MemoryFile(zip)]));
                await ActAsync(page, "ConfirmSaveAsync");
                Assert.True(Assert.IsType<bool>(Field(page, "_confirmSave")),
                    string.Join("; ", Preview(page).Rows[0].Errors) + Field(page, "_error"));
            }));
        var confirmation = Assert.Single(html.QuerySelectorAll("div.alert-warning"));
        string text = confirmation.TextContent;
        Assert.Contains(SiteResources.ImportStatementChanged, text);
        Assert.Contains(SiteResources.ImportAddedTests + " 1", text);
        Assert.Contains(SiteResources.ImportRemovedTests + " 2", text);
        Assert.Contains(SiteResources.TimeLimit + ": 1 → 3", text);
        Assert.Contains(SiteResources.MemoryLimit + ": 256 → 512", text);
    }
}
