using System.Net;
using System.Reflection;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Icpc;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class IcpcImportPageTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task CoachControlsOrganizationOptionsAndBothAreRequiredBeforeConfirmation()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = await _factory.AddUserAsync();
        await GrantRoleAsync(admin.Id, RoleNames.Admin);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("ICPC import", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(5));
        contest.Abbr = "icpc-test";
        db.Contests.Add(contest);
        await db.SaveChangesAsync(token);
        var row = new IcpcImportRow
        {
            Source = new() { Id = 12345, Name = "Team", Status = "ACCEPTED" },
            Snapshot = "",
            Members = [],
            Coaches = [new(1, "One", "one@example.test", [new(10, "First")]), new(2, "Two", "two@example.test", [new(10, "First"), new(20, "Second")])],
            Errors = [],
            Warnings = [],
            Selected = true
        };
        string path = $"/jury/contests/{contest.Id}/participants/icpc";
        async Task<AngleSharp.Html.Dom.IHtmlDocument> PageAsync(bool confirm = false) =>
            await new HtmlParser().ParseDocumentAsync(await RenderAsync<IcpcImport>(path, new() { ["ContestId"] = contest.Id }, Staff(admin.Id), component =>
            {
                typeof(IcpcImport).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, new IcpcImportPreview([row]));
                if (confirm) typeof(IcpcImport).GetMethod("ConfirmImport", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
                return Task.CompletedTask;
            }, services => services.AddScoped(_ => new IcpcClient(new HttpClient(new EmptyCatalogHandler())))));

        using var missing = await PageAsync(true);
        Assert.NotNull(missing.QuerySelector("#icpc-org-12345[disabled]"));
        Assert.Equal(3, missing.QuerySelectorAll("#icpc-coach-12345 option").Length);
        Assert.Contains(SiteResources.IcpcCorrectFields, missing.Body!.TextContent);
        Assert.DoesNotContain(missing.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffConfirm);
        row.CoachId = 1;
        using var single = await PageAsync(true);
        Assert.Equal("10", single.QuerySelector("#icpc-org-12345 option[selected]")!.GetAttribute("value"));
        Assert.Equal(2, single.QuerySelectorAll("#icpc-org-12345 option").Length);
        Assert.Contains(single.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffConfirm);
        row.CoachId = null;
        row.CoachId = 2;
        using var multiple = await PageAsync(true);
        Assert.Contains("is-invalid", multiple.QuerySelector("#icpc-org-12345")!.ClassList);
        Assert.Equal(3, multiple.QuerySelectorAll("#icpc-org-12345 option").Length);
        Assert.DoesNotContain(multiple.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffConfirm);
        row.OrganizationId = 20;
        using var chosen = await PageAsync(true);
        Assert.Contains(chosen.QuerySelectorAll("button"), button => button.TextContent == SiteResources.StaffConfirm);
    }

    private sealed class EmptyCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
    }
}
