using System.Net;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.WebApp;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Identity;

[Trait("Category", "Integration")]
public class ProfileCoachTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task CoachCanBeSelectedRetainedAfterRevocationAndRemoved()
    {
        var (user, coach, organization) = await SetupAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await SaveCoachAsync(client, coach.Id, organization.Id);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(coach.Id, await CoachIdAsync(user.Id));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.UserClaims.Where(item => item.UserId == coach.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
        using var page = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(coach.Id.ToString(), document.QuerySelector("select[name='Input.CoachId'] option[selected]")!.GetAttribute("value"));
        using var retained = await SaveCoachAsync(client, coach.Id, organization.Id);
        Assert.Equal(HttpStatusCode.Redirect, retained.StatusCode);
        Assert.Equal(coach.Id, await CoachIdAsync(user.Id));
        using var removed = await SaveCoachAsync(client, null, organization.Id);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Null(await CoachIdAsync(user.Id));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("no-claim")]
    [InlineData("self")]
    [InlineData("missing")]
    public async Task InvalidCoachIsRejectedAndProfileFieldsArePreserved(string reason)
    {
        var (user, coach, organization) = await SetupAsync();
        int candidate = coach.Id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var claim = await db.UserClaims.SingleAsync(item => item.UserId == coach.Id, TestContext.Current.CancellationToken);
            if (reason == "foreign") claim.ClaimValue = (organization.Id + 100000).ToString();
            if (reason == "no-claim") db.UserClaims.Remove(claim);
            if (reason == "self") candidate = user.Id;
            if (reason == "missing") candidate = int.MaxValue;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await SaveCoachAsync(client, candidate, organization.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.NotNull(document.QuerySelector(".validation-message"));
        Assert.Equal(user.Name, document.QuerySelector("input[name='Input.PublicName']")!.GetAttribute("value"));
        Assert.Null(await CoachIdAsync(user.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileSaveRetainsCoachUnlessUniversityChanges(bool changeUniversity)
    {
        var (user, coach, organization) = await SetupAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        using var selection = await SaveCoachAsync(client, coach.Id, organization.Id);
        Assert.Equal(HttpStatusCode.Redirect, selection.StatusCode);
        using var profile = await WebAppFactory.PostFormAsync(client, "/Account/Manage", "profile", new()
        {
            ["Input.PublicName"] = user.Name,
            ["Input.OrganizationId"] = changeUniversity ? "" : organization.Id.ToString(),
            ["Input.CoachId"] = coach.Id.ToString()
        });
        Assert.Equal(HttpStatusCode.Redirect, profile.StatusCode);
        Assert.Equal(changeUniversity ? null : (int?)coach.Id, await CoachIdAsync(user.Id));
    }

    [Theory]
    [InlineData("ru", "Тренер", "Без тренера")]
    [InlineData("en", "Coach", "No coach")]
    public async Task CoachListUsesOrganizationClaimsAndLocalizesFields(string culture, string label, string empty)
    {
        var (user, coach, _) = await SetupAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(label, document.QuerySelector("label[for='Input.CoachId']")!.TextContent);
        var options = document.QuerySelectorAll("select[name='Input.CoachId'] option");
        Assert.Equal(2, options.Length);
        Assert.Equal(empty, options[0].TextContent);
        Assert.Equal(coach.Id.ToString(), options[1].GetAttribute("value"));
        Assert.NotNull(document.QuerySelector("a[href='account/manage/merge']"));
    }

    private async Task<(OlympUser User, OlympUser Coach, Organization Organization)> SetupAsync()
    {
        var user = await factory.AddUserAsync();
        var coach = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var organization = new Organization { Name = $"Coach university {Guid.NewGuid():N}", Users = [] };
        db.Organizations.Add(organization);
        var storedUser = await db.OlympUsers.SingleAsync(item => item.Id == user.Id, TestContext.Current.CancellationToken);
        storedUser.Organization = organization;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var storedCoach = (await users.FindByIdAsync(coach.Id.ToString()))!;
        Assert.True((await users.AddClaimAsync(storedCoach, new Claim(ClaimNames.CoachOrg, organization.Id.ToString()))).Succeeded);
        return (user, coach, organization);
    }

    private async Task<int?> CoachIdAsync(int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OlympContext>().OlympUsers
            .Where(item => item.Id == userId).Select(item => item.CoachId).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> SaveCoachAsync(HttpClient client, int? coachId, int organizationId) =>
        WebAppFactory.PostFormAsync(client, "/Account/Manage", "profile", new()
        {
            ["Input.PublicName"] = "Test user",
            ["Input.OrganizationId"] = organizationId.ToString(),
            ["Input.CoachId"] = coachId?.ToString() ?? ""
        });
}
