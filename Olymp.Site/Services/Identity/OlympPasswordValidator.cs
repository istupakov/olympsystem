using Microsoft.AspNetCore.Identity;

using Olymp.Domain.Models;

namespace Olymp.Site.Services.Identity;

public class OlympPasswordValidator : PasswordValidator<User>
{
    public OlympPasswordValidator(IdentityErrorDescriber? errors = null)
        : base(errors)
    {
    }

    public override Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user, string? password)
    {
        if (user is Competitor)
            return Task.FromResult(IdentityResult.Success);

        return base.ValidateAsync(manager, user, password);
    }
}
