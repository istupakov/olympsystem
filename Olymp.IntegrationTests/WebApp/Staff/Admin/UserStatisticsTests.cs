using System.Globalization;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Admin;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

[Trait("Category", "Integration")]
public class UserStatisticsTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CountsAndLinkedSelectionsRespectPeriodHashMigrationAndCleanupRules()
    {
        var token = TestContext.Current.CancellationToken;
        var accounts = new List<OlympUser>();
        for (int i = 0; i < 8; i++) accounts.Add(await _factory.AddUserAsync(legacyLogin: i is 1 or 3));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var stored = new List<OlympUser>();
        foreach (var account in accounts)
            stored.Add(await db.OlympUsers.SingleAsync(item => item.Id == account.Id, token));
        for (int i = 0; i < stored.Count; i++)
        {
            stored[i].Name = "Statistics sample";
            stored[i].RegistrationDate = i is 0 or 1 or 4 ? Now.AddMonths(-1) : Now.AddMonths(-2);
        }
        stored[1].PasswordHash = "hash|unused|salt";
        stored[2].PasswordHash = "hash|unused|salt";
        stored[4].PasswordHash = null;
        stored[5].EmailConfirmed = false;
        stored[5].LockoutEnabled = true;
        stored[5].LockoutEnd = Now.AddDays(1);
        stored[6].NormalizedEmail = null;
        stored[6].PasswordHash = "";
        stored[6].RegistrationDate = null;
        stored[7].RegistrationDate = Now.AddTicks(1);
        stored[7].LockoutEnabled = false;
        stored[7].LockoutEnd = Now.AddDays(1);
        var contest = TestEntities.CreateContest("Statistics", Now.AddDays(-1), Now.AddDays(1));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        var team = TestEntities.CreateCompetitor(contest, "Temporary account");
        team.RegistrationDate = Now.AddDays(-1);
        team.Members.Add(stored[3]);
        db.Submissions.AddRange(
            TestEntities.CreateSubmission(stored[1], problem, compiler, Now.AddMonths(-1)),
            TestEntities.CreateSubmission(stored[1], problem, compiler, Now),
            TestEntities.CreateSubmission(stored[2], problem, compiler, Now.AddDays(-1)),
            TestEntities.CreateSubmission(stored[0], problem, compiler, Now.AddMonths(-1).AddTicks(-1)),
            TestEntities.CreateSubmission(stored[4], problem, compiler, Now.AddTicks(1)),
            TestEntities.CreateSubmission(team, problem, compiler, Now));
        await db.SaveChangesAsync(token);

        var expected = new Dictionary<string, int[]>
        {
            ["all"] = [0, 1, 2, 3, 4, 5, 6, 7],
            ["new"] = [0, 1, 4],
            ["active"] = [1, 2],
            ["new-active"] = [1],
            ["confirmed"] = [0, 1, 2, 3, 4, 6, 7],
            ["unconfirmed"] = [5],
            ["blocked"] = [5],
            ["migration"] = [1, 3, 6],
            ["legacy"] = [1, 2],
            ["current-modern"] = [0, 5, 7],
            ["current-legacy"] = [2],
            ["migration-modern"] = [3],
            ["migration-legacy"] = [1],
            ["no-password"] = [4, 6],
            ["cleanup"] = [5]
        };
        var counts = await UserStatisticsQuery.CountAsync(db, Now.AddMonths(-1), Now, token);
        foreach (var (metric, indices) in expected)
        {
            var selection = new UserList { Metric = metric, Since = Now.AddMonths(-1).UtcDateTime, AsOf = Now.UtcDateTime };
            var ids = await selection.Query(db).Select(item => item.Id).ToArrayAsync(token);
            Assert.Equal(indices.Select(index => stored[index].Id).Order(), ids.Order());
            if (metric is "migration" or "legacy") Assert.False(counts.ContainsKey(metric));
            else Assert.Equal(ids.Length, counts[metric]);
        }
        Assert.Equal(counts["all"], counts["confirmed"] + counts["unconfirmed"]);
        Assert.Equal(expected["legacy"].Length, counts["current-legacy"] + counts["migration-legacy"]);
        Assert.Equal(counts["all"], counts["current-modern"] + counts["current-legacy"] + counts["migration-modern"] + counts["migration-legacy"] + counts["no-password"]);
        Assert.Empty(await new UserList { Metric = "unknown" }.Query(db).ToListAsync(token));
    }

    [Fact]
    public async Task StatisticsTabKeepsListFiltersAndMovesCleanupOutOfList()
    {
        string path = "/admin/users?Search=sample&Role=jury&row=12&Tab=statistics";
        using var statistics = await new HtmlParser().ParseDocumentAsync(await RenderAsync<UserList>(path, [],
            configure: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new WorkspaceClock(Now)); }));
        Assert.Null(statistics.QuerySelector("#user-search"));
        Assert.NotNull(statistics.QuerySelector("#user-cleanup-heading"));
        string listUrl = statistics.QuerySelector("nav[aria-label] .nav-underline a:not(.active)")!.GetAttribute("href")!;
        Assert.Contains("Search=sample", listUrl);
        Assert.Contains("Role=jury", listUrl);
        Assert.Contains("row=12", listUrl);
        Assert.DoesNotContain("Tab=", listUrl);
        Assert.NotEmpty(statistics.QuerySelectorAll("a[href*='Metric=']"));
        foreach (var link in statistics.QuerySelectorAll("a[href*='Metric=']"))
        {
            Assert.Contains("Since=", link.GetAttribute("href"));
            Assert.Contains("AsOf=", link.GetAttribute("href"));
            Assert.DoesNotContain("Search=", link.GetAttribute("href"));
        }
        using var list = await new HtmlParser().ParseDocumentAsync(await RenderAsync<UserList>(listUrl, []));
        Assert.NotNull(list.QuerySelector("#user-search"));
        Assert.Null(list.QuerySelector("#user-cleanup-heading"));
        Assert.DoesNotContain("Delete old unconfirmed registrations", list.Body!.TextContent);
    }

    [Fact]
    public async Task LinkedSelectionDisplaysTheSamePeriodWithoutApplyingServerOffsetTwice()
    {
        using var statistics = await new HtmlParser().ParseDocumentAsync(await RenderAsync<UserList>("/admin/users?Tab=statistics", [],
            configure: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new WorkspaceClock(Now)); }));
        string selectionUrl = statistics.QuerySelector("a[href*='Metric=new&']")!.GetAttribute("href")!;
        using var selection = await new HtmlParser().ParseDocumentAsync(await RenderAsync<UserList>(selectionUrl, []));
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, SiteResources.UserStatisticsFilterPeriod,
            Now.AddMonths(-1).ToLocalTime().ToString("g"), Now.ToLocalTime().ToString("g")), selection.Body!.TextContent);
    }

    [Fact]
    public async Task EmptyRegistrationPeriodShowsZeroPercentWithoutDivisionByZero()
    {
        using var document = await new HtmlParser().ParseDocumentAsync(await RenderAsync<UserList>("/admin/users?Tab=statistics", [],
            configure: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new WorkspaceClock(Now.AddYears(-100))); }));
        Assert.DoesNotContain("NaN", document.Body!.TextContent);
        Assert.DoesNotContain("Infinity", document.Body.TextContent);
        Assert.Contains(0d.ToString("P1", CultureInfo.CurrentCulture), document.Body.TextContent);
    }
}
