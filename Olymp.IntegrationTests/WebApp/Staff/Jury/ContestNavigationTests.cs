using System.IO.Compression;
using System.Net;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class ContestNavigationTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task ArchiveSwitchesBothWaysOnTheSameComponent()
    {
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var now = DateTimeOffset.UtcNow;
        string marker = Guid.NewGuid().ToString("N");
        var current = TestEntities.CreateContest(marker + " current", now, now.AddHours(2));
        var archived = TestEntities.CreateContest(marker + " archive", now.AddMonths(-3), now.AddMonths(-2));
        db.Contests.AddRange(current, archived);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RenderAsync<ContestList>($"/jury/contests?Search={marker}", [], Staff(actor.Id), async component =>
        {
            Assert.Equal(current.Id, Assert.Single(await component.Query(db).ToListAsync(TestContext.Current.CancellationToken)).Id);
            await component.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = "archive" }));
            Assert.Equal(archived.Id, Assert.Single(await component.Query(db).ToListAsync(TestContext.Current.CancellationToken)).Id);
            await component.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = null }));
            Assert.Equal(current.Id, Assert.Single(await component.Query(db).ToListAsync(TestContext.Current.CancellationToken)).Id);
        }, services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new WorkspaceClock(now)); });
    }

    [Fact]
    public async Task ContestArchiveUsesCalendarMonthAndKeepsReadAccessWithoutManagement()
    {
        var staff = await _factory.AddUserAsync();
        var now = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
        var cutoff = now.AddMonths(-1);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        string marker = Guid.NewGuid().ToString("N");
        var archived = TestEntities.CreateContest(marker + " archived", cutoff.AddDays(-1), cutoff.AddTicks(-1));
        var boundary = TestEntities.CreateContest(marker + " boundary", cutoff.AddDays(-1), cutoff);
        archived.IsHidden = true;
        db.Contests.AddRange(archived, boundary);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        foreach (bool archive in new[] { true, false })
        {
            int[]? ids = null;
            await RenderAsync<ContestList>("/jury/contests" + (archive ? "/archive" : "") + $"?Search={marker}", new() { ["View"] = archive ? "archive" : null },
                Staff(staff.Id, RoleNames.Jury), async component => ids = await component.Query(db).Select(item => item.Id).ToArrayAsync(TestContext.Current.CancellationToken), services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new WorkspaceClock(now)); });
            Assert.Equal(new[] { archive ? archived.Id : boundary.Id }, ids);
        }
        using var details = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestDetails>(
            $"/jury/contests/{archived.Id}", new() { ["Id"] = archived.Id }, Staff(staff.Id, RoleNames.Jury)));
        Assert.Contains(archived.Name, details.Body!.TextContent);
        Assert.DoesNotContain(details.QuerySelectorAll("a.btn"), item => item.GetAttribute("href") == $"jury/contests/{archived.Id}/edit");
    }

}
