using System.Net;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain.Models;
using Olymp.Site;

using Xunit;

namespace Olymp.IntegrationTests.Site.AntiCheat;

public class AuthorizationTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task ReportsRequireManagementAccessToSelectedContest(string role)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var jury = (await users.FindByNameAsync(RoleNames.Jury))!;
            Assert.True((await users.AddClaimAsync(jury,
                new Claim(ClaimNames.ContestManage, factory.Data.Contest.Id.ToString()))).Succeeded);
        }
        using var client = await factory.LoginAsync(role);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        using var page = await client.GetAsync("/AntiCheat", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var contestIds = document.QuerySelectorAll("select[name='Input.ContestId'] option")
            .Select(option => option.GetAttribute("value")).ToArray();
        Assert.Contains(factory.Data.Contest.Id.ToString(), contestIds);
        Assert.Equal(role == RoleNames.Admin, contestIds.Contains(factory.Data.OtherContest.Id.ToString()));
        Assert.NotNull(document.QuerySelector("nav a[href='/anticheat']"));
        Assert.Equal(role == RoleNames.Admin, document.QuerySelector("main a[href='/anticheat/bydate']") is not null);

        foreach (var action in new[] { "Plagiarism", "AIUsing" })
        {
            using var ownReport = await SiteFactory.PostFormAsync(client, "/AntiCheat?action=" + action, new()
            {
                ["Input.ContestId"] = factory.Data.Contest.Id.ToString(),
                ["Input.Threshold"] = "0.1"
            });
            ownReport.EnsureSuccessStatusCode();
            if (action == "AIUsing")
            {
                using var report = await new HtmlParser().ParseDocumentAsync(
                    await ownReport.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
                Assert.Contains(factory.Data.Competitor.Name,
                    Assert.Single(report.QuerySelectorAll("#ai-report .accordion-button")).TextContent);
            }
            using var otherReport = await SiteFactory.PostFormAsync(client, "/AntiCheat?action=" + action, new()
            {
                ["Input.ContestId"] = factory.Data.OtherContest.Id.ToString(),
                ["Input.Threshold"] = "0.1"
            });
            if (role == RoleNames.Admin)
                otherReport.EnsureSuccessStatusCode();
            else
                Assert.Contains("AccessDenied", otherReport.Headers.Location!.ToString());
        }
    }

    [Theory]
    [InlineData("/AntiCheat/ByDate")]
    [InlineData("/AntiCheat/ByUser")]
    [InlineData("/AntiCheat/PlagiarismTest")]
    [InlineData("/AntiCheat/AIDetectionTest")]
    public async Task GlobalAnalysisRemainsAdminOnlyIncludingDirectPosts(string path)
    {
        using var jury = await factory.LoginAsync(RoleNames.Jury);
        using var deniedGet = await jury.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("AccessDenied", deniedGet.Headers.Location!.ToString());
        using var deniedPost = await SiteFactory.PostFormAsync(jury, path, [], "/AntiCheat");
        Assert.Contains("AccessDenied", deniedPost.Headers.Location!.ToString());

        using var admin = await factory.LoginAsync(RoleNames.Admin);
        using var allowed = await admin.GetAsync(path, TestContext.Current.CancellationToken);
        allowed.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task JuryWithoutContestPermissionsGetsEmptySelectionAndCannotPostAnyContest()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var jury = (await users.FindByNameAsync(RoleNames.Jury))!;
            var claims = await users.GetClaimsAsync(jury);
            Assert.True((await users.RemoveClaimsAsync(jury,
                claims.Where(claim => claim.Type == ClaimNames.ContestManage))).Succeeded);
        }
        using var freshClient = await factory.LoginAsync(RoleNames.Jury);
        using var page = await freshClient.GetAsync("/AntiCheat", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Empty(document.QuerySelectorAll("select[name='Input.ContestId'] option[value]"));
        foreach (var action in new[] { "Plagiarism", "AIUsing" })
        {
            using var response = await SiteFactory.PostFormAsync(freshClient, "/AntiCheat?action=" + action, new()
            {
                ["Input.ContestId"] = factory.Data.Contest.Id.ToString(),
                ["Input.Threshold"] = "invalid"
            });
            Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        }
        using var member = await factory.LoginAsync("member");
        using var denied = await member.GetAsync("/AntiCheat", TestContext.Current.CancellationToken);
        Assert.Contains("AccessDenied", denied.Headers.Location!.ToString());
    }
}
