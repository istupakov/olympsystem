using System.Reflection;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ContestPreparationTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AssignedJurySeesImportOnlyBeforeStartAndCannotEditSettings(bool beforeStart)
    {
        var jury = await _factory.AddUserAsync();
        await GrantRoleAsync(jury.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Import access", DateTimeOffset.UtcNow.AddDays(beforeStart ? 1 : -1), DateTimeOffset.UtcNow.AddDays(2));
        var problem = TestEntities.CreateProblem(contest);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.UserClaims.Add(new IdentityUserClaim<int> { UserId = jury.Id, ClaimType = ClaimNames.ContestImport, ClaimValue = contest.Id.ToString() });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var actor = Staff(jury.Id, RoleNames.Jury);
        using var problems = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Problems>(
            $"/jury/contests/{contest.Id}/problems", new() { ["ContestId"] = contest.Id }, actor));
        Assert.Equal(beforeStart, problems.QuerySelector($"a[href='jury/contests/{contest.Id}/import']") is not null);
        using var import = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ContestImport>(
            $"/jury/contests/{contest.Id}/import", new() { ["ContestId"] = contest.Id }, actor));
        Assert.Equal(beforeStart, import.QuerySelector("#import-zip") is not null);
        using var settings = await new HtmlParser().ParseDocumentAsync(await RenderAsync<EditContest>(
            $"/jury/contests/{contest.Id}/edit", new() { ["Id"] = contest.Id }, actor));
        Assert.Null(settings.QuerySelector("#contest-name"));
    }

    [Theory]
    [InlineData(true, true, 2, true)]
    [InlineData(true, false, 2, false)]
    [InlineData(false, true, 2, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, true, 1, false)]
    [InlineData(true, true, -1001, true)]
    public async Task ReferenceButtonRequiresPermissionHiddenAuthorAndCompletedSubmission(bool hidden, bool assigned, int status, bool visible)
    {
        var author = await _factory.AddUserAsync();
        var jury = await _factory.AddUserAsync();
        await GrantRoleAsync(jury.Id, RoleNames.Jury);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Users.Where(user => user.Id == author.Id).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsHidden, hidden), TestContext.Current.CancellationToken);
        var contest = TestEntities.CreateContest("Reference actions", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2));
        var submission = TestEntities.CreateSubmission(author, TestEntities.CreateProblem(contest), TestEntities.CreateCompilator(), DateTimeOffset.UtcNow);
        submission.User = null!;
        submission.StatusCode = status;
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (assigned)
        {
            db.UserClaims.Add(new IdentityUserClaim<int> { UserId = jury.Id, ClaimType = ClaimNames.ContestImport, ClaimValue = contest.Id.ToString() });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionDetails>(
            $"/jury/submissions/{submission.Id}", new() { ["Id"] = submission.Id }, Staff(jury.Id, RoleNames.Jury)));
        Assert.Equal(visible, page.QuerySelectorAll("button").Any(button => button.TextContent == SiteResources.ReferenceAdd));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("existing.cs")]
    [InlineData("folder/existing.cs")]
    public async Task ReferenceFormPrefersDescriptionThenEmailPrefixAndShowsMissingEmailError(string? description)
    {
        var author = await _factory.AddUserAsync();
        author.IsHidden = true;
        var submission = TestEntities.CreateSubmission(author,
            TestEntities.CreateProblem(TestEntities.CreateContest("Reference form", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))),
            TestEntities.CreateCompilator(), DateTimeOffset.UtcNow);
        submission.Id = 123;
        submission.StatusCode = 2;
        submission.Description = description;
        var parameters = new Dictionary<string, object?> { ["Submission"] = submission };
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionReferenceActions>(
            "/jury/submissions/123", parameters, Staff(author.Id), component => ActAsync(component, "Edit")));
        Assert.Equal(string.IsNullOrWhiteSpace(description) ? author.Email!.Split('@')[0] + ".cs" : description,
            page.QuerySelector("#reference-filename")!.GetAttribute("value"));
        author.Email = null;
        string error = await RenderAsync<SubmissionReferenceActions>("/jury/submissions/123", parameters, Staff(author.Id),
            component => ActAsync(component, "Edit"));
        using var errorPage = await new HtmlParser().ParseDocumentAsync(error);
        Assert.Contains(SiteResources.ReferenceEmailRequired, errorPage.Body!.TextContent);
    }

    [Fact]
    public async Task ImportAssignmentPickerOnlyListsJuryAccounts()
    {
        var jury = await _factory.AddUserAsync();
        var ordinary = await _factory.AddUserAsync();
        await GrantRoleAsync(jury.Id, RoleNames.Jury);
        var field = typeof(PermanentAccountPicker).GetField("_search", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<PermanentAccountPicker>("/jury/contests/1/edit",
            new() { ["RequiredRole"] = RoleNames.Jury }, Staff(jury.Id), async component =>
            {
                field.SetValue(component, jury.Email);
                await ActAsync(component, "SearchAsync");
            }));
        Assert.NotNull(page.QuerySelector($"option[value='{jury.Id}']"));
        using var missing = await new HtmlParser().ParseDocumentAsync(await RenderAsync<PermanentAccountPicker>("/jury/contests/1/edit",
            new() { ["RequiredRole"] = RoleNames.Jury }, Staff(jury.Id), async component =>
            {
                field.SetValue(component, ordinary.Email);
                await ActAsync(component, "SearchAsync");
            }));
        Assert.Null(missing.QuerySelector($"option[value='{ordinary.Id}']"));
    }
}
