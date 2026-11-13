using System.IO.Compression;
using System.Net;
using System.Text;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

[Trait("Category", "Integration")]
public partial class TestDetailsTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(RoleNames.Jury)]
    [InlineData(RoleNames.Admin)]
    public async Task StaffTestDownloadsPreserveAllBytesAndIncludeInactiveTests(string role)
    {
        var staff = await _factory.AddUserAsync();
        await GrantRoleAsync(staff.Id, role);
        var (problem, tests) = await AddWorkspaceTestsAsync();
        using var client = await _factory.LoginAsync(staff.UserName!);
        foreach (var test in tests)
        {
            foreach (bool output in new[] { false, true })
            {
                using var response = await client.GetAsync($"/jury/problems/{problem.Id}/tests/{test.Id}/" + (output ? "output" : "input"),
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(output ? test.Output : test.Input, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                Assert.Contains(TestQuery.FileName(test.Number, output), response.Content.Headers.ContentDisposition!.ToString());
            }
        }
        using var zipResponse = await client.GetAsync($"/jury/problems/{problem.Id}/tests/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, zipResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await zipResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(["input001.txt", "input007.txt", "output001.txt", "output007.txt"], archive.Entries.Select(item => item.FullName).Order());
        foreach (var test in tests)
        {
            await using var input = archive.GetEntry(TestQuery.FileName(test.Number, false))!.Open();
            using var bytes = new MemoryStream();
            await input.CopyToAsync(bytes, TestContext.Current.CancellationToken);
            Assert.Equal(test.Input, bytes.ToArray());
        }
        using var wrongProblem = await client.GetAsync($"/jury/problems/{problem.Id + 10000}/tests/{tests[0].Id}/input", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, wrongProblem.StatusCode);
        var ordinary = await _factory.AddUserAsync();
        using var ordinaryClient = await _factory.LoginAsync(ordinary.UserName!);
        using var denied = await ordinaryClient.GetAsync($"/jury/problems/{problem.Id}/tests/download", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("AccessDenied", denied.Headers.Location!.ToString());
    }

    [Fact]
    public async Task TestDetailsReadBoundedPrefixesAndNeighborsFollowFilters()
    {
        var staff = await _factory.AddUserAsync();
        var (problem, tests) = await AddWorkspaceTestsAsync();
        string returnTo = $"/jury/problems/{problem.Id}/tests?Active=true&sort=Number&row=1";
        using var page = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TestDetails>(
            $"/jury/problems/{problem.Id}/tests/{tests[0].Id}?returnTo={Uri.EscapeDataString(returnTo)}",
            new() { ["ProblemId"] = problem.Id, ["Id"] = tests[0].Id }, Staff(staff.Id, RoleNames.Jury)));
        Assert.Contains("first input", page.Body!.TextContent);
        Assert.Contains(SiteResources.StaffPreviewTruncated, page.Body.TextContent);
        Assert.DoesNotContain("TAIL-MUST-NOT-LOAD", page.Body.TextContent);
        Assert.DoesNotContain(page.QuerySelectorAll("a"), item => item.TextContent == SiteResources.StaffNext);
        Assert.Contains(page.QuerySelectorAll("a"), item => item.GetAttribute("href") == returnTo);
        using var unfiltered = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TestDetails>(
            $"/jury/problems/{problem.Id}/tests/{tests[0].Id}",
            new() { ["ProblemId"] = problem.Id, ["Id"] = tests[0].Id }, Staff(staff.Id, RoleNames.Jury)));
        Assert.Contains(unfiltered.QuerySelectorAll("a"), item => item.TextContent == SiteResources.StaffNext);
        using var binary = await new HtmlParser().ParseDocumentAsync(await RenderAsync<TestDetails>(
            $"/jury/problems/{problem.Id}/tests/{tests[1].Id}",
            new() { ["ProblemId"] = problem.Id, ["Id"] = tests[1].Id }, Staff(staff.Id, RoleNames.Jury)));
        Assert.Contains(SiteResources.StaffPreviewBinary, binary.Body!.TextContent);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(tests[0].Id, Assert.Single(await TestQuery.Rows(TestQuery.Filter(db, problem.Id, true, false))
            .ToListAsync(TestContext.Current.CancellationToken)).Id);
    }

    private async Task<(Problem Problem, Test[] Tests)> AddWorkspaceTestsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Workspace", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2));
        contest.IsHidden = true;
        var problem = TestEntities.CreateProblem(contest);
        problem.IsActive = false;
        var tests = new[] {
            new Test { Problem = problem, Number = 1, IsActive = true, IsOpen = false, Input = "first input"u8.ToArray(),
                Output = Encoding.UTF8.GetBytes(new string('x', TestPreview.ByteLimit + 10) + "TAIL-MUST-NOT-LOAD") },
            new Test { Problem = problem, Number = 7, IsActive = false, IsOpen = false, Input = [0, 255, 128, 1], Output = [] }
        };
        db.Tests.AddRange(tests);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (problem, tests);
    }

}
