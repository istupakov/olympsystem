using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components;
using Olymp.WebApp.Services.Identity;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

public class DevelopmentSeedTests
{
    [Theory]
    [InlineData("Development", false)]
    [InlineData("Development", true)]
    [InlineData("Production", true)]
    public async Task MigrationSeedsOnlyAnEmptyDevelopmentDatabase(string environment, bool asynchronous)
    {
        var token = TestContext.Current.CancellationToken;
        string configured = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")!;
        string connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "OlympSeedTests_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        using var host = new WebApplicationFactory<App>().WithWebHostBuilder(builder =>
        {
            TestHostConfiguration.Configure(builder, connection);
            builder.UseEnvironment(environment);
        });
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(new SqlConnectionStringBuilder(connection).InitialCatalog, db.Database.GetDbConnection().Database);
        Assert.Equal(environment, host.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        try
        {
            if (asynchronous) await db.Database.MigrateAsync(token);
            else db.Database.Migrate();
            if (environment == "Production")
            {
                Assert.False(await db.Users.AnyAsync(token));
                return;
            }
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.Equal(1, await db.Users.CountAsync(token));
            var admin = Assert.IsType<OlympUser>(await users.FindByEmailAsync(DevelopmentSeed.Email));
            Assert.True(await users.CheckPasswordAsync(admin, DevelopmentSeed.Password));
            Assert.True(await users.IsInRoleAsync(admin, RoleNames.Admin));
            Assert.Equal(new[] { RoleNames.Admin, RoleNames.Jury }, await db.Roles.OrderBy(role => role.Name).Select(role => role.Name).ToArrayAsync(token));
            Assert.True(await users.IsEmailConfirmedAsync(admin));
            Assert.Equal(admin.NormalizedEmail, admin.NormalizedUserName);
            admin.Name = "Keep this name";
            Assert.True((await users.UpdateAsync(admin)).Succeeded);
            await db.Database.MigrateAsync(token);
            Assert.Equal(1, await db.Users.CountAsync(token));
            Assert.Equal("Keep this name", (await users.FindByIdAsync(admin.Id.ToString()))!.Name);

            // Existing accounts must never trigger creation of a replacement dev admin.
            Assert.True((await users.DeleteAsync(admin)).Succeeded);
            var existing = new OlympUser
            {
                Name = "Existing user",
                UserName = "existing@example.test",
                Email = "existing@example.test",
                Messages = [],
                Submissions = [],
                Memberships = [],
                Teams = []
            };
            Assert.True((await users.CreateAsync(existing, "ExistingPassword123!")).Succeeded);
            await db.Database.MigrateAsync(token);
            Assert.Null(await users.FindByEmailAsync(DevelopmentSeed.Email));
            Assert.False(await users.IsInRoleAsync(existing, RoleNames.Admin));
        }
        finally { await db.Database.EnsureDeletedAsync(CancellationToken.None); }
    }
}
