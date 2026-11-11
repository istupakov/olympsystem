using System.Globalization;
using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;

using Xunit;

using JuryDetails = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;

namespace Olymp.IntegrationTests.WebApp;

public partial class StaffListTests
{
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
                var row = list.QuerySelector($"#submission-{submission.Id}")!;
                Assert.Equal(expected, row.QuerySelectorAll("td")[column].TextContent.Trim());
                string sourceUrl = row.QuerySelector("a")!.GetAttribute("href")!;
                using var details = await new HtmlParser().ParseDocumentAsync(await RenderAsync<JuryDetails>(
                    "/jury/submissions/" + submission.Id,
                    new() { [nameof(JuryDetails.Id)] = submission.Id }, actor));
                Assert.Contains(label + ": " + expected, details.Body!.TextContent);
                Assert.Contains($"jury/submissions/{submission.Id}", sourceUrl);
                Assert.Contains(Uri.EscapeDataString(path), sourceUrl);
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
        using var client = await factory.LoginAsync(author.UserName!);
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

    private async Task<(OlympUser Author, Submission Submission)> AddIpSubmissionAsync(string? address)
    {
        var author = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
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
}
