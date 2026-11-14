using System.Collections;
using System.Reflection;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components;

using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Services.AntiCheat;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public class AnalysisReportTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferredSideIsFirstWithoutMutatingComparisonAndAllDataColumnsAreSortable(bool bySubmission)
    {
        var author = await _factory.AddUserAsync();
        var other = await _factory.AddUserAsync();
        var contest = TestEntities.CreateContest("Report", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        contest.Id = 10;
        var problem = TestEntities.CreateProblem(contest); problem.Id = 20;
        var compiler = TestEntities.CreateCompilator();
        var source = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now); source.Id = 200;
        var match = TestEntities.CreateSubmission(other, problem, compiler, DateTimeOffset.Now); match.Id = 100;
        var original = new PlagiarismCase(match, source, 0.91);
        var report = new[] { new PlagiarismReport(other, author, [original]) };

        Dictionary<string, object?> parameters = new()
        {
            [nameof(AnalysisReport.Similarity)] = report,
            [nameof(AnalysisReport.PreferredAuthorId)] = bySubmission ? null : author.Id,
            [nameof(AnalysisReport.PreferredSubmissionId)] = bySubmission ? source.Id : null
        };
        var component = new AnalysisReport();
        ParameterView.FromDictionary(parameters).SetParameterProperties(component);
        var row = Assert.Single(ReadRows(component, "SimilarRows"));
        var normalized = (PlagiarismCase)row.GetType().GetProperty("Case")!.GetValue(row)!;
        Assert.Same(source, normalized.Submission1);
        Assert.Same(match, normalized.Submission2);
        Assert.Equal(original.Similarity, normalized.Similarity);
        Assert.Same(match, original.Submission1);
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<AnalysisReport>("/jury/anticheat", parameters, Staff(author.Id)));

        var sorts = html.QuerySelectorAll("th a[href*='sort=']");
        Assert.Equal(6, sorts.Length);
        Assert.Equal(6, sorts.Select(link => link.TextContent.Trim()).Distinct().Count());
    }

    [Fact]
    public async Task ParentTaskExclusionFiltersBothTabsAndReinclusionRestoresCachedRows()
    {
        var author = await _factory.AddUserAsync();
        var other = await _factory.AddUserAsync();
        var contest = TestEntities.CreateContest("Report", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        var problem = TestEntities.CreateProblem(contest); problem.Id = 20;
        var compiler = TestEntities.CreateCompilator();
        var first = TestEntities.CreateSubmission(author, problem, compiler, DateTimeOffset.Now); first.Id = 100;
        var second = TestEntities.CreateSubmission(other, problem, compiler, DateTimeOffset.Now); second.Id = 200;
        var excluded = new HashSet<int>();

        Dictionary<string, object?> parameters = new()
        {
            [nameof(AnalysisReport.Similarity)] = new[] { new PlagiarismReport(author, other, [new(first, second, 0.9)]) },
            [nameof(AnalysisReport.Ai)] = new[] { new AIUsingReport(author, [new(first, new(0.9, 0, 0, []))]) },
            [nameof(AnalysisReport.ExcludedProblems)] = excluded
        };
        var component = new AnalysisReport();
        ParameterView.FromDictionary(parameters).SetParameterProperties(component);
        Assert.Single(ReadRows(component, "SimilarRows")); Assert.Single(ReadRows(component, "AiRows"));
        excluded.Add(problem.Id);
        Assert.Empty(ReadRows(component, "SimilarRows")); Assert.Empty(ReadRows(component, "AiRows"));
        excluded.Clear();
        Assert.Single(ReadRows(component, "SimilarRows")); Assert.Single(ReadRows(component, "AiRows"));
        using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<AnalysisReport>("/jury/anticheat", parameters, Staff(author.Id)));

        Assert.Empty(html.QuerySelectorAll("input[id^=report-problem-]"));
        Assert.NotNull(html.QuerySelector("#report-author"));
    }

    private static IEnumerable<object> ReadRows(AnalysisReport report, string name) =>
        ((IEnumerable)typeof(AnalysisReport).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(report)!).Cast<object>();
}
