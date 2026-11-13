using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Admin;
using Olymp.WebApp.Components.Pages.Staff.Jury;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff;

[Trait("Category", "Integration")]
public class StaffEntityFilterTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntityLabelsMatchAcrossFieldsChipsAndStaffLists(bool missing)
    {
        var user = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Filter contest", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var problem = TestEntities.CreateProblem(contest);
        var compiler = TestEntities.CreateCompilator();
        var organization = new Organization { Name = "Filter organization", Users = [] };
        db.Problems.Add(problem);
        db.Compilators.Add(compiler);
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        int accountId = missing ? -91 : user.Id, organizationId = missing ? -92 : organization.Id,
            contestId = missing ? -93 : contest.Id, problemId = missing ? -94 : problem.Id, compilerId = missing ? -95 : compiler.Id;
        string accountLabel = missing ? $"ID {accountId}" : $"{user.Name} · ID {accountId}";
        string organizationLabel = missing ? $"ID {organizationId}" : $"{organization.Name} · ID {organizationId}";
        string contestLabel = missing ? $"ID {contestId}" : $"{contest.Name} · ID {contestId}";
        string problemLabel = missing ? $"ID {problemId}" : $"{problem.NameWithNumber} · ID {problemId}";
        string compilerLabel = missing ? $"ID {compilerId}" : $"{compiler.Description ?? compiler.Name} · ID {compilerId}";
        using var participants = await new HtmlParser().ParseDocumentAsync(await RenderAsync<ParticipantList>(
            $"/jury/participants?Coach={accountId}&Member={accountId}&Contest={contestId}&Organization={organizationId}", [], Staff(user.Id)));
        Assert.Equal(accountLabel, participants.QuerySelector("#participant-coach")!.GetAttribute("value"));
        Assert.Equal(accountLabel, participants.QuerySelector("#participant-member")!.GetAttribute("value"));
        AssertLabel(participants, "Coach", accountLabel);
        AssertLabel(participants, "Member", accountLabel);
        AssertLabel(participants, "Contest", contestLabel);
        AssertLabel(participants, "Organization", organizationLabel);
        Assert.Equal(contestLabel, participants.QuerySelector($"#participant-contest option[value='{contestId}']")!.TextContent);
        Assert.Equal(organizationLabel, participants.QuerySelector($"#participant-organization option[value='{organizationId}']")!.TextContent);
        using var accounts = await new HtmlParser().ParseDocumentAsync(await RenderAsync<UserList>(
            $"/admin/users?Coach={accountId}&CoachOrganization={organizationId}&Organization={organizationId}", [], Staff(user.Id)));
        AssertLabel(accounts, "Coach", accountLabel);
        AssertLabel(accounts, "CoachOrganization", organizationLabel);
        AssertLabel(accounts, "Organization", organizationLabel);
        Assert.Equal(organizationLabel, accounts.QuerySelector($"#user-organization option[value='{organizationId}']")!.TextContent);
        using var submissions = await new HtmlParser().ParseDocumentAsync(await RenderAsync<SubmissionList>(
            $"/jury/submissions?AuthorId={accountId}&Compiler={compilerId}&Problem={problemId}", [], Staff(user.Id)));
        Assert.Equal(accountLabel, submissions.QuerySelector("#author")!.GetAttribute("value"));
        Assert.Equal(problemLabel, submissions.QuerySelector("#problem")!.GetAttribute("value"));
        AssertLabel(submissions, "AuthorId", accountLabel);
        AssertLabel(submissions, "Compiler", compilerLabel);
        AssertLabel(submissions, "Problem", problemLabel);
        using var messages = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Messages>(
            $"/jury/contests/{contest.Id}/messages?AuthorId={accountId}&Problem={problemId}", new() { ["ContestId"] = contest.Id }, Staff(user.Id)));
        Assert.Equal(accountLabel, messages.QuerySelector("#message-author")!.GetAttribute("value"));
        Assert.Equal(problemLabel, messages.QuerySelector($"#message-problem option[value='{problemId}']")!.TextContent);
        AssertLabel(messages, "AuthorId", accountLabel);
        AssertLabel(messages, "Problem", problemLabel);
    }

    private static void AssertLabel(IDocument document, string filter, string expected) =>
        Assert.EndsWith(": " + expected, document.QuerySelector($"[data-clear-filter='{filter}']")!.ParentElement!.TextContent.Trim());
}
