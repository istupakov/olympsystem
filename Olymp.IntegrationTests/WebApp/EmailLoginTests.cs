using System.Net;
using System.Security.Claims;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class EmailLoginTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task AnonymousVisitorIsRedirectedToLogin()
    {
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/Login", RedirectTarget(response).AbsolutePath);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("email")]
    [InlineData("uppercase-email")]
    public async Task LegacyLoginRedirectsToEmailSettingsWithReturnUrl(string loginKind)
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = factory.CreateSiteClient();
        using var response = await WebAppFactory.PostFormAsync(client,
            "/Account/Login?ReturnUrl=%2Ftest%2Fauth%3Ftest%3D1", "login", new()
            {
                ["Input.Login"] = loginKind switch
                {
                    "email" => user.Email!,
                    "uppercase-email" => user.Email!.ToUpperInvariant(),
                    _ => user.UserName!
                },
                ["Input.Password"] = WebAppFactory.Password
            });

        var target = RedirectTarget(response);
        Assert.Equal("/Account/Manage/Email", target.AbsolutePath);
        Assert.Equal("/test/auth?test=1", QueryHelpers.ParseQuery(target.Query)["returnUrl"]);
        using var protectedPage = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/Manage/Email", RedirectTarget(protectedPage).AbsolutePath);
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(user.UserName, saved!.UserName);
        Assert.Equal(user.Email, saved.Email);
        Assert.Equal(user.PasswordHash, saved.PasswordHash);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Account/Manage")]
    [InlineData("/Account/Manage/Email")]
    [InlineData("/Account/Manage/ChangePassword")]
    public async Task LegacyUserCanOpenPublicAndAccountPages(string path)
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("account/logout", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LegacyUserCannotOpenProtectedPage()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await client.GetAsync("/test/auth?test=1", TestContext.Current.CancellationToken);

        var target = RedirectTarget(response);
        Assert.Equal("/Account/Manage/Email", target.AbsolutePath);
        Assert.Equal("/test/auth?test=1", QueryHelpers.ParseQuery(target.Query)["returnUrl"]);
    }

    [Fact]
    public async Task LegacyUserCanSaveProfileBeforeMigration()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage", "profile", new()
        {
            ["Input.PublicName"] = "Updated user"
        });

        Assert.Equal("/Account/Manage", RedirectTarget(response).AbsolutePath);
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().OlympUsers
            .SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Updated user", saved.Name);
        Assert.Equal(user.UserName, saved.UserName);
    }

    [Fact]
    public async Task ConfirmedEmailBecomesLoginWithoutLetterOrPasswordChange()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = await factory.LoginAsync(user.UserName!);
        int lettersBefore = factory.EmailSender.Confirmations.Count;
        using var response = await WebAppFactory.PostFormAsync(client,
            "/Account/Manage/Email?returnUrl=%2Ftest%2Fauth%3Ftest%3D1", "use-email-login", []);

        Assert.Equal("/test/auth?test=1", RedirectTarget(response).PathAndQuery);
        Assert.Equal(lettersBefore, factory.EmailSender.Confirmations.Count);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var saved = await users.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(saved);
            Assert.Equal(user.Email, saved.Email);
            Assert.Equal(user.Email, saved.UserName);
            Assert.Equal(user.PasswordHash, saved.PasswordHash);
            Assert.Null(await users.FindByNameAsync(user.UserName!));
        }

        using var authorized = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        string html = await authorized.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(user.Email!, html);
        Assert.DoesNotContain(user.UserName!, html);

        using var newLogin = await factory.LoginAsync(user.Email!);
        using var newLoginPage = await newLogin.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, newLoginPage.StatusCode);
    }

    [Fact]
    public async Task OutdatedEmailCanBeChangedBeforeMigration()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        string newEmail = $"{Guid.NewGuid():N}@example.test";
        using var client = await factory.LoginAsync(user.UserName!);
        using var request = await WebAppFactory.PostFormAsync(client,
            "/Account/Manage/Email?returnUrl=%2Ftest%2Fauth%3Ftest%3D1", "change-email", new()
            {
                ["Input.NewEmail"] = newEmail
            });

        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var (_, confirmationLink) = Assert.Single(factory.EmailSender.Confirmations, letter => letter.Email == newEmail);
        using (var restricted = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken))
            Assert.Equal("/Account/Manage/Email", RedirectTarget(restricted).AbsolutePath);
        using (var scope = factory.Services.CreateScope())
        {
            var beforeConfirmation = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
            Assert.Equal(user.Email, beforeConfirmation!.Email);
            Assert.Equal(user.UserName, beforeConfirmation.UserName);
        }

        using var confirmed = await client.GetAsync(WebUtility.HtmlDecode(confirmationLink), TestContext.Current.CancellationToken);
        Assert.Equal("/test/auth?test=1", RedirectTarget(confirmed).PathAndQuery);
        using var authorized = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        using var final = factory.Services.CreateScope();
        var saved = await final.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(newEmail, saved!.Email);
        Assert.Equal(newEmail, saved.UserName);
        Assert.True(saved.EmailConfirmed);
        Assert.Equal(user.PasswordHash, saved.PasswordHash);
    }

    [Fact]
    public async Task UsernameConflictLeavesAccountUnchangedAndRestricted()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        var other = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var owner = await users.FindByIdAsync(other.Id.ToString());
            Assert.True((await users.SetUserNameAsync(owner!, user.Email)).Succeeded);
        }
        using var client = await factory.LoginAsync(user.UserName!);
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Email", "use-email-login", []);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alert-danger", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var restricted = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/Manage/Email", RedirectTarget(restricted).AbsolutePath);
        using var final = factory.Services.CreateScope();
        var saved = await final.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(user.UserName, saved!.UserName);
        Assert.Equal(user.Email, saved.Email);
        Assert.Equal(user.PasswordHash, saved.PasswordHash);
    }

    [Fact]
    public async Task EmailLoginComparisonIgnoresCase()
    {
        var user = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var saved = await users.FindByIdAsync(user.Id.ToString());
            Assert.True((await users.SetUserNameAsync(saved!, user.Email!.ToUpperInvariant())).Succeeded);
        }
        using var client = await factory.LoginAsync(user.Email!);
        using var response = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TemporaryCompetitorDoesNotNeedEmailLogin()
    {
        var contest = TestEntities.CreateContest("WebApp test", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        var competitor = TestEntities.CreateCompetitor(contest, $"team_{Guid.NewGuid():N}");
        competitor.UserName = competitor.Name;
        competitor.IsApproved = true;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.CreateAsync(competitor, WebAppFactory.Password)).Succeeded);
            Assert.True((await users.AddClaimAsync(competitor, new Claim(ClaimNames.CompetitorContest, competitor.ContestId.ToString()))).Succeeded);
        }
        using var client = await factory.LoginAsync(competitor.UserName);
        using var response = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var profile = await client.GetAsync("/Account/Manage/Email", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/AccessDenied", RedirectTarget(profile).AbsolutePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedAccountStillCannotSignIn(bool useEmail)
    {
        var user = await factory.AddUserAsync(legacyLogin: true, emailConfirmed: false);
        using var client = factory.CreateSiteClient();
        using var response = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = useEmail ? user.Email! : user.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var protectedPage = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/Login", RedirectTarget(protectedPage).AbsolutePath);
    }

    [Fact]
    public async Task EmailAndLegacyLoginSharePasswordFailureCounterAndLockout()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = factory.CreateSiteClient();
        int attempts;
        using (var scope = factory.Services.CreateScope())
            attempts = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout.MaxFailedAccessAttempts;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            using var response = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
            {
                ["Input.Login"] = attempt % 2 == 1 ? user.Email! : user.UserName!,
                ["Input.Password"] = "WrongPassword123"
            });
            if (attempt == attempts)
                Assert.Equal("/Account/Lockout", RedirectTarget(response).AbsolutePath);
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var scope = factory.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
                var saved = await users.FindByIdAsync(user.Id.ToString());
                Assert.Equal(attempt, await users.GetAccessFailedCountAsync(saved!));
            }
        }
        foreach (string login in new[] { user.UserName!, user.Email! })
        {
            using var response = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
            {
                ["Input.Login"] = login,
                ["Input.Password"] = WebAppFactory.Password
            });
            Assert.Equal("/Account/Lockout", RedirectTarget(response).AbsolutePath);
        }
    }

    [Fact]
    public async Task ExistingUsernameTakesPrecedenceOverAnotherAccountsEmail()
    {
        var emailOwner = await factory.AddUserAsync(legacyLogin: true);
        var usernameOwner = await factory.AddUserAsync();
        const string otherPassword = "OtherPassword456";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var saved = await users.FindByIdAsync(usernameOwner.Id.ToString());
            Assert.True((await users.SetUserNameAsync(saved!, emailOwner.Email)).Succeeded);
            Assert.True((await users.ChangePasswordAsync(saved!, WebAppFactory.Password, otherPassword)).Succeeded);
        }
        using var client = factory.CreateSiteClient();
        using var rejected = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = emailOwner.Email!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        using var anonymous = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/Login", RedirectTarget(anonymous).AbsolutePath);

        using var accepted = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = emailOwner.Email!,
            ["Input.Password"] = otherPassword
        });
        Assert.Equal("/Account/Manage/Email", RedirectTarget(accepted).AbsolutePath);
        using var page = await client.GetAsync("/Account/Manage/Email", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(usernameOwner.Email, document.QuerySelector("input#email[disabled]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task CrossSiteMigrationPostIsRejected()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = await factory.LoginAsync(user.UserName!);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Manage/Email")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "use-email-login" })
        };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Origin", "https://other.example.test");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(user.UserName, saved!.UserName);
    }

    [Fact]
    public async Task LogoutRemainsAvailableBeforeMigration()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        using var client = await factory.LoginAsync(user.UserName!);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "" });
        using var response = await client.PostAsync("/Account/Logout", content, TestContext.Current.CancellationToken);

        Assert.Equal("/", RedirectTarget(response).AbsolutePath);
        using var protectedPage = await client.GetAsync("/test/auth", TestContext.Current.CancellationToken);
        Assert.Equal("/Account/Login", RedirectTarget(protectedPage).AbsolutePath);
    }

    [Theory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task InvalidEmailChangeLinkLeavesAddressAndLoginUnchanged(string culture, bool malformed)
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        string code = malformed ? "!" : WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("invalid-token"));
        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync(QueryHelpers.AddQueryString("/Account/ConfirmEmailChange",
            new Dictionary<string, string?> { ["userId"] = user.Id.ToString(), ["email"] = "new@example.test", ["code"] = code }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Не удалось изменить email." : "Error changing email.",
            document.QuerySelector(".alert-danger")!.TextContent.Trim());
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(user.Email, saved!.Email);
        Assert.Equal(user.UserName, saved.UserName);
    }

    [Fact]
    public async Task ExpiredEmailChangeLinkLeavesAddressAndLoginUnchanged()
    {
        var user = await factory.AddUserAsync(legacyLogin: true);
        string newEmail = $"{Guid.NewGuid():N}@example.test";
        await using var site = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromMinutes(-1))));
        using var client = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using (var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = user.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        }))
            Assert.Equal("/Account/Manage/Email", RedirectTarget(login).AbsolutePath);
        using var request = await WebAppFactory.PostFormAsync(client, "/Account/Manage/Email", "change-email",
            new() { ["Input.NewEmail"] = newEmail });
        request.EnsureSuccessStatusCode();
        var (_, link) = Assert.Single(factory.EmailSender.Confirmations, letter => letter.Email == newEmail);
        using var response = await client.GetAsync(WebUtility.HtmlDecode(link), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alert-danger", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(user.Id.ToString());
        Assert.Equal(user.Email, saved!.Email);
        Assert.Equal(user.UserName, saved.UserName);
    }

    private static Uri RedirectTarget(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var target = new Uri(new Uri("https://localhost"), response.Headers.Location);
        Assert.Equal("localhost", target.Host);
        return target;
    }
}
