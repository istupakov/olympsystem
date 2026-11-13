using System.IO.Compression;
using System.Net;
using System.Text;

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
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class ParticipantExportTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task ParticipantCsvUsesFilteredSelectionSortAndWarnsAboutMissingLogins()
    {
        var staff = await _factory.AddUserAsync();
        await GrantRoleAsync(staff.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("CSV", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        string[] names = ["Alpha; \"quoted\"", "Zulu", "Missing", "Filtered"];
        var participants = names.Select(name => TestEntities.CreateCompetitor(contest, name)).ToArray();
        for (int index = 0; index < participants.Length; index++)
        {
            participants[index].IsApproved = index != 3;
            if (index != 2) { participants[index].UserName = $"csv-{Guid.NewGuid():N}"; participants[index].PasswordHash = "credential"; }
        }
        db.Competitors.AddRange(participants);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = await _factory.LoginAsync(staff.UserName!);
        using var response = await client.GetAsync($"/jury/contests/{contest.Id}/participants/export?approved=true&sort=Name&descending=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] data = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(data.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        string csv = Encoding.UTF8.GetString(data);
        Assert.Contains("\"Alpha; \"\"quoted\"\"\"", csv);
        Assert.True(csv.IndexOf("Zulu", StringComparison.Ordinal) < csv.IndexOf("Alpha", StringComparison.Ordinal));
        Assert.DoesNotContain("Missing", csv);
        Assert.DoesNotContain("Filtered", csv);
        Assert.Contains("credential", csv);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantList>(
            $"/jury/contests/{contest.Id}/participants?Approved=true", new() { ["ContestId"] = contest.Id }, Staff(staff.Id, RoleNames.Jury)));
        Assert.Contains(string.Format(SiteResources.StaffMissingLogins, 1), page.Body!.TextContent);
        Assert.DoesNotContain(page.QuerySelectorAll("a"), item => item.GetAttribute("href")?.StartsWith("admin/", StringComparison.Ordinal) == true);
    }

}
