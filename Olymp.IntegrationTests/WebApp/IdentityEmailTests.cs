using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class IdentityEmailTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task RegistrationRejectsEmailOwnedByLegacyUsername()
    {
        var email = (await factory.AddUserAsync(legacyLogin: true)).Email!.ToUpperInvariant();
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Register", "register", new()
        {
            ["Input.PublicName"] = "New user",
            ["Input.Email"] = email,
            ["Input.Password"] = WebAppFactory.Password,
            ["Input.ConfirmPassword"] = WebAppFactory.Password,
            ["Input.AcceptRules"] = "true"
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
