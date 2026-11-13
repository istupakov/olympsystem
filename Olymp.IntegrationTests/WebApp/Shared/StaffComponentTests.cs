using System.Reflection;
using System.Security.Claims;

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
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Staff;

using Xunit;

using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;

namespace Olymp.IntegrationTests.WebApp.Shared;

public abstract class StaffComponentTests(WebAppFactory site) : IClassFixture<WebAppFactory>
{
    protected readonly WebAppFactory _factory = site;

    protected async Task<string> RenderAsync<T>(string path, Dictionary<string, object?> parameters, ClaimsPrincipal? user = null,
        Func<T, Task>? action = null, Action<IServiceCollection>? configure = null) where T : IComponent
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<NavigationManager>();
            services.AddSingleton<NavigationManager>(new TestNavigation(path));
            services.RemoveAll<AuthenticationStateProvider>();
            services.AddScoped<AuthenticationStateProvider>(_ => new TestAuthentication(user ?? new ClaimsPrincipal(new ClaimsIdentity())));
            configure?.Invoke(services);
        }));
        await using var scope = host.Services.CreateAsyncScope();
        await using var renderer = new PageRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = renderer.BeginRenderingComponent(typeof(T), ParameterView.FromDictionary(parameters));
            await component.QuiescenceTask;
            if (action is not null)
            {
                await action((T)renderer.Root!);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderer.Root, null);
            }
            return component.ToHtmlString();
        });
    }

    protected async Task<List<StaffSubmissionListItem>> ReadStaffRowsAsync(string path, int? contestId = null, ClaimsPrincipal? user = null)
    {
        List<StaffSubmissionListItem> rows = [];
        await RenderAsync<JurySubmissions>(path, new() { [nameof(JurySubmissions.ContestId)] = contestId }, user, async component =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            rows = await component.Query(scope.ServiceProvider.GetRequiredService<OlympContext>()).ToListAsync(TestContext.Current.CancellationToken);
        });
        return rows;
    }

    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : StaticHtmlRenderer(services, loggerFactory)
    {
        public IComponent? Root { get; private set; }
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode)
        {
            var component = componentActivator.CreateInstance(componentType);
            if (parentComponentId is null) Root = component;
            return component;
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string path) => Initialize("https://localhost/", new Uri(new Uri("https://localhost/"), path).AbsoluteUri);
        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
            NotifyLocationChanged(false);
        }
    }

    private sealed class TestAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }
    protected async Task<(OlympUser Author, Submission Submission)> AddIpSubmissionAsync(string? address)
    {
        var author = await _factory.AddUserAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("IP visibility", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1));
        contest.IsOpen = true;
        var submission = TestEntities.CreateSubmission(author, TestEntities.CreateProblem(contest), TestEntities.CreateCompilator(), DateTimeOffset.Now);
        submission.User = null!;
        submission.CommiterInfo = address;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (author, submission);
    }
    protected async Task GrantRoleAsync(int userId, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        if (!await roles.RoleExistsAsync(role)) Assert.True((await roles.CreateAsync(new(role))).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(userId.ToString()))!, role)).Succeeded);
    }


    protected sealed class WorkspaceClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    protected static ClaimsPrincipal Staff(int id, string role = RoleNames.Admin) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, role)], "Test"));

    protected static Task ActAsync(object page, string method, params object?[] args) =>
        (Task?)page.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args) ?? Task.CompletedTask;


}
