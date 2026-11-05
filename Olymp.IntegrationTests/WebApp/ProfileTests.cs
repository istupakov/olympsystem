using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class ProfileTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ProfileLocalizesFieldNamesAndAccountNavigation(string culture)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        bool russian = culture == "ru";
        string profile = russian ? "Профиль" : "Profile";

        Assert.Equal(profile, document.Title);
        Assert.Equal(profile, document.QuerySelector("h3")!.TextContent);
        Assert.Equal(russian ? "Управление учётной записью" : "Manage your account", document.QuerySelector("h1")!.TextContent);
        Assert.Equal(russian ? "Изменение настроек учётной записи" : "Change your account settings", document.QuerySelector("h2")!.TextContent);
        Assert.Equal(profile, document.QuerySelector("ul.nav a[href='account/manage']")!.TextContent);
        Assert.Equal(russian ? "Пароль" : "Password", document.QuerySelector("ul.nav a[href='account/manage/changepassword']")!.TextContent);
        Assert.Equal(russian ? "Логин" : "Username", document.QuerySelector("label[for='username']")!.TextContent);
        Assert.Equal(russian ? "Логин" : "Username", document.QuerySelector("input#username")!.GetAttribute("placeholder"));
        Assert.Equal(user.UserName, document.QuerySelector("input#username[disabled]")!.GetAttribute("value"));
        string[] fields = ["PublicName", "LastName", "FirstName", "Patronymic", "OrganizationId", "Group"];
        string[] labels = russian ? ["Публичное имя", "Фамилия", "Имя", "Отчество", "Вуз", "Группа"]
            : ["Public name", "Last name", "First name", "Patronymic", "University", "Group"];
        foreach (var (field, label) in fields.Zip(labels))
        {
            Assert.Equal(label, document.QuerySelector($"label[for='Input.{field}']")!.TextContent.Trim());
            if (field != "OrganizationId")
                Assert.Equal(label, document.QuerySelector($"input[name='Input.{field}']")!.GetAttribute("placeholder"));
        }
        Assert.Equal(russian ? "Сохранить" : "Save", document.QuerySelector("form:has(input[name='Input.PublicName']) button[type='submit']")!.TextContent);
    }

    [Theory]
    [InlineData("ru", "Профиль обновлён.")]
    [InlineData("en", "Your profile has been updated")]
    public async Task ProfileCanBeSavedAndOptionalFieldsCleared(string culture, string message)
    {
        var user = await factory.AddUserAsync();
        var organization = new Organization { Name = $"Test university {Guid.NewGuid():N}", Users = [] };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage", "profile", new()
        {
            ["Input.PublicName"] = "Updated user",
            ["Input.LastName"] = "Петров",
            ["Input.FirstName"] = "Иван",
            ["Input.Patronymic"] = "Сергеевич",
            ["Input.OrganizationId"] = organization.Id.ToString(),
            ["Input.Group"] = "АБ-12"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Manage", new Uri(client.BaseAddress!, response.Headers.Location!).AbsolutePath);
        using var page = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector(".alert-success")!.TextContent.Trim());
        Assert.Null(document.QuerySelector(".alert-danger"));
        var saved = await LoadUserAsync(user.Id);
        Assert.Equal("Updated user", saved.Name);
        Assert.Equal("Петров", saved.LastName);
        Assert.Equal("Иван", saved.FirstName);
        Assert.Equal("Сергеевич", saved.Patronymic);
        Assert.Equal(organization.Id, saved.OrganizationId);
        Assert.Equal("АБ-12", saved.Details);
        Assert.Equal(user.UserName, saved.UserName);
        Assert.Equal(user.Email, saved.Email);
        Assert.Equal(user.PasswordHash, saved.PasswordHash);

        using var clearedResponse = await WebAppFactory.PostFormAsync(client, "/Account/Manage", "profile",
            new() { ["Input.PublicName"] = "Updated user" });
        Assert.Equal(HttpStatusCode.Redirect, clearedResponse.StatusCode);
        var cleared = await LoadUserAsync(user.Id);
        Assert.Equal("Updated user", cleared.Name);
        Assert.Null(cleared.LastName);
        Assert.Null(cleared.FirstName);
        Assert.Null(cleared.Patronymic);
        Assert.Null(cleared.OrganizationId);
        Assert.Null(cleared.Details);
    }

    [Theory]
    [InlineData("ru", "Недопустимое значение.")]
    [InlineData("en", "Invalid value.")]
    public async Task UnknownUniversityShowsLocalizedErrorWithoutSavingProfile(string culture, string message)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage", "profile", new()
        {
            ["Input.PublicName"] = "Unsaved user",
            ["Input.FirstName"] = "Иван",
            ["Input.OrganizationId"] = int.MaxValue.ToString()
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, Assert.Single(document.QuerySelectorAll(".validation-message")).TextContent);
        var saved = await LoadUserAsync(user.Id);
        Assert.Equal(user.Name, saved.Name);
        Assert.Null(saved.FirstName);
        Assert.Null(saved.OrganizationId);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task ProfileValidationLocalizesNamesAndGroupRestrictions(string culture)
    {
        var user = await factory.AddUserAsync();
        using var client = await factory.LoginAsync(user.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        bool russian = culture == "ru";
        var cases = new[]
        {
            (Field: "PublicName", Value: "", Message: russian
                ? "Поле «Публичное имя» обязательно для заполнения." : "The Public name field is required."),
            (Field: "PublicName", Value: "bad_name", Message: russian
                ? "Поле «Публичное имя» может содержать только русские или латинские буквы, цифры, пробелы или дефисы."
                : "The Public name field may contain only Russian or Latin letters, digits, spaces or hyphens."),
            (Field: "FirstName", Value: "John", Message: russian
                ? "В поле «Имя» используйте не менее двух символов: русские буквы, пробелы или дефисы. Последний символ должен быть строчной буквой."
                : "Use at least two characters in First name: Russian letters, spaces or hyphens. The last character must be a lowercase letter."),
            (Field: "LastName", Value: "Я", Message: russian
                ? "В поле «Фамилия» используйте не менее двух символов: русские буквы, пробелы или дефисы. Последний символ должен быть строчной буквой."
                : "Use at least two characters in Last name: Russian letters, spaces or hyphens. The last character must be a lowercase letter."),
            (Field: "Patronymic", Value: "Иван1", Message: russian
                ? "В поле «Отчество» используйте не менее двух символов: русские буквы, пробелы или дефисы. Последний символ должен быть строчной буквой."
                : "Use at least two characters in Patronymic: Russian letters, spaces or hyphens. The last character must be a lowercase letter."),
            (Field: "FirstName", Value: new string('а', 65), Message: russian
                ? "Поле «Имя» должно содержать не более 64 символов." : "The field First name must be a string with a maximum length of 64."),
            (Field: "Group", Value: "group 1", Message: russian
                ? "Поле «Группа» может содержать только заглавные русские или латинские буквы, цифры или дефисы."
                : "The Group field may contain only uppercase Russian or Latin letters, digits or hyphens."),
            (Field: "Group", Value: new string('A', 17), Message: russian
                ? "Поле «Группа» должно содержать не более 16 символов." : "The field Group must be a string with a maximum length of 16.")
        };
        foreach (var (field, value, message) in cases)
        {
            var fields = new Dictionary<string, string> { ["Input.PublicName"] = "Updated user", [$"Input.{field}"] = value };
            using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage", "profile", fields);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Equal(message, Assert.Single(document.QuerySelectorAll(".validation-message")).TextContent);
        }
        Assert.Equal(user.Name, (await LoadUserAsync(user.Id)).Name);
    }

    [Theory]
    [InlineData("ru", "Не удалось обновить профиль.")]
    [InlineData("en", "Failed to update profile.")]
    public async Task FailedProfileUpdateKeepsLocalizedErrorStyle(string culture, string message)
    {
        var user = await factory.AddUserAsync();
        await using var site = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IUserValidator<User>>(new RejectProfileUpdateValidator())));
        using var client = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = user.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage", "profile",
            new() { ["Input.PublicName"] = "Unsaved user" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(message, document.QuerySelector(".alert-danger")!.TextContent.Trim());
        Assert.Null(document.QuerySelector(".alert-success"));
        Assert.Equal(user.Name, (await LoadUserAsync(user.Id)).Name);
    }

    private async Task<OlympUser> LoadUserAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OlympContext>().OlympUsers.AsNoTracking()
            .SingleAsync(user => user.Id == userId, TestContext.Current.CancellationToken);
    }

    private sealed class RejectProfileUpdateValidator : IUserValidator<User>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user) =>
            Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "RejectedForTest", Description = "Rejected for test." }));
    }
}
