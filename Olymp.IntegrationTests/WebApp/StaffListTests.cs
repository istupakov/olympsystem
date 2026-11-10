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
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.AntiCheat;

using Xunit;

using JuryDetails = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails;
using JuryProblems = Olymp.WebApp.Components.Pages.Staff.Jury.Problems;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;
using Organizations = Olymp.WebApp.Components.Pages.Staff.Admin.OrganizationList;
using Users = Olymp.WebApp.Components.Pages.Staff.Admin.UserList;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public partial class StaffListTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task SubmittingAfterReturningToTaskClearsCodeAndRefreshesOwnList()
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Stay on task", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        db.Problems.Add(problem);
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, author.Id.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            $"/jury/contests/{contest.Id}/problems?problemId={problem.Id}#submissions", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, user,
            async component =>
            {
                var input = (SubmissionInput)typeof(JuryProblems).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
                input.Code = "// Stay on task\nint main(void) { return 0; }";
                input.CompilatorId = compiler.Id;
                await (Task)typeof(JuryProblems).GetMethod("SubmitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null)!;
            }));
        Assert.Single(page.QuerySelectorAll("#submissions tbody tr"));
        Assert.Equal("", page.QuerySelector("#submission-code")!.TextContent);
        Assert.Equal(compiler.Id.ToString(), page.QuerySelector("#submission-compiler option[selected]")!.GetAttribute("value"));
        Assert.Equal(author.Id, await db.Submissions.Where(item => item.ProblemId == problem.Id).Select(item => item.UserId).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OwnAndHiddenScopesMatchInContestAndProblemLists()
    {
        var own = await factory.AddUserAsync();
        var hidden = await factory.AddUserAsync();
        var visible = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Users.Where(item => item.Id == hidden.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsHidden, true), TestContext.Current.CancellationToken);
        var contest = TestEntities.CreateContest("Scopes", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        var submissions = new[] { own, hidden, visible }.Select(author =>
        {
            var submission = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now);
            submission.User = null!;
            return submission;
        }).ToArray();
        db.Submissions.AddRange(submissions);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, own.Id.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));
        foreach (var (filter, expectedId) in new[] { ("own", submissions[0].Id), ("hidden", submissions[1].Id) })
        {
            using var list = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(
                $"/jury/contests/{contest.Id}/submissions?Scope={filter}", new() { [nameof(JurySubmissions.ContestId)] = contest.Id }, user));
            Assert.Equal(expectedId.ToString(), Assert.Single(list.QuerySelectorAll("tbody tr")).Children[0].TextContent.Trim());
            using var task = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
                $"/jury/contests/{contest.Id}/problems?problemId={problem.Id}&Scope={filter}", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, user));
            Assert.Equal(expectedId.ToString(), Assert.Single(task.QuerySelectorAll("#submissions tbody tr")).Children[0].TextContent.Trim());
            string link = task.QuerySelector("#submissions tbody tr td a")!.GetAttribute("href")!;
            using var source = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(link,
                new() { [nameof(JuryDetails.Id)] = expectedId }, user));
            string back = Assert.Single(source.QuerySelectorAll("a"), item => item.TextContent == SiteResources.BackToProblem).GetAttribute("href")!;
            Assert.Contains($"problemId={problem.Id}", back);
            Assert.Contains($"Scope={filter}", back);
            Assert.EndsWith("#submissions", back);
        }
    }

    [Fact]
    public async Task OrganizationSearchIncludesFullNameAndPrefixAndPreservesPagination()
    {
        string prefix = Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Organizations.AddRange(Enumerable.Range(0, 21).Select(index => new Organization
        {
            Name = $"Organization {index:00}",
            FullName = index % 2 == 0 ? prefix : null,
            ACMName = index % 2 != 0 ? prefix : null,
            Users = []
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var first = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Organizations>($"/admin/organizations?Search={prefix}", []));
        Assert.Equal(20, first.QuerySelectorAll("tbody tr").Length);
        string next = first.QuerySelector("a[rel=next]")!.GetAttribute("href")!;
        Assert.Contains($"Search={prefix}", next);
        using var second = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Organizations>(next, []));
        Assert.Single(second.QuerySelectorAll("tbody tr"));
        Assert.Null(second.QuerySelector("a[rel=next]"));
    }

    [Fact]
    public async Task AuthorLinkUsesExactAccountWithinContestAndBackLinkRequiresListContext()
    {
        var author = await factory.AddUserAsync();
        var namesake = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Author", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var other = TestEntities.CreateContest("Other", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        var own = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now);
        var another = TestEntities.CreateSubmission(namesake, problem, compiler, DateTimeOffset.Now);
        var foreign = TestEntities.CreateSubmission(author, TestEntities.CreateProblem(other), compiler, DateTimeOffset.Now);
        own.User = another.User = foreign.User = null!;
        db.Submissions.AddRange(own, another, foreign);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var details = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(
            $"/jury/submissions/{own.Id}", new() { [nameof(JuryDetails.Id)] = own.Id }));
        Assert.DoesNotContain(details.QuerySelectorAll("a"), link => link.TextContent == SiteResources.StaffBackToSubmissions);
        string authorUrl = Assert.Single(details.QuerySelectorAll("a[href*='AuthorId=']")).GetAttribute("href")!;
        Assert.EndsWith($"/submissions?AuthorId={author.Id}", authorUrl);
        using var list = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(authorUrl,
            new() { [nameof(JurySubmissions.ContestId)] = contest.Id }));
        Assert.Equal(own.Id.ToString(), Assert.Single(list.QuerySelectorAll("tbody tr")).Children[0].TextContent.Trim());
        string back = $"/jury/contests/{contest.Id}/submissions?Page=2";
        using var fromList = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(
            $"/jury/submissions/{own.Id}?ReturnUrl={Uri.EscapeDataString(back)}", new() { [nameof(JuryDetails.Id)] = own.Id }));
        Assert.NotNull(fromList.QuerySelector($"a[href='{back}']"));
        own.User = author;
        another.User = namesake;
        using var report = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SimilarityReport>("/jury/anticheat",
            new() { [nameof(SimilarityReport.Reports)] = new PlagiarismReport[] { new(author, namesake, [new(own, another, 0.9f)]) } }));
        Assert.Equal(2, report.QuerySelectorAll("a[target='_blank'][rel='noopener']").Length);
    }

    [Fact]
    public async Task JuryProblemsShowInactiveUnpublishedStatementsAndOnlyCommonClarifications()
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Unpublished", DateTimeOffset.Now.AddDays(1), DateTimeOffset.Now.AddDays(2));
        contest.IsHidden = true;
        var problem = TestEntities.CreateProblem(contest);
        problem.IsActive = false;
        problem.Text = "<strong>Unpublished statement</strong>";
        var second = TestEntities.CreateProblem(contest, 2);
        db.Problems.AddRange(problem, second);
        db.Messages.AddRange(
            new Message { Contest = contest, Problem = problem, JuryText = "Common clarification" },
            new Message { Contest = contest, Problem = problem, UserId = author.Id, JuryText = "Private reply" },
            new Message { Contest = contest, Problem = second, JuryText = "Other problem" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, author.Id.ToString()), new(ClaimTypes.Role, RoleNames.Jury)], "Test"));
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
            $"/jury/contests/{contest.Id}/problems?problemId={problem.Id}", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, user));
        Assert.Equal("Unpublished statement", page.QuerySelector(".problem-statement strong")!.TextContent);
        Assert.Contains("Common clarification", page.QuerySelector(".problem-statement .jury-announcements")!.TextContent);
        Assert.DoesNotContain("Private reply", page.DocumentElement.OuterHtml);
        Assert.DoesNotContain("Other problem", page.QuerySelector(".problem-statement")!.TextContent);
        Assert.Equal(2, page.QuerySelectorAll(".problem-list .list-group-item").Length);
        Assert.NotNull(page.QuerySelector(".problem-list .badge"));
        Assert.NotNull(page.QuerySelector("#submission-code"));
        Assert.Empty(page.QuerySelectorAll("a[href*='downloadproblems']"));
    }

    [Fact]
    public async Task UserTypeAndRoleFiltersCombineAndShowAllRoles()
    {
        var jury = await factory.AddUserAsync();
        var ordinary = await factory.AddUserAsync();
        string name = Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Users.Where(user => user.Id == jury.Id || user.Id == ordinary.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.Name, name), TestContext.Current.CancellationToken);
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        if (!await roles.RoleExistsAsync(RoleNames.Jury)) Assert.True((await roles.CreateAsync(new(RoleNames.Jury))).Succeeded);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await manager.FindByIdAsync(jury.Id.ToString()))!;
        Assert.True((await manager.AddToRoleAsync(user, RoleNames.Jury)).Succeeded);
        Assert.True((await manager.AddClaimAsync(user, new Claim(ClaimNames.CoachOrg, "1"))).Succeeded);
        using var document = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Users>(
            $"/admin/users?Search={name}&AccountType=permanent&Role=jury", []));
        var row = Assert.Single(document.QuerySelectorAll("tbody tr"));
        Assert.Equal(jury.Id.ToString(), row.Children[0].TextContent.Trim());
        Assert.Equal(2, row.Children[5].QuerySelectorAll(".badge").Length);
        Assert.Null(document.QuerySelector(".pagination"));
        using var participants = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Users>(
            $"/admin/users?Search={name}&AccountType=participant&Role=jury", []));
        Assert.Empty(participants.QuerySelectorAll("tbody tr"));
    }

    [Fact]
    public async Task VerdictFiltersIncludeEveryTestNumberAndSeparateInternalErrors()
    {
        var author = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Verdicts", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        int[] codes = [0, 1, 2, -1, -10, -1001, -1999, -2000, -2001, -2999, -3001, -3999, -4001, -4999, -5001, -5999, -6001, -6999];
        foreach (int code in codes)
        {
            var submission = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now);
            submission.User = null!;
            submission.StatusCode = code;
            db.Submissions.Add(submission);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (string Filter, int[] Codes)[] filters = [
            ("accepted", [2]), ("compilation", [-1]), ("queued", [0]), ("checking", [1]),
            ("wrong-answer", [-1001, -1999]), ("time-limit", [-2001, -2999]),
            ("runtime-error", [-3001, -3999]), ("presentation-error", [-4001, -4999]),
            ("idleness-limit", [-5001, -5999]), ("memory-limit", [-6001, -6999]),
            ("internal-error", [-10, -2000]), ("rejected", codes.Where(code => code is not (0 or 1 or 2)).ToArray())
        ];
        foreach (var (filter, expected) in filters)
        {
            using var document = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(
                $"/jury/contests/{contest.Id}/submissions?Status={filter}", new() { [nameof(JurySubmissions.ContestId)] = contest.Id }));
            var actual = document.QuerySelectorAll("tbody tr").Select(row => row.Children[5].TextContent.Trim()).Order().ToArray();
            Assert.Equal(expected.Select(CheckStatusCodes.Description).Order(), actual);
        }
    }

    [Fact]
    public async Task ParticipantFilterUsesTemporaryAccountsOfThisContestAndPreservesPagination()
    {
        var permanent = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Participants", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        var other = TestEntities.CreateContest("Other", DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(4));
        var participant = TestEntities.CreateCompetitor(contest, "Team in contest");
        var foreign = TestEntities.CreateCompetitor(other, "Team in other contest");
        db.Competitors.AddRange(participant, foreign);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        foreach (var author in new Olymp.Domain.Models.User[] { permanent, participant, foreign })
        {
            for (int i = 0; i < (author == participant ? 21 : 1); i++)
            {
                var submission = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now);
                submission.User = null!;
                submission.StatusCode = -2001;
                db.Submissions.Add(submission);
            }
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string path = $"/jury/contests/{contest.Id}/submissions?OnlyParticipants=true&Status=time-limit";
        using var first = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(path,
            new() { [nameof(JurySubmissions.ContestId)] = contest.Id }));
        Assert.Equal(20, first.QuerySelectorAll("tbody tr").Length);
        Assert.All(first.QuerySelectorAll("tbody tr"), row => Assert.Contains(participant.Name, row.Children[2].TextContent));
        Assert.All(first.QuerySelectorAll("tbody tr"), row => Assert.NotNull(row.Children[2].QuerySelector(".badge")));
        string next = Assert.Single(first.QuerySelectorAll("a[rel=next]")).GetAttribute("href")!;
        Assert.Contains("OnlyParticipants=true", next);
        Assert.Contains("Status=time-limit", next);
        using var last = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(next,
            new() { [nameof(JurySubmissions.ContestId)] = contest.Id }));
        Assert.Single(last.QuerySelectorAll("tbody tr"));
        Assert.Null(last.QuerySelector("a[rel=next]"));
        Assert.Equal("2", last.QuerySelector(".pagination [aria-current=page]")!.TextContent);
    }

    private async Task<string> RenderAsync<T>(string path, Dictionary<string, object?> parameters, ClaimsPrincipal? user = null,
        Func<T, Task>? action = null, Action<IServiceCollection>? configure = null) where T : IComponent
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
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
    }

    private sealed class TestAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }
}
