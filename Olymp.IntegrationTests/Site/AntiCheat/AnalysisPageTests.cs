using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Site;

using Xunit;

namespace Olymp.IntegrationTests.Site.AntiCheat;

[Trait("Category", "Integration")]
public class AnalysisPageTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    [Theory]
    [InlineData("en", "The end date must be on or after the start date.")]
    [InlineData("ru", "Дата окончания должна быть не раньше даты начала.")]
    public async Task ReversedDateRangeShowsLocalizedFieldErrorAndKeepsInput(string culture, string message)
    {
        using var client = await factory.LoginAsync(RoleNames.Admin);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);

        using var response = await SiteFactory.PostFormAsync(client, "/AntiCheat/ByDate", new()
        {
            ["Input.StartDate"] = "2030-01-03T00:00:00",
            ["Input.StopDate"] = "2030-01-02T00:00:00",
            ["Input.Threshold"] = "1"
        });
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal(message, document.QuerySelector("[data-valmsg-for='Input.StopDate']")!.TextContent);
        Assert.StartsWith("2030-01-03", document.QuerySelector("input[name='Input.StartDate']")!.GetAttribute("value"));
        Assert.StartsWith("2030-01-02", document.QuerySelector("input[name='Input.StopDate']")!.GetAttribute("value"));
        Assert.Empty(document.QuerySelectorAll("#plagiarism-report .accordion-item"));
    }

    [Fact]
    public async Task EqualDatesAreAllowedForInclusivePeriod()
    {
        using var client = await factory.LoginAsync(RoleNames.Admin);
        using var response = await SiteFactory.PostFormAsync(client, "/AntiCheat/ByDate", new()
        {
            ["Input.StartDate"] = "2030-01-02T00:00:00",
            ["Input.StopDate"] = "2030-01-02T00:00:00",
            ["Input.Threshold"] = "1"
        });
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Empty(document.QuerySelectorAll(".field-validation-error, .validation-summary-errors"));
    }

    [Theory]
    [InlineData("en", "User not found.")]
    [InlineData("ru", "Пользователь не найден.")]
    public async Task MissingUserShowsLocalizedErrorButExistingUserWithoutSubmissionsDoesNot(string culture, string message)
    {
        using var client = await factory.LoginAsync(RoleNames.Admin);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var missing = Guid.NewGuid().ToString("N");

        foreach (var name in new[] { missing, factory.Data.Member.UserName! })
        {
            using var response = await SiteFactory.PostFormAsync(client, "/AntiCheat/ByUser", new()
            {
                ["Input.UserName"] = name,
                ["Input.Threshold"] = "1"
            });
            response.EnsureSuccessStatusCode();
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Equal(name == missing ? message : "", document.QuerySelector("[data-valmsg-for='Input.UserName']")!.TextContent);
            Assert.Equal(name, document.QuerySelector("input[name='Input.UserName']")!.GetAttribute("value"));
            Assert.Empty(document.QuerySelectorAll("#plagiarism-report .accordion-item"));
        }
    }

    [Fact]
    public async Task AIDetectionUsesDecodedSourceAndSubmissionLanguage()
    {
        const string comment = "Русский комментарий";
        var bytes = await SetSourceAsync(factory.Data.Submission.Id, "# " + comment + "\nprint(42)", "Python");
        using var client = await factory.LoginAsync(RoleNames.Admin);

        using var response = await SiteFactory.PostFormAsync(client, "/AntiCheat/AIDetectionTest", new()
        {
            ["Input.SubmissionId"] = factory.Data.Submission.Id.ToString()
        });
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var report = document.QuerySelector("main samp")!;
        Assert.Contains("Score:", report.TextContent);
        Assert.Contains("Cyrillic Comments: 1", report.TextContent);
        Assert.DoesNotContain("Probability:", report.TextContent);
        Assert.Contains(comment, Assert.Single(report.QuerySelectorAll("pre code")).TextContent);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Submissions
            .SingleAsync(submission => submission.Id == factory.Data.Submission.Id, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, saved.SourceCode);
    }

    [Fact]
    public async Task SimilarityPageComparesDecodedSourcesWithDifferentIdentifiers()
    {
        await SetSourceAsync(factory.Data.Submission.Id, "имя = число + 1;", "C");
        await SetSourceAsync(factory.Data.OtherSubmission.Id, "renamed = other + 42;", "C");
        using var client = await factory.LoginAsync(RoleNames.Admin);

        using var response = await SiteFactory.PostFormAsync(client, "/AntiCheat/PlagiarismTest", new()
        {
            ["Input.SubmissionId1"] = factory.Data.Submission.Id.ToString(),
            ["Input.SubmissionId2"] = factory.Data.OtherSubmission.Id.ToString()
        });
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal("1", document.QuerySelector("main samp pre")!.TextContent.Trim());
    }

    [Theory]
    [InlineData("0.6", true)]
    [InlineData("0.7", false)]
    public async Task ContestReportFiltersByScoreAndRendersCommentDetails(string threshold, bool included)
    {
        const string comment = "Русский комментарий";
        await SetSourceAsync(factory.Data.Submission.Id, "// " + comment + "\nreturn 0;", "C++");
        using var client = await factory.LoginAsync(RoleNames.Admin);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        using var response = await SiteFactory.PostFormAsync(client, "/AntiCheat?action=AIUsing", new()
        {
            ["Input.ContestId"] = factory.Data.Contest.Id.ToString(),
            ["Input.Threshold"] = threshold
        });
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var report = Assert.Single(document.QuerySelectorAll("#ai-report"));
        Assert.Single(document.QuerySelectorAll("#plagiarism-report"));
        Assert.Equal(included ? 1 : 0, report.QuerySelectorAll(".accordion-item").Length);
        if (included)
        {
            Assert.Contains("score", report.TextContent);
            Assert.Contains(comment, report.TextContent);
            Assert.DoesNotContain("probability", report.TextContent);
        }
    }

    private async Task<byte[]> SetSourceAsync(int id, string source, string language)
    {
        var bytes = Encoding.GetEncoding(1251).GetBytes(source);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var token = TestContext.Current.CancellationToken;
        await db.Submissions.Where(submission => submission.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(submission => submission.SourceCode, bytes), token);
        await db.Compilators.Where(compiler => compiler.Id == factory.Data.Compilator.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(compiler => compiler.Language, language), token);
        return bytes;
    }
}
