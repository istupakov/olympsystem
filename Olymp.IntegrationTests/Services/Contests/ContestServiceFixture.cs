using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public sealed class ContestServiceFixture : WebApplicationFactory<Olymp.WebApp.Components.App>, IAsyncLifetime
{
    public const string Password = "ValidTest123";
    private readonly string _connection;

    public ContestServiceFixture()
    {
        var original = Environment.GetEnvironmentVariable("OLYMP_TEST_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException("Set OLYMP_TEST_CONNECTION to a SQL Server connection allowed to create a temporary test database.");
        _connection = new SqlConnectionStringBuilder(original)
        {
            InitialCatalog = "OlympTests_" + Guid.NewGuid().ToString("N"),
            TrustServerCertificate = true
        }.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _connection,
            ["MailService:From"] = "tests@example.test",
            ["MailService:SmtpServer"] = "localhost",
            ["MailService:Port"] = "587",
            ["MailService:UserName"] = "unused",
            ["MailService:Password"] = "unused",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "None"
        }));
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.AddOptions<ContestImportOptions>();
            services.AddScoped<ContestProblemService>();
            services.RemoveAll<DbContextOptions<OlympContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OlympContext>>();
            services.AddDbContextFactory<OlympContext>(options => options.UseSqlServer(_connection)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution));
        });
    }

    public async ValueTask InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        // This fixture only creates/deletes its own randomly named database.
        Assert.StartsWith("OlympTests_", db.Database.GetDbConnection().Database);
        await db.Database.EnsureCreatedAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        foreach (var name in new[] { RoleNames.Admin, RoleNames.Jury })
            Assert.True((await roles.CreateAsync(new(name))).Succeeded);
        await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT Checkers ON; INSERT INTO Checkers (Id, Description) VALUES (4, N'Strict'); SET IDENTITY_INSERT Checkers OFF;");
    }

    public override async ValueTask DisposeAsync()
    {
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            if (db.Database.GetDbConnection().Database == new SqlConnectionStringBuilder(_connection).InitialCatalog)
                await db.Database.EnsureDeletedAsync();
        }
        await base.DisposeAsync();
    }

    public async Task<OlympUser> AddUserAsync(string? role = null)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var name = Guid.NewGuid().ToString("N") + "@example.test";
        var user = new OlympUser
        {
            Name = name,
            UserName = name,
            Email = name,
            EmailConfirmed = true,
            IsHidden = role is not null,
            Memberships = [],
            Teams = [],
            Messages = [],
            Submissions = []
        };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join(";", created.Errors.Select(x => x.Description)));
        if (role is not null)
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    public async Task<Contest> AddContestAsync(User? owner = null, string? prefix = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = new Contest
        {
            Name = "Контест",
            Abbr = (prefix ?? "school") + Guid.NewGuid().ToString("N"),
            StartTime = DateTimeOffset.Now.AddHours(-1),
            EndTime = DateTimeOffset.Now.AddHours(4),
            FreezeTime = DateTimeOffset.Now,
            IsHidden = true,
            Problems = [],
            Messages = [],
            Competitors = []
        };
        db.Contests.Add(contest);
        await db.SaveChangesAsync();
        if (owner is not null)
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await users.FindByIdAsync(owner.Id.ToString()))!;
            Assert.True((await users.AddClaimAsync(user, new(ClaimNames.ContestManage, contest.Id.ToString()))).Succeeded);
        }
        return contest;
    }

}
