using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Admin;
using Olymp.WebApp.Components.Pages.Staff.Jury;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

[Trait("Category", "Integration")]
public class AdminListTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task UserRelationsUseExactAccountIdsAndOrganizationClaims()
    {
        var member = await factory.AddUserAsync();
        var coach = await factory.AddUserAsync();
        var other = await factory.AddUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var organization = new Organization { Name = "Relations", Users = [] };
        var contest = TestEntities.CreateContest("Relations", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var participant = TestEntities.CreateCompetitor(contest, "Linked participant");
        participant.CoachId = coach.Id;
        participant.Organization = organization;
        participant.Members.Add((await db.OlympUsers.FindAsync([member.Id], TestContext.Current.CancellationToken))!);
        participant.MemberInfos = $"Member;{member.Id}";
        db.Competitors.Add(participant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var storedCoach = (await manager.FindByIdAsync(coach.Id.ToString()))!;
        Assert.True((await manager.AddClaimAsync(storedCoach, new Claim(ClaimNames.CoachOrg, organization.Id.ToString()))).Succeeded);
        Assert.Empty(await new UserList { Organization = organization.Id }.Query(db).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(coach.Id, Assert.Single(await new UserList { CoachOrganization = organization.Id }.Query(db).ToListAsync(TestContext.Current.CancellationToken)).Id);
        Assert.Equal(participant.Id, Assert.Single(await new ParticipantList { Coach = coach.Id }.Query(db).ToListAsync(TestContext.Current.CancellationToken)).Id);
        var participation = Assert.Single(await new ParticipantList { Member = member.Id }.Query(db).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(participant.Id, participation.Id);
        Assert.Equal(contest.Id, participation.ContestId);
        Assert.Equal(contest.Name, participation.Contest.Name);
        Assert.Empty(await new ParticipantList { Member = other.Id }.Query(db).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await new UserList { CoachOrganization = organization.Id, Organization = organization.Id }.Query(db).ToListAsync(TestContext.Current.CancellationToken));
        var sorted = await new ParticipantList { Member = member.Id }.Query(db).OrderBy(item => item.UserName).ThenBy(item => item.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(participant.Id, Assert.Single(sorted).Id);
    }

    [Fact]
    public async Task CompilersDefaultOrderShowsActiveThenNewestAndAllowsExplicitSorting()
    {
        string search = Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var compilers = Enumerable.Range(0, 4).Select(index =>
        {
            var compiler = TestEntities.CreateCompilator();
            compiler.Description = search;
            compiler.IsActive = index % 2 == 0;
            return compiler;
        }).ToArray();
        db.Compilators.AddRange(compilers);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var query = new CompilerList { Search = search }.Query(db);
        var actual = await query.Select(item => new { item.Id, item.IsActive }).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(compilers.Where(item => item.IsActive).Select(item => item.Id).OrderDescending()
            .Concat(compilers.Where(item => !item.IsActive).Select(item => item.Id).OrderDescending()), actual.Select(item => item.Id));
        Assert.Equal(compilers.Select(item => item.Id).Order(),
            await query.OrderBy(item => item.Id).Select(item => item.Id).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewsSearchMatchesBodyAndCompilerSearchMatchesConfiguration()
    {
        string search = Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var news = new News { Title = "Body match", Text = search, PublicationDate = DateTimeOffset.UtcNow };
        var compiler = TestEntities.CreateCompilator();
        compiler.ConfigName = search;
        db.News.Add(news);
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(news.Id, Assert.Single(await new NewsList { Search = search }.Query(db).ToListAsync(TestContext.Current.CancellationToken)).Id);
        Assert.Equal(compiler.Id, Assert.Single(await new CompilerList { Search = search }.Query(db).ToListAsync(TestContext.Current.CancellationToken)).Id);
        Assert.Empty(await new CompilerList { Search = search, Active = !compiler.IsActive }.Query(db).ToListAsync(TestContext.Current.CancellationToken));
    }
}
