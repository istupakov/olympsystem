using AngleSharp.Html.Parser;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class AccountStatusTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "/Account/AccessDenied", "Доступ запрещён", "У вас нет доступа к этому ресурсу.")]
    [InlineData("en", "/Account/AccessDenied", "Access denied", "You do not have access to this resource.")]
    [InlineData("ru", "/Account/Lockout", "Учётная запись заблокирована", "Эта учётная запись заблокирована. Попробуйте позже.")]
    [InlineData("en", "/Account/Lockout", "Locked out", "This account has been locked out, please try again later.")]
    [InlineData("ru", "/Account/InvalidUser", "Недействительный пользователь", "Пользователь не найден.")]
    [InlineData("en", "/Account/InvalidUser", "Invalid user", "User not found.")]
    public async Task AccountStatusPageIsLocalized(string culture, string path, string title, string message)
    {
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(title, document.Title);
        Assert.Equal(title, document.QuerySelector("h1, h3")!.TextContent);
        Assert.Equal(message, document.QuerySelector("p.text-danger, .alert-danger")!.TextContent.Trim());
        Assert.Null(document.QuerySelector(".alert-success"));
    }
}
