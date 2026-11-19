using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Compilators;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class ContestImportPageTests
{
    [Fact]
    public async Task ValidatorOnlyConfirmationListsRejectedStoredTestsWhileNormalImportRemainsBlocked()
    {
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, RoleNames.Admin);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT 1 FROM Checkers WHERE Id = 4) BEGIN SET IDENTITY_INSERT Checkers ON; INSERT INTO Checkers (Id, Description) VALUES (4, N'Strict'); SET IDENTITY_INSERT Checkers OFF; END", Token);
        var contest = TestEntities.CreateContest("Validator only", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var problem = TestEntities.CreateProblem(contest);
        problem.Checker = null!;
        problem.CheckerId = 4;
        problem.Tests.Add(new Test { Problem = problem, Number = 1, Input = "stored\rinput"u8.ToArray(), Output = [], IsActive = true, IsOpen = true });
        var info = new GccLinuxCpp23Configuration().ParseInfo("15.2.0") with { FullName = Guid.NewGuid().ToString("N") };
        var compiler = new Compilator
        {
            Name = info.Configuration.Name,
            Description = info.FullName,
            Language = "C++",
            SourceExtension = ".cpp",
            ConfigName = info.Configuration.ConfigName,
            CommandLine = info.Configuration.CompileCommand,
            IsActive = true,
            Submissions = []
        };
        db.Compilators.Add(compiler);
        var unavailable = TestEntities.CreateCompilator();
        unavailable.Language = "C++";
        unavailable.SourceExtension = ".cpp";
        db.Compilators.Add(unavailable);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        var manager = new CheckerManager(_factory.Services, scope.ServiceProvider.GetRequiredService<IDbContextFactory<OlympContext>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CheckerManager>.Instance);
        manager.Register(new WaitingImportChecker(info), Token);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestImport>(
            $"/jury/contests/{contest.Id}/import", new() { ["ContestId"] = contest.Id }, Staff(user.Id), async page =>
            {
                byte[] zip = ProblemArchives.Zip(("A/problem.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id })),
                    ("A/validator.cpp", "// validator"u8.ToArray()));
                await ActAsync(page, "UploadAsync", new InputFileChangeEventArgs([new MemoryFile(zip)]));
                Preview(page).ValidatorCompilerId = compiler.Id;
                await ActAsync(page, "ConfirmValidatorOnlyAsync");
                Assert.True(Assert.IsType<bool>(Field(page, "_confirmSave")));
                Assert.Equal([1], Assert.IsType<ValidatorImportCheck>(Field(page, "_validatorOnlyCheck")).RejectedTests);
            }, services => services.AddSingleton<ICheckerManager>(manager)));
        Assert.Contains(SiteResources.ImportConfirmValidatorOnly, html.Body!.TextContent);
        var options = html.QuerySelectorAll("#import-cpp option");
        Assert.Contains(options, option => option.GetAttribute("value") == compiler.Id.ToString());
        Assert.Contains(options, option => option.GetAttribute("value") == unavailable.Id.ToString());
        Assert.All(options.Where(option => option.GetAttribute("value") != ""), option =>
            Assert.True(int.TryParse(option.GetAttribute("value"), out _)));
        Assert.Contains(string.Format(SiteResources.ImportValidatorOnlyRejected, "1"), html.Body.TextContent);
        Assert.True(Assert.Single(html.QuerySelectorAll("button"), button => button.TextContent == SiteResources.ImportSaveProblem).HasAttribute("disabled"));
        Assert.False(await db.Submissions.AnyAsync(submission => submission.ProblemId == problem.Id, Token));
    }
}
