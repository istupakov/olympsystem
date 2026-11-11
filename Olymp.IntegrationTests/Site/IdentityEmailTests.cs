using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;

using Xunit;

namespace Olymp.IntegrationTests.Site;

[Trait("Category", "Integration")]
public class IdentityEmailTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Fact]
    public async Task RegistrationRejectsEmailOwnedByLegacyUsername()
    {
        var email = factory.Data.Member.Email!.ToUpperInvariant();
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        using var response = await SiteFactory.PostFormAsync(client, "/Identity/Account/Register", new()
        {
            ["Input.PublicName"] = "New user",
            ["Input.Email"] = email,
            ["Input.Password"] = SiteData.Password,
            ["Input.ConfirmPassword"] = SiteData.Password
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Contains($"Email '{email}' is already taken.", document.Body!.TextContent);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OlympContext>().OlympUsers
            .CountAsync(x => x.NormalizedEmail == email, TestContext.Current.CancellationToken));
    }
}
