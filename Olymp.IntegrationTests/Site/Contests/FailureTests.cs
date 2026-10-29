using System.Net;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class FailureTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Fact]
    public async Task ImportedParticipantsReceiveUploadedLoginsAndAdminCanViewTheirCards()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants",
            "ParticipantsFile", "Первый\nВторой"u8.ToArray(), "names.csv")).StatusCode);
        var free = "free" + Guid.NewGuid().ToString("N");
        int competitorId;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.True(await db.Competitors.Where(x => x.ContestId == contest.Id).AllAsync(x => x.UserName == null, cancellationToken: TestContext.Current.CancellationToken));
            competitorId = await db.Competitors.Where(x => x.ContestId == contest.Id).Select(x => x.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        var response = await SiteFixture.UploadAsync(client, path + "?handler=UploadLogins", "LoginsFile",
            Encoding.UTF8.GetBytes($"{free};12345678\n{free}b;23456789"), "logins.csv");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var admin = await site.AddUserAsync(RoleNames.Admin);
        using var adminClient = await site.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync($"/Users/Manage/{competitorId}", TestContext.Current.CancellationToken)).StatusCode);
    }

}
