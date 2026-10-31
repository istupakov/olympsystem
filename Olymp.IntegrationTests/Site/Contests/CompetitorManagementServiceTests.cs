using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.Services;
using Olymp.Site.Services.Contests;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class CompetitorManagementServiceTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Theory]
    [InlineData("identity")]
    [InlineData("exception")]
    public async Task PageHandlesExpectedServiceExceptionsAndLeavesUnexpectedFailuresAsServerErrors(string failure)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        await AddParticipantAsync(contest.Id);
        await AddParticipantAsync(contest.Id);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<IUserValidator<User>>(_ => new FailSecondParticipantValidator(failure, cancellation))));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = owner.UserName!,
            ["Input.Password"] = SiteFixture.Password
        })).StatusCode);
        using var response = await SiteFixture.PostAsync(client,
            $"/Contests/Competitors/Manage/{contest.Id}?handler=ApproveAll", []);
        if (failure == "identity")
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = await response.Content.ReadFromJsonAsync<IdentityError[]>(TestContext.Current.CancellationToken);
            var error = Assert.Single(errors!);
            Assert.Equal("InjectedFailure", error.Code);
            Assert.Equal("Injected second participant failure", error.Description);
        }
        else
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        using var final = site.Services.CreateScope();
        var saved = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(x => x.ContestId == contest.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, saved.Count);
        Assert.All(saved, participant => Assert.False(participant.IsApproved));
    }

    [Fact]
    public async Task ParticipantFlagsCanBeChangedAndParticipantDeleted()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var participant = await AddParticipantAsync(contest.Id);
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        await service.SetApprovalAsync(contest.Id, participant.Id, true, token);
        await service.SetOutOfCompetitionAsync(contest.Id, participant.Id, true, token);
        await service.SetDisqualificationAsync(contest.Id, participant.Id, true, token);
        using (var verification = site.Services.CreateScope())
        {
            var saved = await verification.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .SingleAsync(x => x.Id == participant.Id, token);
            Assert.True(saved.IsApproved);
            Assert.True(saved.IsOutOfCompetition);
            Assert.True(saved.IsDisqualified);
        }
        await service.SetApprovalAsync(contest.Id, participant.Id, false, token);
        await service.SetOutOfCompetitionAsync(contest.Id, participant.Id, false, token);
        await service.SetDisqualificationAsync(contest.Id, participant.Id, false, token);
        using (var verification = site.Services.CreateScope())
        {
            var saved = await verification.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .SingleAsync(x => x.Id == participant.Id, token);
            Assert.False(saved.IsApproved);
            Assert.False(saved.IsOutOfCompetition);
            Assert.False(saved.IsDisqualified);
        }
        await service.DeleteAsync(contest.Id, participant.Id, token);
        using var final = site.Services.CreateScope();
        Assert.False(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(x => x.Id == participant.Id, token));
    }

    [Fact]
    public async Task DeletingParticipantPreservesMemberAndTheirOtherRegistration()
    {
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
            await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>().DeleteAsync(contest.Id, participant.Id, token);

        using var final = site.Services.CreateScope();
        var saved = final.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await saved.Competitors.AnyAsync(x => x.Id == participant.Id, token));
        var remainingMember = await saved.OlympUsers.Include(x => x.Memberships).SingleAsync(x => x.Id == member.Id, token);
        Assert.Equal(otherParticipant.Id, Assert.Single(remainingMember.Memberships).Id);
    }

    [Theory]
    [InlineData(RoleNames.Jury, "school", "ru", "Удалено участников: 2.", "Удалено участников: 0.")]
    [InlineData(RoleNames.Admin, "acm", "en", "Deleted participants: 2.", "Deleted participants: 0.")]
    public async Task DeletingWithoutSubmissionsPreservesHistoryMembersAndOtherContests(
        string role, string prefix, string culture, string status, string repeatedStatus)
    {
        var token = TestContext.Current.CancellationToken;
        var manager = await site.AddUserAsync(role);
        var contest = await site.AddContestAsync(role == RoleNames.Jury ? manager : null, prefix);
        var otherContest = await site.AddContestAsync();
        var member = await site.AddUserAsync();
        var empty = await AddParticipantAsync(contest.Id);
        var hidden = await AddParticipantAsync(contest.Id);
        var submitted = await AddParticipantAsync(contest.Id);
        var messaged = await AddParticipantAsync(contest.Id);
        var other = await AddParticipantAsync(otherContest.Id);

        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Contests.Where(x => x.Id == contest.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndTime, DateTimeOffset.Now.AddHours(-1)), token);
            await db.Competitors.Where(x => x.Id == hidden.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, true), token);
            db.Attach(member);
            var participants = await db.Competitors.AsTracking().Include(x => x.Members)
                .Where(x => x.Id == empty.Id || x.Id == other.Id).ToListAsync(token);
            foreach (var participant in participants)
                participant.Members.Add(member);
            db.Messages.Add(new Message
            {
                ContestId = contest.Id,
                Contest = null!,
                UserId = messaged.Id,
                UserText = "Question",
                JuryText = "Answer"
            });
            db.Messages.Add(new Message { ContestId = contest.Id, Contest = null!, JuryText = "Announcement" });
            await db.SaveChangesAsync(token);
            var users = setup.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.AddClaimAsync(participants.Single(x => x.Id == empty.Id),
                new(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
            await ContestManagementTests.ImportAsync(setup.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
            var problem = await db.Problems.SingleAsync(x => x.ContestId == contest.Id, token);
            await ContestManagementTests.AddSubmissionsAsync(db, submitted.Id, problem, [-1]);
        }

        using var client = await site.LoginAsync(manager);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        Assert.Contains("DeleteWithoutSubmissions", await client.GetStringAsync(path, token));
        using var response = await SiteFixture.PostAsync(client, path + "?handler=DeleteWithoutSubmissions", []);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(status, WebUtility.HtmlDecode(await client.GetStringAsync(path, token)));
        using var repeated = await SiteFixture.PostAsync(client, path + "?handler=DeleteWithoutSubmissions", []);
        Assert.Equal(HttpStatusCode.Redirect, repeated.StatusCode);
        Assert.Contains(repeatedStatus, WebUtility.HtmlDecode(await client.GetStringAsync(path, token)));

        using var final = site.Services.CreateScope();
        var saved = final.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal([submitted.Id, messaged.Id], await saved.Competitors.Where(x => x.ContestId == contest.Id)
            .OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token));
        var savedMember = await saved.OlympUsers.Include(x => x.Memberships).SingleAsync(x => x.Id == member.Id, token);
        Assert.Equal(other.Id, Assert.Single(savedMember.Memberships).Id);
        Assert.False(await saved.UserClaims.AnyAsync(x => x.UserId == empty.Id || x.UserId == hidden.Id, token));
        Assert.Equal("Question", (await saved.Messages.SingleAsync(x => x.UserId == messaged.Id, token)).UserText);
        Assert.Equal(2, await saved.Messages.CountAsync(x => x.ContestId == contest.Id, token));
        Assert.True(await saved.Submissions.AnyAsync(x => x.UserId == submitted.Id, token));
        Assert.True(await saved.CheckResults.AnyAsync(x => x.Submission.UserId == submitted.Id, token));
    }

    [Theory]
    [InlineData("ru", "Удалять участников без отправок можно только после окончания соревнования.")]
    [InlineData("en", "Participants without submissions can only be deleted after the contest has ended.")]
    public async Task DeletingWithoutSubmissionsIsUnavailableUntilContestEnds(string culture, string error)
    {
        var token = TestContext.Current.CancellationToken;
        var manager = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(manager);
        var participant = await AddParticipantAsync(contest.Id);
        using var client = await site.LoginAsync(manager);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        Assert.DoesNotContain("DeleteWithoutSubmissions", await client.GetStringAsync(path, token));
        using var response = await SiteFixture.PostAsync(client, path + "?handler=DeleteWithoutSubmissions", []);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync(token));

        using var final = site.Services.CreateScope();
        Assert.True(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(x => x.Id == participant.Id, token));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task DeletingWithoutSubmissionsRollsBackEarlierDeletions(string failure)
    {
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
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteWithoutSubmissionsAsync(contest.Id, cancellation.Token));
            else if (failure == "exception")
                await Assert.ThrowsAsync<DbUpdateException>(() => service.DeleteWithoutSubmissionsAsync(contest.Id, cancellation.Token));
            else
                await Assert.ThrowsAsync<IdentityOperationException>(() => service.DeleteWithoutSubmissionsAsync(contest.Id, cancellation.Token));
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
            .DeleteWithoutSubmissionsAsync(contest.Id, token));
    }

    [Fact]
    public async Task ApproveAllPreservesAlreadyApprovedParticipantsAndDoesNotRepeatWrites()
    {
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
        await service.ApproveAllAsync(contest.Id, token);
        var afterFirst = await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(x => x.ContestId == contest.Id).ToDictionaryAsync(x => x.Id, token);
        Assert.All(afterFirst.Values, participant => Assert.True(participant.IsApproved));
        Assert.Equal(approved.ConcurrencyStamp, afterFirst[approved.Id].ConcurrencyStamp);

        await service.ApproveAllAsync(contest.Id, token);
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
            ? service.ApproveAllAsync(contest.Id, token)
            : service.ImportAsync(contest.Id, ["First", "Second"], token);

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
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var foreign = await site.AddContestAsync();
        var participant = await AddParticipantAsync(foreign.Id);
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        foreach (var operation in new Func<Task>[]
        {
            () => service.SetApprovalAsync(contest.Id, participant.Id, true, token),
            () => service.SetOutOfCompetitionAsync(contest.Id, participant.Id, true, token),
            () => service.SetDisqualificationAsync(contest.Id, participant.Id, true, token),
            () => service.DeleteAsync(contest.Id, participant.Id, token)
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
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
        var first = await service.ImportAsync(contest.Id, ["First", "Second"], token);
        Assert.Equal(new ParticipantImportSummary(2, 0), first);
        var repeated = await service.ImportAsync(contest.Id, ["first", "Second", "Third"], token);
        Assert.Equal(new ParticipantImportSummary(1, 2), repeated);
        await Assert.ThrowsAsync<OperationValidationException>(() =>
            service.ImportAsync(contest.Id, ["Fourth", "fourth"], token));
        using var final = site.Services.CreateScope();
        var names = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .Where(x => x.ContestId == contest.Id).Select(x => x.Name).OrderBy(x => x)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["First", "Second", "Third"], names);
    }

    [Fact]
    public async Task AssigningLoginsPreservesExistingAccountsAndDoesNotConsumeInput()
    {
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
            await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>().AssignLoginsAsync(contest.Id, logins, token);
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

        await service.ImportAsync(contest.Id, ["First", "Second"], token);
        var firstId = await db.Competitors.Where(x => x.ContestId == contest.Id && x.Name == "First")
            .Select(x => x.Id).SingleAsync(token);
        var first = await db.Competitors.AsTracking().SingleAsync(x => x.Id == firstId, token);
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>()
            .AddClaimAsync(first, new("Unrelated", "keep"))).Succeeded);
        await service.SetApprovalAsync(contest.Id, firstId, false, token);
        await service.ApproveAllAsync(contest.Id, token);
        Assert.Equal(new ParticipantImportSummary(1, 1), await service.ImportAsync(contest.Id, ["First", "Third"], token));

        await service.AssignLoginsAsync(contest.Id, logins, token);
        var trackingBehavior = db.ChangeTracker.QueryTrackingBehavior;
        await service.RemoveLoginsAsync(contest.Id, token);
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

        await service.AssignLoginsAsync(contest.Id, logins, token);
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
        await service.SetOutOfCompetitionAsync(contest.Id, late.Id, true, token);
        await service.SetApprovalAsync(contest.Id, earlyTie.Id, false, token);
        var prefix = Guid.NewGuid().ToString("N");
        await service.AssignLoginsAsync(contest.Id, new Dictionary<string, string>
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

    [Fact]
    public async Task UploadedLoginsTrimWhitespaceFromLoginAndPassword()
    {
        var token = TestContext.Current.CancellationToken;
        var manager = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(manager);
        var participant = await AddParticipantAsync(contest.Id);
        var login = "Team" + Guid.NewGuid().ToString("N");
        using var client = await site.LoginAsync(manager);
        var content = System.Text.Encoding.UTF8.GetBytes($"  {login}  ,  Password123  \n");
        using var response = await SiteFixture.UploadAsync(client,
            $"/Contests/Competitors/Manage/{contest.Id}?handler=UploadLogins", "LoginsFile", content, "logins.csv");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var final = site.Services.CreateScope();
        var saved = await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .SingleAsync(x => x.Id == participant.Id, token);
        Assert.Equal(login, saved.UserName);
        Assert.Equal("Password123", saved.PasswordHash);
    }

    [Theory]
    [InlineData("ru", "team01", "В файле повторяются логины.")]
    [InlineData("ru", " team01 ", "В файле повторяются логины.")]
    [InlineData("ru", "TEAM01", "В файле повторяются логины.")]
    [InlineData("en", "team01", "The file contains duplicate logins.")]
    [InlineData("en", " team01 ", "The file contains duplicate logins.")]
    [InlineData("en", "TEAM01", "The file contains duplicate logins.")]
    public async Task DuplicateLoginsUseRequestCultureAndLeaveCredentialsUnchanged(string culture, string duplicate, string error)
    {
        var token = TestContext.Current.CancellationToken;
        var manager = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(manager);
        await AddParticipantAsync(contest.Id);
        await AddParticipantAsync(contest.Id);
        using var client = await site.LoginAsync(manager);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var content = System.Text.Encoding.UTF8.GetBytes($"team01,Password123\n{duplicate},OtherPassword123");
        using var response = await SiteFixture.UploadAsync(client,
            $"/Contests/Competitors/Manage/{contest.Id}?handler=UploadLogins", "LoginsFile", content, "logins.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync(token));

        using var final = site.Services.CreateScope();
        var db = final.ServiceProvider.GetRequiredService<OlympContext>();
        var participants = await db.Competitors.Where(x => x.ContestId == contest.Id).ToListAsync(token);
        Assert.Equal(2, participants.Count);
        Assert.All(participants, participant =>
        {
            Assert.Null(participant.UserName);
            Assert.Null(participant.PasswordHash);
        });
        var participantIds = participants.Select(x => x.Id).ToArray();
        Assert.False(await db.UserClaims.AnyAsync(x => participantIds.Contains(x.UserId), token));
    }

    [Theory]
    [InlineData("en", "The file contains too few logins.")]
    [InlineData("ru", "В файле недостаточно логинов.")]
    public async Task TooFewLoginsUsesRequestCultureAndLeavesAllParticipantsWithoutCredentials(string culture, string error)
    {
        var token = TestContext.Current.CancellationToken;
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        await AddParticipantAsync(contest.Id);
        await AddParticipantAsync(contest.Id);

        using var client = await site.LoginAsync(owner);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await SiteFixture.UploadAsync(client,
            $"/Contests/Competitors/Manage/{contest.Id}?handler=UploadLogins", "LoginsFile",
            "single-login,Password123"u8.ToArray(), "logins.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync(token));

        using var final = site.Services.CreateScope();
        var db = final.ServiceProvider.GetRequiredService<OlympContext>();
        var saved = await db.Competitors.Where(x => x.ContestId == contest.Id).ToListAsync(token);
        Assert.Equal(2, saved.Count);
        Assert.All(saved, participant =>
        {
            Assert.Null(participant.UserName);
            Assert.Null(participant.PasswordHash);
        });
        var ids = saved.Select(x => x.Id).ToArray();
        Assert.False(await db.UserClaims.AnyAsync(x => ids.Contains(x.UserId), token));
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
            ? service.RemoveLoginsAsync(contest.Id, cancellationToken)
            : service.AssignLoginsAsync(contest.Id, logins, cancellationToken);

        if (removing)
        {
            using var setup = site.Services.CreateScope();
            await setup.ServiceProvider.GetRequiredService<CompetitorManagementService>().AssignLoginsAsync(contest.Id, logins, token);
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

    [Fact]
    public async Task GeneratedLoginsAllowSignInAndCanBeRemovedThroughPage()
    {
        var token = TestContext.Current.CancellationToken;
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        var participant = await AddParticipantAsync(contest.Id);
        using (var setup = site.Services.CreateScope())
        {
            await setup.ServiceProvider.GetRequiredService<CompetitorManagementService>().ApproveAllAsync(contest.Id, token);
            await setup.ServiceProvider.GetRequiredService<OlympContext>().Contests.Where(x => x.Id == contest.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, false), token);
        }

        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=CreateRandomLogins", [])).StatusCode);
        using (var verification = site.Services.CreateScope())
            participant = await verification.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .SingleAsync(x => x.Id == participant.Id, token);
        Assert.Equal("olymp001", participant.UserName);
        Assert.Matches("^[A-Za-z0-9]{10}$", participant.PasswordHash!);
        using var participantClient = await site.LoginAsync(participant, participant.PasswordHash!);
        Assert.Equal(HttpStatusCode.OK, (await participantClient.GetAsync($"/Contests/Submits/{contest.Id}", token)).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=RemoveLogins", [])).StatusCode);
        using (var verification = site.Services.CreateScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
            var saved = await db.Competitors.SingleAsync(x => x.Id == participant.Id, token);
            Assert.Null(saved.UserName);
            Assert.Null(saved.PasswordHash);
            Assert.False(await db.UserClaims.AnyAsync(x => x.UserId == participant.Id && x.ClaimType == ClaimNames.CompetitorContest, token));
        }
        using var freshClient = site.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await SiteFixture.PostAsync(freshClient, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = participant.UserName!,
            ["Input.Password"] = participant.PasswordHash!
        })).StatusCode);
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
