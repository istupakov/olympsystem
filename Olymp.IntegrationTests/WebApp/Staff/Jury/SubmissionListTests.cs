using System.Globalization;
using System.Net;
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
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.AntiCheat;
using Olymp.WebApp.Services.Checker;

using Xunit;

using JuryDetails = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails;
using JuryMessages = Olymp.WebApp.Components.Pages.Staff.Jury.Messages;
using JuryProblems = Olymp.WebApp.Components.Pages.Staff.Jury.Problems;
using JuryRunners = Olymp.WebApp.Components.Pages.Staff.Jury.Runners;
using JuryStanding = Olymp.WebApp.Components.Pages.Staff.Jury.Standing;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;
using Organizations = Olymp.WebApp.Components.Pages.Staff.Admin.OrganizationList;
using Users = Olymp.WebApp.Components.Pages.Staff.Admin.UserList;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class SubmissionListTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task OwnAndHiddenScopesMatchInContestAndProblemLists()
    {
        var own = await _factory.AddUserAsync();
        var hidden = await _factory.AddUserAsync();
        var visible = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
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
            var list = await ReadStaffRowsAsync($"/jury/contests/{contest.Id}/submissions?Scope={filter}", contest.Id, user);
            Assert.Equal(expectedId, Assert.Single(list).Id);
            using var task = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryProblems>(
                $"/jury/contests/{contest.Id}/problems?problemId={problem.Id}&Scope={filter}", new() { [nameof(JuryProblems.ContestId)] = contest.Id }, user));
            Assert.Equal($"submission-{expectedId}", Assert.Single(task.QuerySelectorAll("#submissions tbody tr")).Id);
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
    public async Task VerdictFiltersIncludeEveryTestNumberAndSeparateInternalErrors()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
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
            var rows = await ReadStaffRowsAsync($"/jury/contests/{contest.Id}/submissions?Status={filter}", contest.Id);
            Assert.Equal(expected.Order(), rows.Select(row => row.StatusCode).Order());
        }
    }

    [Fact]
    public async Task ParticipantFilterUsesTemporaryAccountsOfThisContestWithoutPagination()
    {
        var permanent = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
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
        var rows = await ReadStaffRowsAsync(path, contest.Id);
        Assert.Equal(21, rows.Count);
        Assert.All(rows, row => { Assert.Equal(participant.Id, row.AuthorId); Assert.True(row.IsParticipant); });
        Assert.Null(first.QuerySelector(".pagination"));
    }

    [Fact]
    public async Task ExactVerdictFilterDistinguishesTestsAndErrorTypes()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var problem = TestEntities.CreateProblem(TestEntities.CreateContest("Exact verdicts", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));
        var compiler = TestEntities.CreateCompilator();
        var submissions = new[] { -1004, -1005, -2004, 2 }.Select(code =>
        {
            var submission = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.UtcNow);
            submission.User = null!;
            submission.StatusCode = code;
            return submission;
        }).ToArray();
        db.Submissions.AddRange(submissions);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        foreach (int? contestId in new int?[] { null, problem.ContestId })
        {
            string path = (contestId is int id ? $"/jury/contests/{id}/submissions" : "/jury/submissions")
                + $"?AuthorId={author.Id}&StatusCode=-1004";
            Assert.Equal(submissions[0].Id, Assert.Single(await ReadStaffRowsAsync(path, contestId)).Id);
            using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(path,
                new() { [nameof(JurySubmissions.ContestId)] = contestId }, Staff(author.Id, RoleNames.Jury)));
            string reset = html.QuerySelector("a[data-clear-filter='StatusCode']")!.GetAttribute("href")!;
            Assert.DoesNotContain("StatusCode=", reset);
            Assert.Equal(4, (await ReadStaffRowsAsync(reset, contestId)).Count);
        }
    }

    [Fact]
    public async Task GlobalProblemSearchAndLanguageFilterCombineWithExactCompiler()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Filters", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var first = TestEntities.CreateProblem(contest);
        first.Name = "Search sorting";
        var second = TestEntities.CreateProblem(contest, 2);
        second.Name = "Other task";
        var cpp = TestEntities.CreateCompilator();
        cpp.Language = "C++";
        var python = TestEntities.CreateCompilator();
        python.Language = "Python";
        var submissions = new[] { (first, cpp), (first, python), (second, cpp) }.Select(pair =>
        {
            var submission = TestEntities.CreateSubmission(author, pair.Item1, pair.Item2, DateTimeOffset.UtcNow);
            submission.User = null!;
            return submission;
        }).ToArray();
        db.Submissions.AddRange(submissions);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string path = $"/jury/submissions?AuthorId={author.Id}&ProblemSearch=sorting&Language=C%2B%2B&Compiler={cpp.Id}";
        Assert.Equal(submissions[0].Id, Assert.Single(await ReadStaffRowsAsync(path)).Id);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(path, [], Staff(author.Id, RoleNames.Jury)));
        Assert.Equal("input", html.QuerySelector("#problem")!.LocalName);
        Assert.NotNull(html.QuerySelector("#language"));
        Assert.Null(html.QuerySelector("#compiler"));
        Assert.Contains(cpp.Description ?? cpp.Name, html.Body!.TextContent);
        string removeCompiler = Assert.Single(html.QuerySelectorAll("a[data-clear-filter]"), item => item.GetAttribute("aria-label") == string.Format(SiteResources.StaffRemoveFilter, SiteResources.Compiler)).GetAttribute("href")!;
        Assert.DoesNotContain("Compiler=", removeCompiler);
        Assert.Contains("Language=", removeCompiler);
        Assert.Contains("ProblemSearch=sorting", removeCompiler);
    }

    [Theory]
    [InlineData("203.0.113.42")]
    [InlineData("2001:db8::42")]
    public async Task IpFilterCombinesWithAuthorAndPreservesOtherFiltersWhenRemoved(string address)
    {
        var (author, submission) = await AddIpSubmissionAsync(address);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var other = TestEntities.CreateSubmission(author, submission.Problem, submission.Compilator, DateTimeOffset.Now);
        other.User = null!;
        other.Problem = null!;
        other.Compilator = null!;
        other.CommiterInfo = address + "0";
        db.Submissions.Add(other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var actor = Staff(author.Id, RoleNames.Jury);
        foreach (int? contestId in new int?[] { null, submission.Problem.ContestId })
        {
            string path = (contestId is int id ? $"/jury/contests/{id}/submissions" : "/jury/submissions")
                + $"?AuthorId={author.Id}&IpAddress={Uri.EscapeDataString(address)}";
            Assert.Equal(submission.Id, Assert.Single(await ReadStaffRowsAsync(path, contestId, actor)).Id);
            using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(path,
                new() { [nameof(JurySubmissions.ContestId)] = contestId }, actor));
            var clear = html.QuerySelector("a[data-clear-filter='IpAddress']")!;
            Assert.NotNull(clear.QuerySelector("svg path"));
            string reset = clear.GetAttribute("href")!;
            Assert.DoesNotContain("IpAddress=", reset);
            Assert.Contains($"AuthorId={author.Id}", reset);
            Assert.Equal(2, (await ReadStaffRowsAsync(reset, contestId, actor)).Count);
        }
    }

    [Theory]
    [InlineData(RoleNames.Jury, "en", "203.0.113.42")]
    [InlineData(RoleNames.Admin, "ru", "2001:db8::42")]
    [InlineData(RoleNames.Jury, "ru", null)]
    [InlineData(RoleNames.Admin, "en", "")]
    public async Task StaffListsAndDetailsShowSavedIpAddress(string role, string culture, string? address)
    {
        var (author, submission) = await AddIpSubmissionAsync(address);
        var actor = Staff(author.Id, role);
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            string label = culture == "ru" ? "IP-адрес" : "IP address";
            string expected = string.IsNullOrWhiteSpace(address) ? "–" : address;
            foreach (int? contestId in new int?[] { null, submission.Problem.ContestId })
            {
                string path = (contestId is int id ? $"/jury/contests/{id}/submissions" : "/jury/submissions") + $"?AuthorId={author.Id}";
                using var list = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(path,
                    new() { [nameof(JurySubmissions.ContestId)] = contestId }, actor));
                var headers = list.QuerySelectorAll("thead th").Select(item => item.TextContent.Trim()).ToArray();
                int column = Array.IndexOf(headers, label);
                Assert.True(column >= 0);
                var row = Assert.Single(await ReadStaffRowsAsync(path, contestId, actor));
                Assert.Equal(address, row.IpAddress);
                Assert.Equal(submission.Id, row.Id);
                using var details = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(
                    "/jury/submissions/" + submission.Id,
                    new() { [nameof(JuryDetails.Id)] = submission.Id }, actor));
                Assert.Equal(expected, Assert.Single(details.QuerySelectorAll("dt"), item => item.TextContent == label).NextElementSibling!.TextContent.Trim());
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public async Task PublicListsAndDetailsDoNotExposeSavedIpAddressEvenToAuthor()
    {
        const string address = "2001:db8::42";
        var (author, submission) = await AddIpSubmissionAsync(address);
        using var client = await _factory.LoginAsync(author.UserName!);
        foreach (string path in new[] { "/submissions", $"/submissions/{submission.Id}", $"/contests/{submission.Problem.ContestId}/problems?problemId={submission.ProblemId}" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(address, html);
            using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(document.QuerySelectorAll("thead th"), item => item.TextContent.Trim() is "IP address" or "IP-адрес");
        }
    }

    [Fact]
    public async Task GlobalSubmissionListFiltersContestsAndPreservesReturnContext()
    {
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var first = TestEntities.CreateContest("First global", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        var second = TestEntities.CreateContest("Second global", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(5));
        first.IsHidden = second.IsHidden = true;
        var compiler = TestEntities.CreateCompilator();
        var submissions = new[] { first, second }.Select(contest =>
        {
            var submission = TestEntities.CreateSubmission(author, TestEntities.CreateProblem(contest), compiler, DateTimeOffset.Now);
            submission.User = null!;
            return submission;
        }).ToArray();
        db.Submissions.AddRange(submissions);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var all = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(
            $"/jury/submissions?AuthorId={author.Id}", [], Staff(author.Id, RoleNames.Jury)));
        var allRows = await ReadStaffRowsAsync($"/jury/submissions?AuthorId={author.Id}");
        Assert.Equal(2, allRows.Count);
        Assert.Contains(allRows, item => item.Contest == first.Name);
        Assert.Contains(allRows, item => item.Contest == second.Name);
        using var filtered = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JurySubmissions>(
            $"/jury/submissions?AuthorId={author.Id}&Contest={second.Id}", [], Staff(author.Id)));
        var filteredRow = Assert.Single(await ReadStaffRowsAsync($"/jury/submissions?AuthorId={author.Id}&Contest={second.Id}"));
        Assert.Equal(second.Id, filteredRow.ContestId);
        string sourceUrl = new Olymp.WebApp.Components.Staff.StaffGridState().DetailUrl(
            $"https://localhost/jury/submissions?AuthorId={author.Id}&Contest={second.Id}", $"jury/submissions/{filteredRow.Id}", filteredRow.Id);
        Assert.Contains("returnTo=", sourceUrl);
        using var source = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails>(
            sourceUrl, new() { ["Id"] = submissions[1].Id }, Staff(author.Id)));
        Assert.Contains(source.QuerySelectorAll("a"), link => link.GetAttribute("href") == $"/jury/submissions?AuthorId={author.Id}&Contest={second.Id}&row=0");
    }

}
