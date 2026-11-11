using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class CompetitorManagementServiceTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    public enum StaffOperation { Approval, OutOfCompetition, Disqualification, ApproveAll, Import, AssignLogins, GenerateLogins, RemoveLogins, Delete, Cleanup }

    public static TheoryData<string?, bool, bool, StaffOperation> StaffAccessCases
    {
        get
        {
            var data = new TheoryData<string?, bool, bool, StaffOperation>();
            foreach (var (role, assigned, allowed) in new (string?, bool, bool)[]
            {
                (null, false, false), ("user", true, false), (RoleNames.Jury, false, false),
                (RoleNames.Jury, true, true), (RoleNames.Admin, false, true)
            })
            {
                foreach (var operation in Enum.GetValues<StaffOperation>())
                    data.Add(role, assigned, allowed, operation);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(StaffAccessCases))]
    public async Task StaffOperationsAuthorizeDirectCalls(string? role, bool assigned, bool allowed, StaffOperation operation)
    {
        var token = TestContext.Current.CancellationToken;
        var user = role is null ? null : await site.AddUserAsync(role == "user" ? null : role);
        var actor = user is null ? new ClaimsPrincipal(new ClaimsIdentity()) : Actor(user, role!);
        var contest = await site.AddContestAsync(assigned ? user : null);
        var participant = await AddParticipantAsync(contest.Id);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Contests.Where(item => item.Id == contest.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.EndTime, DateTimeOffset.Now.AddHours(-1)), token);
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        string login = Guid.NewGuid().ToString("N");
        var logins = new Dictionary<string, string> { [login] = "FirstPassword123" };
        if (operation == StaffOperation.RemoveLogins)
            await service.AssignLoginsAsync(await AdminAsync(), contest.Id, logins, token);
        Task RunAsync() => operation switch
        {
            StaffOperation.Approval => service.SetApprovalAsync(actor, contest.Id, participant.Id, true, token),
            StaffOperation.OutOfCompetition => service.SetOutOfCompetitionAsync(actor, contest.Id, participant.Id, true, token),
            StaffOperation.Disqualification => service.SetDisqualificationAsync(actor, contest.Id, participant.Id, true, token),
            StaffOperation.ApproveAll => service.ApproveAllAsync(actor, contest.Id, token),
            StaffOperation.Import => service.ImportAsync(actor, contest.Id, ["Imported"], token),
            StaffOperation.AssignLogins => service.AssignLoginsAsync(actor, contest.Id, logins, token),
            StaffOperation.GenerateLogins => service.GenerateLoginsAsync(actor, contest.Id, token),
            StaffOperation.RemoveLogins => service.RemoveLoginsAsync(actor, contest.Id, token),
            StaffOperation.Delete => service.DeleteAsync(actor, contest.Id, participant.Id, token),
            StaffOperation.Cleanup => service.DeleteWithoutSubmissionsAsync(actor, contest.Id, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (allowed)
            await RunAsync();
        else
            Assert.Equal(SiteResources.StaffAccessDenied, (await Assert.ThrowsAsync<OperationValidationException>(RunAsync)).Message);

        var saved = await db.Competitors.AsNoTracking().SingleOrDefaultAsync(item => item.Id == participant.Id, token);
        if (allowed && operation is StaffOperation.Delete or StaffOperation.Cleanup)
            Assert.Null(saved);
        else
        {
            Assert.NotNull(saved);
            Assert.Equal(allowed && (operation is StaffOperation.Approval or StaffOperation.ApproveAll), saved.IsApproved);
            Assert.Equal(allowed && operation == StaffOperation.OutOfCompetition, saved.IsOutOfCompetition);
            Assert.Equal(allowed && operation == StaffOperation.Disqualification, saved.IsDisqualified);
            bool hasLogin = operation == StaffOperation.RemoveLogins ? !allowed
                : allowed && (operation is StaffOperation.AssignLogins or StaffOperation.GenerateLogins);
            Assert.Equal(hasLogin, saved.UserName is not null);
            if (hasLogin)
                Assert.Equal(operation == StaffOperation.GenerateLogins ? "olymp001" : login, saved.UserName);
            Assert.Equal(saved.UserName is not null, saved.PasswordHash is not null);
        }
        Assert.Equal(allowed && operation == StaffOperation.Import ? 1 : 0,
            await db.Competitors.CountAsync(item => item.ContestId == contest.Id && item.Name == "Imported", token));
        // Generated logins are global; release this test's login before the next case.
        if (allowed && operation == StaffOperation.GenerateLogins)
            await service.RemoveLoginsAsync(actor, contest.Id, token);
    }

    [Fact]
    public async Task StoredAssignmentIsRecheckedForEachOperationDespiteStaleClaim()
    {
        var token = TestContext.Current.CancellationToken;
        var user = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(user);
        var participant = await AddParticipantAsync(contest.Id);
        var actor = Actor(user, RoleNames.Jury);
        actor.Identities.Single().AddClaim(new Claim(ClaimNames.ContestManage, contest.Id.ToString()));
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        await service.SetApprovalAsync(actor, contest.Id, participant.Id, true, token);
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.UserClaims.Where(item => item.UserId == user.Id && item.ClaimType == ClaimNames.ContestManage)
            .ExecuteDeleteAsync(token);

        await Assert.ThrowsAsync<OperationValidationException>(() => service.SetApprovalAsync(actor, contest.Id, participant.Id, false, token));
        Assert.True(await db.Competitors.Where(item => item.Id == participant.Id).Select(item => item.IsApproved).SingleAsync(token));
    }

    [Fact]
    public async Task AdministratorCannotUseRegistrationCancellationForAnotherUser()
    {
        var token = TestContext.Current.CancellationToken;
        var actor = await AdminAsync();
        var contest = await site.AddContestAsync();
        var participant = await AddParticipantAsync(contest.Id);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Contests.Where(item => item.Id == contest.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.IsHidden, false).SetProperty(item => item.StartTime, DateTimeOffset.Now.AddHours(1)), token);
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();

        await Assert.ThrowsAsync<OperationValidationException>(() => service.CancelRegistrationAsync(actor, contest.Id, participant.Id, token));
        Assert.True(await db.Competitors.AnyAsync(item => item.Id == participant.Id, token));
    }

    private static ClaimsPrincipal Actor(OlympUser user, string role) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, role)], "Test"));

    private async Task<ClaimsPrincipal> AdminAsync() => Actor(await site.AddUserAsync(RoleNames.Admin), RoleNames.Admin);

    [Fact]
    public async Task ParticipantFlagsCanBeChangedAndParticipantDeleted()
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var participant = await AddParticipantAsync(contest.Id);
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        await service.SetApprovalAsync(actor, contest.Id, participant.Id, true, token);
        await service.SetOutOfCompetitionAsync(actor, contest.Id, participant.Id, true, token);
        await service.SetDisqualificationAsync(actor, contest.Id, participant.Id, true, token);
        using (var verification = site.Services.CreateScope())
        {
            var saved = await verification.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .SingleAsync(x => x.Id == participant.Id, token);
            Assert.True(saved.IsApproved);
            Assert.True(saved.IsOutOfCompetition);
            Assert.True(saved.IsDisqualified);
        }
        await service.SetApprovalAsync(actor, contest.Id, participant.Id, false, token);
        await service.SetOutOfCompetitionAsync(actor, contest.Id, participant.Id, false, token);
        await service.SetDisqualificationAsync(actor, contest.Id, participant.Id, false, token);
        using (var verification = site.Services.CreateScope())
        {
            var saved = await verification.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .SingleAsync(x => x.Id == participant.Id, token);
            Assert.False(saved.IsApproved);
            Assert.False(saved.IsOutOfCompetition);
            Assert.False(saved.IsDisqualified);
        }
        await service.DeleteAsync(actor, contest.Id, participant.Id, token);
        using var final = site.Services.CreateScope();
        Assert.False(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(x => x.Id == participant.Id, token));
    }

    [Fact]
    public async Task DeletingParticipantPreservesMemberAndTheirOtherRegistration()
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var member = await site.AddUserAsync();
        var contest = await site.AddContestAsync();
        var otherContest = await site.AddContestAsync();
        var participant = await AddParticipantAsync(contest.Id);
        var otherParticipant = await AddParticipantAsync(otherContest.Id);
        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            db.Attach(member);
            var registrations = await db.Competitors.AsTracking().Include(x => x.Members)
                .Where(x => x.Id == participant.Id || x.Id == otherParticipant.Id).ToListAsync(token);
            foreach (var registration in registrations)
                registration.Members.Add(member);
            await db.SaveChangesAsync(token);
        }

        using (var scope = site.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>().DeleteAsync(actor, contest.Id, participant.Id, token);

        using var final = site.Services.CreateScope();
        var saved = final.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await saved.Competitors.AnyAsync(x => x.Id == participant.Id, token));
        var remainingMember = await saved.OlympUsers.Include(x => x.Memberships).SingleAsync(x => x.Id == member.Id, token);
        Assert.Equal(otherParticipant.Id, Assert.Single(remainingMember.Memberships).Id);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task DeletingWithoutSubmissionsRollsBackEarlierDeletions(string failure)
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var first = await AddParticipantAsync(contest.Id);
        var second = await AddParticipantAsync(contest.Id);
        var member = await site.AddUserAsync();
        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Contests.Where(x => x.Id == contest.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndTime, DateTimeOffset.Now.AddHours(-1)), token);
            db.Attach(member);
            var participant = await db.Competitors.AsTracking().Include(x => x.Members)
                .SingleAsync(x => x.Id == first.Id, token);
            participant.Members.Add(member);
            await db.SaveChangesAsync(token);
            Assert.True((await setup.ServiceProvider.GetRequiredService<UserManager<User>>().AddClaimAsync(participant,
                new(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<IUserStore<User>>(provider => new FailSecondDeletionStore(
                provider.GetRequiredService<OlympContext>(), provider.GetRequiredService<IdentityErrorDescriber>(), failure, cancellation))));
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteWithoutSubmissionsAsync(actor, contest.Id, cancellation.Token));
            else if (failure == "exception")
                await Assert.ThrowsAsync<DbUpdateException>(() => service.DeleteWithoutSubmissionsAsync(actor, contest.Id, cancellation.Token));
            else
                await Assert.ThrowsAsync<IdentityOperationException>(() => service.DeleteWithoutSubmissionsAsync(actor, contest.Id, cancellation.Token));
        }

        using (var verification = site.Services.CreateScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.True(await db.Competitors.AnyAsync(x => x.Id == first.Id && x.Members.Any(m => m.Id == member.Id), token));
            Assert.True(await db.Competitors.AnyAsync(x => x.Id == second.Id, token));
            Assert.True(await db.UserClaims.AnyAsync(x => x.UserId == first.Id && x.ClaimType == ClaimNames.CompetitorContest, token));
        }

        using var retry = site.Services.CreateScope();
        Assert.Equal(2, await retry.ServiceProvider.GetRequiredService<CompetitorManagementService>()
            .DeleteWithoutSubmissionsAsync(actor, contest.Id, token));
    }

    [Fact]
    public async Task ApproveAllPreservesAlreadyApprovedParticipantsAndDoesNotRepeatWrites()
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var approved = await AddParticipantAsync(contest.Id);
        var pending = await AddParticipantAsync(contest.Id);
        using (var setup = site.Services.CreateScope())
        {
            approved.IsApproved = true;
            Assert.True((await setup.ServiceProvider.GetRequiredService<UserManager<User>>().UpdateAsync(approved)).Succeeded);
        }

        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AsTracking()
            .SingleAsync(x => x.Id == approved.Id, token);
        await service.ApproveAllAsync(actor, contest.Id, token);
        var afterFirst = await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(x => x.ContestId == contest.Id).ToDictionaryAsync(x => x.Id, token);
        Assert.All(afterFirst.Values, participant => Assert.True(participant.IsApproved));
        Assert.Equal(approved.ConcurrencyStamp, afterFirst[approved.Id].ConcurrencyStamp);

        await service.ApproveAllAsync(actor, contest.Id, token);
        using var final = site.Services.CreateScope();
        var saved = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(x => x.ContestId == contest.Id).ToDictionaryAsync(x => x.Id, token);
        Assert.Equal(afterFirst[approved.Id].ConcurrencyStamp, saved[approved.Id].ConcurrencyStamp);
        Assert.Equal(afterFirst[pending.Id].ConcurrencyStamp, saved[pending.Id].ConcurrencyStamp);
    }

    [Theory]
    [InlineData("ApproveAll", "identity")]
    [InlineData("Import", "identity")]
    [InlineData("ApproveAll", "exception")]
    [InlineData("Import", "exception")]
    [InlineData("ApproveAll", "cancel")]
    [InlineData("Import", "cancel")]
    public async Task BulkOperationsRollBackEarlierWritesAndCanBeRetriedInNewScope(string operation, string failure)
    {
        var actor = await AdminAsync();
        var contest = await site.AddContestAsync();
        if (operation == "ApproveAll")
        {
            await AddParticipantAsync(contest.Id);
            await AddParticipantAsync(contest.Id);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<IUserValidator<User>>(_ => new FailSecondParticipantValidator(failure, cancellation))));
        Task RunAsync(CompetitorManagementService service, CancellationToken token) => operation == "ApproveAll"
            ? service.ApproveAllAsync(actor, contest.Id, token)
            : service.ImportAsync(actor, contest.Id, ["First", "Second"], token);

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            if (failure == "exception")
                await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(service, cancellation.Token));
            else if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(service, cancellation.Token));
            else
            {
                var exception = await Assert.ThrowsAsync<IdentityOperationException>(() => RunAsync(service, cancellation.Token));
                var error = Assert.Single(exception.Errors);
                Assert.Equal("InjectedFailure", error.Code);
                Assert.Equal("Injected second participant failure", error.Description);
            }
        }

        using (var verification = site.Services.CreateScope())
        {
            var saved = verification.ServiceProvider.GetRequiredService<OlympContext>();
            var participants = await saved.Competitors.Where(x => x.ContestId == contest.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
            if (operation == "Import")
                Assert.Empty(participants);
            else
            {
                Assert.Equal(2, participants.Count);
                Assert.All(participants, participant => Assert.False(participant.IsApproved));
            }
        }

        using (var retry = site.Services.CreateScope())
            await RunAsync(retry.ServiceProvider.GetRequiredService<CompetitorManagementService>(), TestContext.Current.CancellationToken);
        using var final = site.Services.CreateScope();
        var approved = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(x => x.ContestId == contest.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, approved.Count);
        Assert.All(approved, participant => Assert.True(participant.IsApproved));
    }

    [Fact]
    public async Task ServiceRejectsParticipantsFromOtherContests()
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var foreign = await site.AddContestAsync();
        var participant = await AddParticipantAsync(foreign.Id);
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        foreach (var operation in new Func<Task>[]
        {
            () => service.SetApprovalAsync(actor, contest.Id, participant.Id, true, token),
            () => service.SetOutOfCompetitionAsync(actor, contest.Id, participant.Id, true, token),
            () => service.SetDisqualificationAsync(actor, contest.Id, participant.Id, true, token),
            () => service.DeleteAsync(actor, contest.Id, participant.Id, token)
        })
            await Assert.ThrowsAsync<EntityNotFoundException>(operation);
        using var final = site.Services.CreateScope();
        var db = final.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == contest.Id, TestContext.Current.CancellationToken));
        var saved = await db.Competitors.SingleAsync(x => x.Id == participant.Id, TestContext.Current.CancellationToken);
        Assert.False(saved.IsApproved);
        Assert.False(saved.IsOutOfCompetition);
        Assert.False(saved.IsDisqualified);
    }

    [Fact]
    public async Task ImportReturnsCountsWithoutDuplicatingParticipants()
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        var first = await service.ImportAsync(actor, contest.Id, ["First", "Second"], token);
        Assert.Equal(new ParticipantImportSummary(2, 0), first);
        var repeated = await service.ImportAsync(actor, contest.Id, ["first", "Second", "Third"], token);
        Assert.Equal(new ParticipantImportSummary(1, 2), repeated);
        await Assert.ThrowsAsync<OperationValidationException>(() =>
            service.ImportAsync(actor, contest.Id, ["Fourth", "fourth"], token));
        using var final = site.Services.CreateScope();
        var names = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(x => x.ContestId == contest.Id).Select(x => x.Name).OrderBy(x => x)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["First", "Second", "Third"], names);
    }

    [Fact]
    public async Task AssigningLoginsPreservesExistingAccountsAndDoesNotConsumeInput()
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var first = await AddParticipantAsync(contest.Id);
        var existing = await AddParticipantAsync(contest.Id);
        var last = await AddParticipantAsync(contest.Id);
        var prefix = Guid.NewGuid().ToString("N");
        var logins = new Dictionary<string, string>
        {
            [prefix + "-EXISTING"] = "DoNotReplaceThisPassword",
            [prefix + "-first"] = "FirstPassword123",
            [prefix + "-last"] = "LastPassword123"
        };
        using (var setup = site.Services.CreateScope())
        {
            var users = setup.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.SetUserNameAsync(existing, prefix + "-existing")).Succeeded);
            Assert.True((await users.AddPasswordAsync(existing, "ExistingPassword123")).Succeeded);
            Assert.True((await users.AddClaimAsync(existing, new(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var scope = site.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>().AssignLoginsAsync(actor, contest.Id, logins, token);
        }

        Assert.Equal(3, logins.Count);
        Assert.Equal("DoNotReplaceThisPassword", logins[prefix + "-EXISTING"]);
        Assert.Equal("FirstPassword123", logins[prefix + "-first"]);
        using var final = site.Services.CreateScope();
        var db = final.ServiceProvider.GetRequiredService<OlympContext>();
        var saved = await db.Competitors.Where(x => x.ContestId == contest.Id).ToDictionaryAsync(x => x.Id, token);
        Assert.Equal(prefix + "-first", saved[first.Id].UserName);
        Assert.Equal("FirstPassword123", saved[first.Id].PasswordHash);
        Assert.Equal(prefix + "-last", saved[last.Id].UserName);
        Assert.Equal("LastPassword123", saved[last.Id].PasswordHash);
        Assert.Equal(prefix + "-existing", saved[existing.Id].UserName);
        Assert.Equal("ExistingPassword123", saved[existing.Id].PasswordHash);
        var ids = saved.Keys.ToArray();
        var claims = await db.UserClaims.Where(x => ids.Contains(x.UserId) && x.ClaimType == ClaimNames.CompetitorContest)
            .ToListAsync(token);
        Assert.Equal(3, claims.Count);
        Assert.All(claims, claim => Assert.Equal(contest.Id.ToString(), claim.ClaimValue));
    }

    [Fact]
    public async Task ImportApprovalAndLoginOperationsCanBeCombinedInOneScope()
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var prefix = Guid.NewGuid().ToString("N");
        var logins = new Dictionary<string, string>
        {
            [prefix + "-first"] = "FirstPassword123",
            [prefix + "-second"] = "SecondPassword123",
            [prefix + "-third"] = "ThirdPassword123"
        };
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();

        await service.ImportAsync(actor, contest.Id, ["First", "Second"], token);
        var firstId = await db.Competitors.Where(x => x.ContestId == contest.Id && x.Name == "First")
            .Select(x => x.Id).SingleAsync(token);
        var first = await db.Competitors.AsTracking().SingleAsync(x => x.Id == firstId, token);
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>()
            .AddClaimAsync(first, new("Unrelated", "keep"))).Succeeded);
        await service.SetApprovalAsync(actor, contest.Id, firstId, false, token);
        await service.ApproveAllAsync(actor, contest.Id, token);
        Assert.Equal(new ParticipantImportSummary(1, 1), await service.ImportAsync(actor, contest.Id, ["First", "Third"], token));

        await service.AssignLoginsAsync(actor, contest.Id, logins, token);
        var trackingBehavior = db.ChangeTracker.QueryTrackingBehavior;
        await service.RemoveLoginsAsync(actor, contest.Id, token);
        Assert.Equal(trackingBehavior, db.ChangeTracker.QueryTrackingBehavior);
        var removed = await db.Competitors.Where(x => x.ContestId == contest.Id).ToListAsync(token);
        Assert.All(removed, participant =>
        {
            Assert.Null(participant.UserName);
            Assert.Null(participant.PasswordHash);
        });
        var ids = removed.Select(x => x.Id).ToArray();
        Assert.False(await db.UserClaims.AnyAsync(x => ids.Contains(x.UserId)
            && x.ClaimType == ClaimNames.CompetitorContest && x.ClaimValue == contest.Id.ToString(), token));
        Assert.True(await db.UserClaims.AnyAsync(x => x.UserId == firstId && x.ClaimType == "Unrelated" && x.ClaimValue == "keep", token));

        await service.AssignLoginsAsync(actor, contest.Id, logins, token);
        using var final = site.Services.CreateScope();
        var savedContext = final.ServiceProvider.GetRequiredService<OlympContext>();
        var saved = await savedContext.Competitors.Where(x => x.ContestId == contest.Id)
            .OrderBy(x => x.RegistrationDate).ThenBy(x => x.Id).ToListAsync(token);
        Assert.Equal(["First", "Second", "Third"], saved.Select(x => x.Name));
        Assert.Equal(logins.Keys, saved.Select(x => x.UserName));
        Assert.All(saved, participant =>
        {
            Assert.True(participant.IsApproved);
            Assert.Equal(logins[participant.UserName!], participant.PasswordHash);
        });
        Assert.Equal(3, await savedContext.UserClaims.CountAsync(x => ids.Contains(x.UserId)
            && x.ClaimType == ClaimNames.CompetitorContest && x.ClaimValue == contest.Id.ToString(), token));
    }

    [Fact]
    public async Task AssignLoginsOrdersPreviouslyTrackedParticipantsByRegistrationDateAndId()
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var late = await AddParticipantAsync(contest.Id);
        var early = await AddParticipantAsync(contest.Id);
        var earlyTie = await AddParticipantAsync(contest.Id);
        var time = DateTimeOffset.Now.AddHours(-2);
        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Competitors.Where(x => x.ContestId == contest.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RegistrationDate, time), token);
            await db.Competitors.Where(x => x.Id == late.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RegistrationDate, time.AddHours(1)), token);
        }

        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        await service.SetOutOfCompetitionAsync(actor, contest.Id, late.Id, true, token);
        await service.SetApprovalAsync(actor, contest.Id, earlyTie.Id, false, token);
        var prefix = Guid.NewGuid().ToString("N");
        await service.AssignLoginsAsync(actor, contest.Id, new Dictionary<string, string>
        {
            [prefix + "-first"] = "FirstPassword123",
            [prefix + "-second"] = "SecondPassword123",
            [prefix + "-third"] = "ThirdPassword123"
        }, token);

        using var final = site.Services.CreateScope();
        var saved = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(x => x.ContestId == contest.Id).ToDictionaryAsync(x => x.Id, token);
        Assert.Equal(prefix + "-first", saved[early.Id].UserName);
        Assert.Equal(prefix + "-second", saved[earlyTie.Id].UserName);
        Assert.Equal(prefix + "-third", saved[late.Id].UserName);
    }

    [Theory]
    [InlineData(false, "identity")]
    [InlineData(true, "identity")]
    [InlineData(false, "exception")]
    [InlineData(true, "exception")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task LoginChangesRollBackUsernamesPasswordsAndClaimsAndCanBeRetried(bool removing, string failure)
    {
        var actor = await AdminAsync();
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        await AddParticipantAsync(contest.Id);
        await AddParticipantAsync(contest.Id);
        var prefix = Guid.NewGuid().ToString("N");
        var logins = new Dictionary<string, string>
        {
            [prefix + "-first"] = "FirstPassword123",
            [prefix + "-second"] = "SecondPassword123"
        };
        Task RunAsync(CompetitorManagementService service, CancellationToken cancellationToken) => removing
            ? service.RemoveLoginsAsync(actor, contest.Id, cancellationToken)
            : service.AssignLoginsAsync(actor, contest.Id, logins, cancellationToken);

        if (removing)
        {
            using var setup = site.Services.CreateScope();
            await setup.ServiceProvider.GetRequiredService<CompetitorManagementService>().AssignLoginsAsync(actor, contest.Id, logins, token);
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<IUserValidator<User>>(_ => new FailSecondParticipantValidator(failure, cancellation, removing ? 3 : 5))));
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            var operationContext = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var trackingBehavior = operationContext.ChangeTracker.QueryTrackingBehavior;
            if (failure == "exception")
                await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(service, cancellation.Token));
            else if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(service, cancellation.Token));
            else
            {
                var exception = await Assert.ThrowsAsync<IdentityOperationException>(() => RunAsync(service, cancellation.Token));
                Assert.Equal("InjectedFailure", Assert.Single(exception.Errors).Code);
            }
            Assert.Equal(trackingBehavior, operationContext.ChangeTracker.QueryTrackingBehavior);
        }

        using (var verification = site.Services.CreateScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
            var saved = await db.Competitors.Where(x => x.ContestId == contest.Id).ToListAsync(token);
            Assert.Equal(2, saved.Count);
            Assert.All(saved, participant =>
            {
                if (removing)
                    Assert.Equal(logins[participant.UserName!], participant.PasswordHash);
                else
                {
                    Assert.Null(participant.UserName);
                    Assert.Null(participant.PasswordHash);
                }
            });
            var ids = saved.Select(x => x.Id).ToArray();
            Assert.Equal(removing ? 2 : 0, await db.UserClaims.CountAsync(x => ids.Contains(x.UserId)
                && x.ClaimType == ClaimNames.CompetitorContest && x.ClaimValue == contest.Id.ToString(), token));
        }

        using (var retry = site.Services.CreateScope())
            await RunAsync(retry.ServiceProvider.GetRequiredService<CompetitorManagementService>(), token);
        using var final = site.Services.CreateScope();
        var context = final.ServiceProvider.GetRequiredService<OlympContext>();
        var participants = await context.Competitors.Where(x => x.ContestId == contest.Id).ToListAsync(token);
        Assert.All(participants, participant =>
        {
            if (removing)
            {
                Assert.Null(participant.UserName);
                Assert.Null(participant.PasswordHash);
            }
            else
                Assert.Equal(logins[participant.UserName!], participant.PasswordHash);
        });
        var participantIds = participants.Select(x => x.Id).ToArray();
        Assert.Equal(removing ? 0 : 2, await context.UserClaims.CountAsync(x => participantIds.Contains(x.UserId)
            && x.ClaimType == ClaimNames.CompetitorContest && x.ClaimValue == contest.Id.ToString(), token));
    }

    private async Task<Competitor> AddParticipantAsync(int contestId)
    {
        var participant = new Competitor
        {
            Name = "Participant " + Guid.NewGuid().ToString("N"),
            ContestId = contestId,
            Contest = null!,
            SecurityStamp = Guid.NewGuid().ToString(),
            Members = [],
            Messages = [],
            Submissions = []
        };
        using var scope = site.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(participant)).Succeeded);
        return participant;
    }

    private sealed class FailSecondDeletionStore(OlympContext context, IdentityErrorDescriber describer,
        string failure, CancellationTokenSource cancellation) : UserStore<User, IdentityRole<int>, OlympContext, int>(context, describer)
    {
        private int _deletions;

        public override Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user is Competitor && ++_deletions == 2)
            {
                if (failure == "exception")
                    throw new DbUpdateException("Injected deletion failure");
                if (failure == "cancel")
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(IdentityResult.Failed(new IdentityError
                {
                    Code = "InjectedFailure",
                    Description = "Injected deletion failure"
                }));
            }
            return base.DeleteAsync(user, cancellationToken);
        }
    }

    private sealed class FailSecondParticipantValidator(string failure, CancellationTokenSource cancellation, int failAt = 2) : IUserValidator<User>
    {
        private int _participants;

        public Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user)
        {
            if (user is Competitor && ++_participants == failAt)
            {
                if (failure == "exception")
                    throw new InvalidOperationException("Injected second participant failure");
                if (failure == "cancel")
                    cancellation.Cancel();
                else
                    return Task.FromResult(IdentityResult.Failed(new IdentityError
                    {
                        Code = "InjectedFailure",
                        Description = "Injected second participant failure"
                    }));
            }
            return Task.FromResult(IdentityResult.Success);
        }
    }

}
