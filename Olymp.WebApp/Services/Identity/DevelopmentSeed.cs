using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Services.Identity;

internal static class DevelopmentSeed
{
    public const string Email = "admin@example.test";
    public const string Password = "DevAdmin123!";

    public static async Task SeedAsync(OlympContext db, IServiceProvider services, CancellationToken token = default)
    {
        if (await db.Users.AnyAsync(token)) return;

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        using var scope = services.CreateScope();
        using var userStore = new UserStore<User, IdentityRole<int>, OlympContext, int>(db);
        using var roleStore = new RoleStore<IdentityRole<int>, OlympContext, int>(db);
        using var users = ActivatorUtilities.CreateInstance<UserManager<User>>(scope.ServiceProvider, userStore);
        using var roles = ActivatorUtilities.CreateInstance<RoleManager<IdentityRole<int>>>(scope.ServiceProvider, roleStore);
        foreach (string role in new[] { RoleNames.Admin, RoleNames.Jury })
            if (!await roles.RoleExistsAsync(role)) Check(await roles.CreateAsync(new(role)));
        var admin = new OlympUser
        {
            Name = "Development admin",
            UserName = Email,
            Email = Email,
            EmailConfirmed = true,
            RegistrationDate = DateTimeOffset.UtcNow,
            Messages = [],
            Submissions = [],
            Memberships = [],
            Teams = []
        };
        Check(await users.CreateAsync(admin, Password));
        Check(await users.AddToRoleAsync(admin, RoleNames.Admin));
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
    }
}
