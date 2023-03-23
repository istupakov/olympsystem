using Microsoft.AspNetCore.Identity;

using Olymp.Domain.Models;

namespace Olymp.Site.Services.Identity;

public class OlympUserConfirmation : DefaultUserConfirmation<User>
{
    public override Task<bool> IsConfirmedAsync(UserManager<User> manager, User user)
    {
        if (user is Competitor comp)
            return Task.FromResult(comp.IsApproved && !comp.IsDisqualified);

        return base.IsConfirmedAsync(manager, user);
    }
}
