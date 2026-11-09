using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Admin;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class StaffOrganizationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task AdminCreatesEditsAndDeletesUnusedOrganizationAfterConfirmation()
    {
        string name = Guid.NewGuid().ToString("N");
        await WithEditorAsync(null, RoleNames.Admin, async (editor, _) =>
        {
            SetInput(editor, "Name", " " + name + " ");
            SetInput(editor, "FullName", " Full organization ");
            SetInput(editor, "ACMName", " Prefix ");
            await InvokeAsync(editor, "SaveAsync");
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var created = await db.Organizations.AsNoTracking().SingleAsync(item => item.Name == name, TestContext.Current.CancellationToken);
        Assert.Equal("Full organization", created.FullName);
        Assert.Equal("Prefix", created.ACMName);
        await WithEditorAsync(created.Id, RoleNames.Admin, async (editor, _) =>
        {
            SetInput(editor, "Name", name + " updated");
            SetInput(editor, "FullName", " ");
            SetInput(editor, "ACMName", "");
            await InvokeAsync(editor, "SaveAsync");
        });
        var updated = await db.Organizations.AsNoTracking().SingleAsync(item => item.Id == created.Id, TestContext.Current.CancellationToken);
        Assert.Equal(name + " updated", updated.Name);
        Assert.Null(updated.FullName);
        Assert.Null(updated.ACMName);
        await WithEditorAsync(created.Id, RoleNames.Admin, async (editor, _) =>
        {
            await InvokeAsync(editor, "DeleteAsync");
            Assert.True(await db.Organizations.AnyAsync(item => item.Id == created.Id, TestContext.Current.CancellationToken));
            SetField(editor, "_confirmDelete", true);
            await InvokeAsync(editor, "DeleteAsync");
        });
        Assert.False(await db.Organizations.AnyAsync(item => item.Id == created.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("user", false)]
    [InlineData("participant", false)]
    [InlineData("coach", false)]
    [InlineData("user", true)]
    [InlineData("coach", true)]
    public async Task DeletionRetainsOrganizationsReferencedBeforeOrAfterLoadingEditor(string kind, bool late)
    {
        var user = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var organization = new Organization { Name = Guid.NewGuid().ToString("N"), Users = [] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        async Task AddReference()
        {
            if (kind == "coach")
                db.UserClaims.Add(new IdentityUserClaim<int> { UserId = user.Id, ClaimType = ClaimNames.CoachOrg, ClaimValue = organization.Id.ToString() });
            else if (kind == "participant")
            {
                var contest = TestEntities.CreateContest("Organization", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
                var participant = TestEntities.CreateCompetitor(contest, "Participant");
                participant.OrganizationId = organization.Id;
                db.Competitors.Add(participant);
            }
            else
                await db.Users.Where(item => item.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.OrganizationId, organization.Id), TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        if (!late) await AddReference();
        await WithEditorAsync(organization.Id, RoleNames.Admin, async (editor, html) =>
        {
            if (late) await AddReference();
            SetField(editor, "_confirmDelete", true);
            await InvokeAsync(editor, "DeleteAsync");
            using var document = await new HtmlParser().ParseDocumentAsync(html());
            Assert.Contains(string.Format(SiteResources.StaffOrganizationInUse, kind == "coach" ? 0 : 1, kind == "coach" ? 1 : 0), document.DocumentElement.TextContent);
        });
        Assert.True(await db.Organizations.AnyAsync(item => item.Id == organization.Id, TestContext.Current.CancellationToken));
        if (kind == "coach") Assert.True(await db.UserClaims.AnyAsync(item => item.UserId == user.Id && item.ClaimType == ClaimNames.CoachOrg, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoleNames.Jury)]
    public async Task MutationsRequireAdministratorEvenWhenHandlerIsInvokedDirectly(string? role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var organization = new Organization { Name = Guid.NewGuid().ToString("N"), Users = [] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await WithEditorAsync(organization.Id, role, async (editor, _) =>
        {
            SetInput(editor, "Name", "Unexpected edit");
            await InvokeAsync(editor, "SaveAsync");
            SetField(editor, "_confirmDelete", true);
            await InvokeAsync(editor, "DeleteAsync");
        });
        Assert.Equal(organization.Name, await db.Organizations.Where(item => item.Id == organization.Id).Select(item => item.Name).SingleAsync(TestContext.Current.CancellationToken));
    }

    private async Task WithEditorAsync(int? id, string? role, Func<EditOrganization, Func<string>, Task> action)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(role is null ? [] : [new Claim(ClaimTypes.Role, role)], role is null ? null : "Test"));
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<NavigationManager>();
            services.AddSingleton<NavigationManager>(new TestNavigation());
            services.RemoveAll<AuthenticationStateProvider>();
            services.AddScoped<AuthenticationStateProvider>(_ => new TestAuthentication(user));
        }));
        await using var scope = host.Services.CreateAsyncScope();
        await using var renderer = new EditorRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(EditOrganization), ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(EditOrganization.Id)] = id }));
            await root.QuiescenceTask;
            await action(renderer.Editor!, () => root.ToHtmlString());
        });
    }

    private static void SetInput(EditOrganization editor, string name, string value)
    {
        var input = typeof(EditOrganization).GetField("_input", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        input.GetType().GetProperty(name)!.SetValue(input, value);
    }

    private static void SetField(EditOrganization editor, string name, object value) =>
        typeof(EditOrganization).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(editor, value);

    private static async Task InvokeAsync(EditOrganization editor, string method)
    {
        await (Task)typeof(EditOrganization).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null)!;
        typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
    }

    private sealed class EditorRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : StaticHtmlRenderer(services, loggerFactory)
    {
        public EditOrganization? Editor { get; private set; }
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode)
        {
            var component = componentActivator.CreateInstance(componentType);
            if (component is EditOrganization editor) Editor = editor;
            return component;
        }
    }

    private sealed class TestAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/admin/organizations/new");
        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }
}
