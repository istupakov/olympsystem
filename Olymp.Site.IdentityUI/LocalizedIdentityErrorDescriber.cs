using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace Olymp.Site.IdentityUI;

#nullable disable

public class LocalizedIdentityErrorDescriber(IStringLocalizer<IdentityUIResources> localizer) : IdentityErrorDescriber
{
    private readonly IStringLocalizer<IdentityUIResources> _localizer = localizer;

    public override IdentityError DefaultError() => new() { Code = nameof(DefaultError), Description = string.Format(_localizer["An unknown failure has occurred."]) };
    public override IdentityError ConcurrencyFailure() => new() { Code = nameof(ConcurrencyFailure), Description = string.Format(_localizer["Optimistic concurrency failure, object has been modified."]) };
    public override IdentityError PasswordMismatch() => new() { Code = nameof(PasswordMismatch), Description = string.Format(_localizer["Incorrect password."]) };
    public override IdentityError InvalidToken() => new() { Code = nameof(InvalidToken), Description = string.Format(_localizer["Invalid token."]) };
    public override IdentityError LoginAlreadyAssociated() => new() { Code = nameof(LoginAlreadyAssociated), Description = string.Format(_localizer["A user with this login already exists."]) };
    public override IdentityError InvalidUserName(string userName) => new() { Code = nameof(InvalidUserName), Description = string.Format(_localizer["User name '{0}' is invalid, can only contain letters or digits."], userName) };
    public override IdentityError InvalidEmail(string email) => new() { Code = nameof(InvalidEmail), Description = string.Format(_localizer["Email '{0}' is invalid."], email) };
    public override IdentityError DuplicateUserName(string userName) => new() { Code = nameof(DuplicateUserName), Description = string.Format(_localizer["User Name '{0}' is already taken."], userName) };
    public override IdentityError DuplicateEmail(string email) => new() { Code = nameof(DuplicateEmail), Description = string.Format(_localizer["Email '{0}' is already taken."], email) };
    public override IdentityError InvalidRoleName(string role) => new() { Code = nameof(InvalidRoleName), Description = string.Format(_localizer["Role name '{0}' is invalid."], role) };
    public override IdentityError DuplicateRoleName(string role) => new() { Code = nameof(DuplicateRoleName), Description = string.Format(_localizer["Role name '{0}' is already taken."], role) };
    public override IdentityError UserAlreadyHasPassword() => new() { Code = nameof(UserAlreadyHasPassword), Description = string.Format(_localizer["User already has a password set."]) };
    public override IdentityError UserLockoutNotEnabled() => new() { Code = nameof(UserLockoutNotEnabled), Description = string.Format(_localizer["Lockout is not enabled for this user."]) };
    public override IdentityError UserAlreadyInRole(string role) => new() { Code = nameof(UserAlreadyInRole), Description = string.Format(_localizer["User already in role '{0}'."], role) };
    public override IdentityError UserNotInRole(string role) => new() { Code = nameof(UserNotInRole), Description = string.Format(_localizer["User is not in role '{0}'."], role) };
    public override IdentityError PasswordTooShort(int length) => new() { Code = nameof(PasswordTooShort), Description = string.Format(_localizer["Passwords must be at least {0} characters."], length) };
    public override IdentityError PasswordRequiresNonAlphanumeric() => new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = string.Format(_localizer["Passwords must have at least one non alphanumeric character."]) };
    public override IdentityError PasswordRequiresDigit() => new() { Code = nameof(PasswordRequiresDigit), Description = string.Format(_localizer["Passwords must have at least one digit ('0'-'9')."]) };
    public override IdentityError PasswordRequiresLower() => new() { Code = nameof(PasswordRequiresLower), Description = string.Format(_localizer["Passwords must have at least one lowercase ('a'-'z')."]) };
    public override IdentityError PasswordRequiresUpper() => new() { Code = nameof(PasswordRequiresUpper), Description = string.Format(_localizer["Passwords must have at least one uppercase ('A'-'Z')."]) };
}
