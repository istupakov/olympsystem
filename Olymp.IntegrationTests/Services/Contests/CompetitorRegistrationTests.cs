using System.Data.Common;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Shared;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public class CompetitorRegistrationTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>
{
    [Theory]
    [InlineData("hidden")]
    [InlineData("team")]
    [InlineData("ended")]
    [InlineData("firstName")]
    [InlineData("lastName")]
    [InlineData("organization")]
    [InlineData("group")]
    public async Task RegistrationRejectsUnavailableContestOrIncompleteProfile(string invalid)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, _) = await SetupAsync();
        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsHidden, invalid == "hidden")
                .SetProperty(x => x.Abbr, invalid == "team" ? "acm" + contest.Abbr : contest.Abbr)
                .SetProperty(x => x.EndTime, invalid == "ended" ? DateTimeOffset.Now.AddHours(-1) : contest.EndTime), token);
            await db.OlympUsers.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.FirstName, invalid == "firstName" ? null : "Иван")
                .SetProperty(x => x.LastName, invalid == "lastName" ? null : "Иванов")
                .SetProperty(x => x.OrganizationId, x => invalid == "organization" ? null : x.OrganizationId)
                .SetProperty(x => x.Details, invalid == "group" ? null : "A1"), token);
        }

        using (var scope = site.Services.CreateScope())
            await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider
                .GetRequiredService<CompetitorManagementService>().RegisterIndividualAsync(contest.Id, user.Id, false, token));
        using var final = site.Services.CreateScope();
        Assert.False(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(x => x.ContestId == contest.Id, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterSavingRegistrationRollsBackParticipantAndMembership(bool team)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync(team);
        Task RegisterAsync(CompetitorManagementService service, CancellationToken cancellationToken) => team
            ? service.RegisterTeamAsync(contest.Id, orgId, user.Id, "NSTU", [user.Id.ToString(), "Второй", "Третий"], cancellationToken)
            : service.RegisterIndividualAsync(contest.Id, user.Id, false, cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var interceptor = new CancelAfterRegistrationSaved(cancellation);
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContextFactory<OlympContext>(options => options.AddInterceptors(interceptor))));
        using (var scope = factory.Services.CreateScope())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RegisterAsync(scope.ServiceProvider.GetRequiredService<CompetitorManagementService>(), cancellation.Token));

        Assert.True(interceptor.RegistrationSaved);
        using (var verification = site.Services.CreateScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == contest.Id, token));
            Assert.False(await db.OlympUsers.Where(x => x.Id == user.Id).SelectMany(x => x.Memberships)
                .AnyAsync(x => x.ContestId == contest.Id, token));
        }

        using var retry = site.Services.CreateScope();
        await RegisterAsync(retry.ServiceProvider.GetRequiredService<CompetitorManagementService>(), token);
    }

    [Theory]
    [InlineData("hiddenMember")]
    [InlineData("foreignMember")]
    [InlineData("duplicateMember")]
    [InlineData("missingMember")]
    [InlineData("separatorInName")]
    [InlineData("hiddenContest")]
    [InlineData("individualContest")]
    [InlineData("endedContest")]
    public async Task TeamRegistrationRejectsInvalidCompositionOrUnavailableContest(string invalid)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync(team: true);
        using (var setup = site.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OlympContext>();
            await db.OlympUsers.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsHidden, invalid == "hiddenMember")
                .SetProperty(x => x.OrganizationId, x => invalid == "foreignMember" ? null : x.OrganizationId), token);
            await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsHidden, invalid == "hiddenContest")
                .SetProperty(x => x.Abbr, invalid == "individualContest" ? "school-test" : contest.Abbr)
                .SetProperty(x => x.EndTime, invalid == "endedContest" ? DateTimeOffset.Now.AddHours(-1) : contest.EndTime), token);
        }

        string[] slots = invalid switch
        {
            "duplicateMember" => [user.Id.ToString(), "000" + user.Id, "Третий"],
            "missingMember" => [user.Id.ToString(), "Второй"],
            "separatorInName" => [user.Id.ToString(), "Второй;Третий", "Четвёртый"],
            _ => [user.Id.ToString(), "Второй", "Третий"]
        };
        using (var scope = site.Services.CreateScope())
            await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider.GetRequiredService<CompetitorManagementService>()
                .RegisterTeamAsync(contest.Id, orgId, user.Id, "NSTU", slots, token));
        using var final = site.Services.CreateScope();
        Assert.False(await final.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(x => x.ContestId == contest.Id, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRegistrationCannotEnrollMemberTwice(bool team)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync(team);
        var barrier = new SqlReadBarrier("[Participations]");
        using var factory = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContextFactory<OlympContext>(options => options.AddInterceptors(barrier))));

        async Task<Exception?> RegisterAsync(string name)
        {
            using var scope = factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            return await Record.ExceptionAsync(() => team
                ? service.RegisterTeamAsync(contest.Id, orgId, user.Id, name, [user.Id.ToString(), "Второй", "Третий"], token)
                : service.RegisterIndividualAsync(contest.Id, user.Id, false, token));
        }

        var errors = await Task.WhenAll(RegisterAsync("First"), RegisterAsync("Second"))
            .WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.Equal(2, barrier.Arrivals);
        Assert.Single(errors, error => error is null);
        var failure = Assert.Single(errors, error => error is not null)!;
        Assert.True(failure is OperationValidationException || failure.GetBaseException() is DbException, failure.ToString());
        using var verification = site.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<OlympContext>();
        var participant = Assert.Single(await db.Competitors.Include(x => x.Members)
            .Where(x => x.ContestId == contest.Id).ToListAsync(token));
        Assert.Equal(user.Id, Assert.Single(participant.Members).Id);
    }

    [Theory]
    [InlineData("ru", false, "уже зарегистрированы")]
    [InlineData("en", false, "already registered")]
    [InlineData("ru", true, "уже зарегистрирован")]
    [InlineData("en", true, "already registered")]
    public async Task RepeatedRegistrationReportsLocalizedErrorWithoutChangingMembership(string culture, bool team, string expected)
    {
        var token = TestContext.Current.CancellationToken;
        var (user, contest, orgId) = await SetupAsync(team);
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Task RegisterAsync(CompetitorManagementService service) => team
                ? service.RegisterTeamAsync(contest.Id, orgId, user.Id, "NSTU", [user.Id.ToString(), "Второй", "Третий"], token)
                : service.RegisterIndividualAsync(contest.Id, user.Id, false, token);
            using (var initial = site.Services.CreateScope())
                await RegisterAsync(initial.ServiceProvider.GetRequiredService<CompetitorManagementService>());
            using (var retry = site.Services.CreateScope())
            {
                var error = await Assert.ThrowsAsync<OperationValidationException>(() =>
                    RegisterAsync(retry.ServiceProvider.GetRequiredService<CompetitorManagementService>()));
                Assert.Contains(expected, error.Message);
            }
            using var verification = site.Services.CreateScope();
            var participant = Assert.Single(await verification.ServiceProvider.GetRequiredService<OlympContext>()
                .Competitors.Include(x => x.Members).Where(x => x.ContestId == contest.Id).ToListAsync(token));
            Assert.Equal(user.Id, Assert.Single(participant.Members).Id);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    private async Task<(OlympUser User, Contest Contest, int OrgId)> SetupAsync(bool team = false)
    {
        var token = TestContext.Current.CancellationToken;
        var user = await site.AddUserAsync();
        var contest = await site.AddContestAsync(prefix: team ? "acm" : "school");
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var organization = new Organization { Name = "Test university", Users = [] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(token);
        await db.OlympUsers.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.FirstName, "Иван").SetProperty(x => x.LastName, "Иванов")
            .SetProperty(x => x.OrganizationId, organization.Id).SetProperty(x => x.Details, "A1"), token);
        await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.IsHidden, false).SetProperty(x => x.StartTime, DateTimeOffset.Now.AddHours(1)), token);
        return (user, contest, organization.Id);
    }

    private sealed class CancelAfterRegistrationSaved(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private bool _savingRegistration;
        public bool RegistrationSaved { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _savingRegistration = eventData.Context!.ChangeTracker.Entries<Competitor>().Any(x => x.State == EntityState.Added);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (_savingRegistration)
            {
                RegistrationSaved = true;
                cancellation.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }
}
