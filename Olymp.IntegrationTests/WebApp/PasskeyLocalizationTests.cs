using System.Buffers.Text;
using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain.Models;
using Olymp.WebApp.Components;
using Olymp.WebApp.Components.Account;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class PasskeyLocalizationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task PasskeysStayDisabledByDefault(string culture)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Null(document.QuerySelector("a[href='account/manage/passkeys']"));
        Assert.Null(document.QuerySelector("script[src*='PasskeySubmit']"));
        using var denied = await client.GetAsync("/Account/Manage/Passkeys", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Equal("/Account/AccessDenied", new Uri(client.BaseAddress!, denied.Headers.Location!).AbsolutePath);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task PasskeyListAndBrowserMessagesAreLocalized(string culture)
    {
        var user = await factory.AddUserAsync();
        var data = new PasskeyTestData();
        data.Passkeys.Add(CreatePasskey());
        await using var site = CreatePasskeySite(data);
        using var client = await LoginAsync(site, user.UserName!, culture);
        using var response = await client.GetAsync("/Account/Manage/Passkeys", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        bool russian = culture == "ru";
        Assert.Equal(russian ? "Управление ключами доступа" : "Manage your passkeys", document.Title);
        Assert.Equal(document.Title, document.QuerySelector("h3")!.TextContent);
        Assert.Equal(russian ? "Безымянный ключ доступа" : "Unnamed passkey", document.QuerySelector("td div")!.TextContent);
        Assert.Equal(russian ? "Создан 5 нояб. 2026 (UTC)" : "Created Nov 5, 2026 (UTC)", document.QuerySelector("small")!.TextContent);
        Assert.Equal(russian ? "Переименовать" : "Rename", document.QuerySelector("button[value='rename']")!.TextContent);
        Assert.Equal(russian ? "Удалить" : "Delete", document.QuerySelector("button[value='delete']")!.TextContent);
        var component = document.QuerySelector("passkey-submit")!;
        Assert.Equal(russian ? "Некоторые функции ключей доступа недоступны. Обновите браузер."
            : "Some passkey features are missing. Please update your browser.", component.GetAttribute("unsupported-browser-message"));
        Assert.Equal(russian ? "Сервер вернул код состояния {0}." : "The server responded with status {0}.", component.GetAttribute("server-error-message"));
        Assert.Equal(russian ? "Неизвестная операция с ключом доступа «{0}»." : "Unknown passkey operation '{0}'.", component.GetAttribute("unknown-operation-message"));
        Assert.Equal(russian ? "Аутентификатор не предоставил ключ доступа." : "No passkey was provided by the authenticator.", component.GetAttribute("no-passkey-message"));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task MissingBrowserCredentialShowsLocalizedErrorAfterRedirect(string culture)
    {
        var user = await factory.AddUserAsync();
        await using var site = CreatePasskeySite(new());
        using var client = await LoginAsync(site, user.UserName!, culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Passkeys", "add-passkey", new());
        await AssertStatusAsync(client, response, culture == "ru" ? "Браузер не предоставил ключ доступа." : "The browser did not provide a passkey.", isError: true);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task RenameValidationAndSuccessAreLocalized(string culture)
    {
        var user = await factory.AddUserAsync();
        var data = new PasskeyTestData();
        var passkey = CreatePasskey();
        passkey.Name = "Personal key";
        data.Passkeys.Add(passkey);
        string path = $"/Account/Manage/RenamePasskey/{Base64Url.EncodeToString(passkey.CredentialId)}";
        await using var site = CreatePasskeySite(data);
        using var client = await LoginAsync(site, user.UserName!, culture);
        using var page = await client.GetAsync(path, TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        bool russian = culture == "ru";
        Assert.Equal(russian ? "Введите новое имя для ключа доступа «Personal key»" : "Enter a new name for your \"Personal key\" passkey", document.QuerySelector("h4")!.TextContent);
        Assert.Equal(russian ? "Имя ключа доступа" : "Passkey name", document.QuerySelector("label[for='Input.Name']")!.TextContent);
        Assert.Equal(russian ? "Мой ключ доступа" : "My passkey", document.QuerySelector("input[name='Input.Name']")!.GetAttribute("placeholder"));
        foreach (var (name, message) in new[]
        {
            ("", russian ? "Поле «Имя ключа доступа» обязательно для заполнения." : "The Name field is required."),
            (new string('A', 201), russian ? "Имя ключа доступа должно содержать не более 200 символов." : "Passkey names must be no longer than 200 characters.")
        })
        {
            using var invalid = await WebAppFactory.PostFormAsync(client, path, "rename-passkey", new() { ["Input.Name"] = name });
            Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
            using var invalidDocument = await new HtmlParser().ParseDocumentAsync(
                await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Equal(message, Assert.Single(invalidDocument.QuerySelectorAll(".validation-message")).TextContent);
            Assert.Equal("Personal key", passkey.Name);
        }
        using var response = await WebAppFactory.PostFormAsync(client, path, "rename-passkey", new() { ["Input.Name"] = "Updated key" });
        await AssertStatusAsync(client, response, russian ? "Ключ доступа успешно обновлён." : "Passkey updated successfully.", isError: false);
        Assert.Equal("Updated key", Assert.Single(data.Passkeys).Name);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task DeleteSuccessAndInvalidIdErrorAreLocalized(string culture)
    {
        var user = await factory.AddUserAsync();
        var data = new PasskeyTestData();
        var passkey = CreatePasskey();
        data.Passkeys.Add(passkey);
        await using var site = CreatePasskeySite(data);
        using var client = await LoginAsync(site, user.UserName!, culture);
        string id = Base64Url.EncodeToString(passkey.CredentialId);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Passkeys", $"update-passkey-{id}", new() { ["Action"] = "delete" });
        await AssertStatusAsync(client, response, culture == "ru" ? "Ключ доступа успешно удалён." : "Passkey deleted successfully.", isError: false);
        Assert.Empty(data.Passkeys);
        using var invalid = await client.GetAsync("/Account/Manage/RenamePasskey/!", TestContext.Current.CancellationToken);
        await AssertStatusAsync(client, invalid, culture == "ru" ? "Идентификатор указанного ключа доступа имеет неверный формат." : "The specified passkey ID had an invalid format.", isError: true);
    }

    private WebApplicationFactory<App> CreatePasskeySite(PasskeyTestData data) => factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.RemoveAll<IdentityFeatures>();
        services.AddSingleton(new IdentityFeatures { PasskeysEnabled = true });
        services.AddAuthorizationBuilder().AddPolicy(IdentityFeatures.PasskeysPolicy, policy => policy.RequireAssertion(_ => true));
        services.AddSingleton(data);
        services.AddScoped<IUserStore<User>, PasskeyTestStore>();
    }));

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<App> site, string username, string culture)
    {
        var client = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = username,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static UserPasskeyInfo CreatePasskey() => new([1, 2, 3], [4, 5, 6],
        new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), 0, [], true, false, false, [], []);

    private static async Task AssertStatusAsync(HttpClient client, HttpResponseMessage response, string message, bool isError)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var page = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector(isError ? ".alert-danger" : ".alert-success")!.TextContent.Trim());
        Assert.Null(document.QuerySelector(isError ? ".alert-success" : ".alert-danger"));
    }
}
