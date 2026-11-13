using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ContestOperationsPageTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task SettingsShowOnlyApplicableActions(bool ended, bool archive, bool finished)
    {
        var token = TestContext.Current.CancellationToken;
        var actor = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest("Actions", now.AddDays(-2), ended ? now.AddDays(-1) : now.AddDays(1));
        contest.IsOpen = finished;
        contest.FreezeTime = finished ? null : now.AddDays(-1).AddHours(-1);
        if (archive) contest.TestZip = new Blob { Data = [1, 2] };
        var problem = TestEntities.CreateProblem(contest);
        db.Tests.Add(new Test { Problem = problem, Number = 1, IsActive = true, Input = [], Output = [] });
        await db.SaveChangesAsync(token);
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(
            $"/jury/contests/{contest.Id}/edit", new() { ["Id"] = contest.Id }, Staff(actor.Id)));
        var section = page.QuerySelector($"details[aria-label='{SiteResources.StaffContestOperations}']")!;
        Assert.Contains(section.QuerySelectorAll("a"), link => link.GetAttribute("href") == $"jury/contests/{contest.Id}/submissions?Retest=true");
        var buttons = section.QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Equal(ended, buttons.Contains(archive ? SiteResources.StaffRebuildTests : SiteResources.StaffPublishTests));
        Assert.Equal(archive, buttons.Contains(SiteResources.StaffRemoveTests));
        Assert.Equal(ended && !finished, buttons.Contains(SiteResources.StaffFinishContest));
        Assert.DoesNotContain(SiteResources.StaffConfirm, buttons);
    }
}
