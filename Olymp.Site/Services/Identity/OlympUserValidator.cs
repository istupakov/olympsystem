using Microsoft.AspNetCore.Identity;

using Olymp.Domain.Models;

namespace Olymp.Site.Services.Identity;

public class OlympUserValidator(IdentityErrorDescriber? errors = null) : UserValidator<User>(errors)
{

    public override Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user)
    {
        if (user is Competitor)
            return Task.FromResult(IdentityResult.Success);

        return base.ValidateAsync(manager, user);
    }
}
