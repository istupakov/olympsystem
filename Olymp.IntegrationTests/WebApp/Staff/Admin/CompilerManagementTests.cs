using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

using Xunit;

using JuryMessages = Olymp.WebApp.Components.Pages.Staff.Jury.Messages;
using JuryRunners = Olymp.WebApp.Components.Pages.Staff.Jury.Runners;
using JuryStanding = Olymp.WebApp.Components.Pages.Staff.Jury.Standing;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

[Trait("Category", "Integration")]
public partial class CompilerManagementTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddCompilerOmitsSavedConfigurationsButKeepsChangedCommand(bool changedCommand)
    {
        var staff = await _factory.AddUserAsync();
        var runner = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.TestRunner();
        var checker = await Olymp.IntegrationTests.Services.Checkers.CheckerTestData.CreateCheckerAsync(runner, $"saved-{Guid.NewGuid():N}");
        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));
        var key = CompilerConfigurationKey.From(Assert.Single(checker.Compilators!));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var compiler = TestEntities.CreateCompilator();
        compiler.Name = key.Name;
        compiler.Description = key.Description;
        compiler.Language = key.Language;
        compiler.SourceExtension = key.SourceExtension;
        compiler.ConfigName = key.Environment;
        compiler.CommandLine = changedCommand ? key.CommandLine + " changed" : key.CommandLine;
        compiler.IsActive = false;
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var manager = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.PageManager(checker);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Olymp.WebApp.Components.Pages.Staff.Jury.AddCompiler>(
            $"/jury/compilers/add?Runner={runner.Id}", [], Staff(staff.Id), configure: services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(manager); }));
        Assert.Equal(changedCommand, html.QuerySelector($"#add-configuration option[value='{key.Name}']") is not null);
        Assert.Equal(!changedCommand, html.Body!.TextContent.Contains(SiteResources.StaffAllConfigurationsAdded, StringComparison.Ordinal));

        foreach (bool available in new[] { false, true })
        {
            int[] ids = [];
            await RenderAsync<Olymp.WebApp.Components.Pages.Staff.Jury.CompilerList>(
                $"/jury/compilers?Search={key.Name}&RunnerAvailable={available}", [], Staff(staff.Id), async page => ids = await page.Query(db).Select(item => item.Id).ToArrayAsync(TestContext.Current.CancellationToken), services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(manager); });
            Assert.Equal(available != changedCommand, ids.Contains(compiler.Id));
        }
    }

    [Fact]
    public async Task CompilerAvailabilityRequiresPassedSelfTestsAndMatchingVersion()
    {
        var runner = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.TestRunner();
        var checker = await Olymp.IntegrationTests.Services.Checkers.CheckerTestData.CreateCheckerAsync(runner, "available-compiler");
        var compiler = TestEntities.CreateCompilator();
        compiler.Name = "available-compiler";
        compiler.Description = "Test compiler";
        compiler.Language = "C";
        compiler.SourceExtension = ".c";
        compiler.CommandLine = "compile";
        Assert.False(checker.CanCheck(compiler));
        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));
        Assert.True(checker.CanCheck(compiler));
        compiler.Description = "Another version";
        Assert.False(checker.CanCheck(compiler));
        compiler.Description = "Test compiler";
        compiler.Name = "another-compiler";
        Assert.False(checker.CanCheck(compiler));
    }

}
