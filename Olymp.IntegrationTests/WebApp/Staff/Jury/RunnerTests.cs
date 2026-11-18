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

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class RunnerTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Jury)]
    public async Task OnlyAnAdminCanPauseJudging(string role)
    {
        var author = await _factory.AddUserAsync();
        var manager = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.PageManager();
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryRunners>("/jury/runners", [], Staff(author.Id, role),
            page => ActAsync(page, "ToggleActiveAsync"), services =>
            {
                services.RemoveAll<ICheckerManager>();
                services.AddSingleton<ICheckerManager>(manager);
            }));
        Assert.Equal(role != RoleNames.Admin, manager.Active);
        Assert.Equal(role == RoleNames.Admin, html.QuerySelectorAll("button").Any(button => button.TextContent == SiteResources.StaffStartJudging));
        if (role == RoleNames.Jury) Assert.Contains(SiteResources.StaffAccessDenied, html.Body!.TextContent);
    }

    [Fact]
    public async Task RunnerRefreshRecoversFromInitialFailureAndRetainsSelectionAfterLaterFailure()
    {
        var author = await _factory.AddUserAsync();
        var contexts = new RefreshFailureContextFactory(_factory.Services.GetRequiredService<IDbContextFactory<OlympContext>>());
        var runner = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.TestRunner();
        var checker = await Olymp.IntegrationTests.Services.Checkers.CheckerTestData.CreateCheckerAsync(runner, "refresh-recovery");
        var manager = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.PageManager(checker);
        void Configure(IServiceCollection services)
        {
            services.RemoveAll<IDbContextFactory<OlympContext>>();
            services.AddSingleton<IDbContextFactory<OlympContext>>(contexts);
            services.RemoveAll<ICheckerManager>();
            services.AddSingleton<ICheckerManager>(manager);
        }
        string path = $"/jury/runners?Runner={runner.Id}";
        using var failed = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryRunners>(path, [], Staff(author.Id), configure: Configure));
        Assert.Contains(SiteResources.StaffRefreshFailed, failed.Body!.TextContent);
        Assert.DoesNotContain(SiteResources.StaffLoading, failed.Body.TextContent);
        using var recovered = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryRunners>(path, [], Staff(author.Id), async page =>
        {
            contexts.Fail = false;
            await ActAsync(page, "RefreshAsync");
            var selected = typeof(JuryRunners).GetProperty("SelectedRunner", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Same(checker, selected.GetValue(page));
            contexts.Fail = true;
            await ActAsync(page, "RefreshAsync");
            Assert.Same(checker, selected.GetValue(page));
            contexts.Fail = false;
            await ActAsync(page, "RefreshAsync");
        }, Configure));
        Assert.Contains(runner.Id.ToString(), recovered.Body!.TextContent);
        Assert.DoesNotContain(SiteResources.StaffRefreshFailed, recovered.Body.TextContent);
        Assert.DoesNotContain(recovered.QuerySelectorAll("button"), button => button.TextContent == SiteResources.Refresh);
    }

    [Fact]
    public async Task RunnerViewRetainsDiagnosticsAndLinksToTrialRun()
    {
        var author = await _factory.AddUserAsync();
        var runner = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.TestRunner { FailureTests = [1] };
        var checker = await Olymp.IntegrationTests.Services.Checkers.CheckerTestData.CreateCheckerAsync(runner, "compiler-details");
        await checker.RunSelfTests(TestContext.Current.CancellationToken);
        var manager = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.PageManager(checker);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryRunners>(
            $"/jury/runners?Runner={runner.Id}", [], Staff(author.Id, RoleNames.Jury), configure: services => { services.RemoveAll<ICheckerManager>(); services.AddSingleton<ICheckerManager>(manager); }));
        Assert.Contains(runner.Id.ToString(), html.Body!.TextContent);
        Assert.Contains("private compiler version", html.Body.TextContent);
        Assert.Contains("compiler-details", html.Body.TextContent);
        Assert.Contains(SiteResources.StaffSelfTestFailed, html.Body.TextContent);
        Assert.Contains(SiteResources.StaffExpected, html.Body.TextContent);
        Assert.Contains(SiteResources.StaffActual, html.Body.TextContent);
        Assert.DoesNotContain(html.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffAddCompiler);
        Assert.Contains(html.QuerySelectorAll("a"), link => link.GetAttribute("href")?.StartsWith("jury/trial-run?Runner=", StringComparison.Ordinal) == true);
        Assert.Null(html.QuerySelector("#run-source"));
    }

}
