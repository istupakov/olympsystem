using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public class ReferenceMaterialsTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task ProblemBadgesLinkToValidatorAndReferencesAndReferenceTabKeepsBothKinds()
    {
        var author = await _factory.AddUserAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Reference materials", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var problem = TestEntities.CreateProblem(contest);
        var otherProblem = TestEntities.CreateProblem(contest, 2, problem.Checker);
        var compiler = TestEntities.CreateCompilator();
        compiler.Language = "C++";
        compiler.SourceExtension = ".cpp";
        Submission Create(Problem target, string filename, bool reference)
        {
            var submission = TestEntities.CreateSubmission(author, target, compiler, DateTimeOffset.UtcNow, "// source code"u8.ToArray());
            submission.User = null!;
            submission.Description = filename;
            submission.IsReference = reference;
            submission.StatusCode = 2;
            return submission;
        }
        var validator = Create(problem, "validator.cpp", true);
        var solution = Create(problem, "author.cpp", true);
        db.Submissions.AddRange(validator, solution, Create(problem, "ordinary.cpp", false), Create(otherProblem, "other.cpp", true));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        string path = $"/jury/contests/{contest.Id}/problems?problemId={problem.Id}&scope=reference";
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Problems>(path,
            new() { [nameof(Problems.ContestId)] = contest.Id }, Staff(author.Id)));
        var validatorBadge = Assert.Single(html.QuerySelectorAll("a.badge"), a => a.TextContent == SiteResources.StaffValidator);
        Assert.StartsWith($"jury/submissions/{validator.Id}?returnUrl=", validatorBadge.GetAttribute("href"));
        Assert.Contains(html.QuerySelectorAll("a.badge"), a => a.TextContent == $"{SiteResources.StaffReferenceSolutions} · 1"
            && a.GetAttribute("href") == $"jury/contests/{contest.Id}/submissions?Problem={problem.Id}&Reference=true");
        var rows = html.QuerySelectorAll("#submissions tbody tr");
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { $"submission-{validator.Id}", $"submission-{solution.Id}" }.Order(), rows.Select(r => r.Id).Order());
        Assert.Contains(html.QuerySelectorAll("#submissions .nav-link.active"), a => a.TextContent == SiteResources.StaffReferenceTab);
        Assert.Empty(html.QuerySelectorAll("#submissions .badge"));
        foreach (var reference in new[] { validator, solution })
        {
            var source = Assert.Single(html.QuerySelectorAll($"#submission-{reference.Id} a"));
            Assert.Equal(reference.Description, source.TextContent);
            Assert.Equal(reference.Description, source.GetAttribute("title"));
            Assert.StartsWith($"jury/submissions/{reference.Id}?returnUrl=", source.GetAttribute("href"));
        }

        var staffRows = await ReadStaffRowsAsync($"/jury/contests/{contest.Id}/submissions?Problem={problem.Id}&Reference=true", contest.Id, Staff(author.Id));
        Assert.Equal(2, staffRows.Count);
        Assert.True(staffRows.Single(r => r.Id == validator.Id).IsValidator);
        Assert.False(staffRows.Single(r => r.Id == solution.Id).IsValidator);
        using var list = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionList>(
            $"/jury/contests/{contest.Id}/submissions?Reference=true", new() { [nameof(SubmissionList.ContestId)] = contest.Id }, Staff(author.Id)));
        Assert.Equal(3, list.QuerySelectorAll("#submission-reference option").Length);
        Assert.Equal("true", list.QuerySelector("#submission-reference option[selected]")!.GetAttribute("value"));
        using var details = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionDetails>(
            $"/jury/submissions/{validator.Id}", new() { [nameof(SubmissionDetails.Id)] = validator.Id }, Staff(author.Id)));
        Assert.Contains(details.QuerySelectorAll("a"), a => a.TextContent == SiteResources.StaffRunValidator
            && a.GetAttribute("href") == $"jury/trial-run?Submission={validator.Id}");
    }
}
