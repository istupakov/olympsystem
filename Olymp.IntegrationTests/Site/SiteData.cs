using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Tests.Shared;

namespace Olymp.IntegrationTests.Site;

public sealed class SiteData
{
    public const string Password = "TestPassword123";
    public const string Source = "System.Console.WriteLine(42);";

    public required Contest Contest { get; init; }
    public required Contest OtherContest { get; init; }
    public required Competitor Competitor { get; init; }
    public required OlympUser Member { get; init; }
    public required Problem Problem { get; init; }
    public required Problem OtherProblem { get; init; }
    public required Compilator Compilator { get; init; }
    public required Submission Submission { get; init; }
    public required Submission OtherSubmission { get; init; }

    public static async Task<SiteData> SeedAsync(IServiceProvider services)
    {
        var context = services.GetRequiredService<OlympContext>();
        var users = services.GetRequiredService<UserManager<User>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole<int>>>();
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest("Integration contest", now.AddDays(-1), now.AddDays(1));
        var otherContest = TestEntities.CreateContest("Other contest", now.AddDays(-1), now.AddDays(1));
        context.Contests.AddRange(contest, otherContest);
        await context.SaveChangesAsync();

        var competitor = CreateCompetitor("team", contest);
        var otherCompetitor = CreateCompetitor("other-team", otherContest);
        EnsureSuccess(await users.CreateAsync(competitor));
        EnsureSuccess(await users.CreateAsync(otherCompetitor));
        EnsureSuccess(await users.AddClaimAsync(competitor,
            new Claim(ClaimNames.CompetitorContest, contest.Id.ToString())));

        var member = CreateUser("member");
        EnsureSuccess(await users.CreateAsync(member, Password));
        competitor.Members.Add(member);

        foreach (var role in new[] { RoleNames.Admin, RoleNames.Jury })
        {
            EnsureSuccess(await roles.CreateAsync(new IdentityRole<int>(role)));
            var user = CreateUser(role);
            EnsureSuccess(await users.CreateAsync(user, Password));
            EnsureSuccess(await users.AddToRoleAsync(user, role));
        }

        var checker = TestEntities.CreateChecker();
        var problem = TestEntities.CreateProblem(contest, checker: checker);
        var otherProblem = TestEntities.CreateProblem(otherContest, checker: checker);
        var compilator = TestEntities.CreateCompilator();
        var sourceCode = Encoding.ASCII.GetBytes(Source);
        var submission = TestEntities.CreateSubmission(competitor, problem, compilator, now, sourceCode);
        var otherSubmission = TestEntities.CreateSubmission(otherCompetitor, otherProblem, compilator, now, sourceCode);
        context.Submissions.AddRange(submission, otherSubmission);
        await context.SaveChangesAsync();

        return new SiteData
        {
            Contest = contest,
            OtherContest = otherContest,
            Competitor = competitor,
            Member = member,
            Problem = problem,
            OtherProblem = otherProblem,
            Compilator = compilator,
            Submission = submission,
            OtherSubmission = otherSubmission
        };
    }

    private static Competitor CreateCompetitor(string name, Contest contest)
    {
        var competitor = TestEntities.CreateCompetitor(contest, name);
        competitor.UserName = name;
        competitor.PasswordHash = Password;
        return competitor;
    }

    private static OlympUser CreateUser(string name)
    {
        return new OlympUser
        {
            Name = name,
            UserName = name,
            EmailConfirmed = true,
            Memberships = [],
            Teams = [],
            Messages = [],
            Submissions = []
        };
    }

    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
    }
}
