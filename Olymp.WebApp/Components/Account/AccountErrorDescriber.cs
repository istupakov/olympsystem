using System.Globalization;

using Microsoft.AspNetCore.Identity;

using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Account;

internal sealed class AccountErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => new() { Code = nameof(DefaultError), Description = AccountResources.UnknownIdentityError };
    public override IdentityError ConcurrencyFailure() => new() { Code = nameof(ConcurrencyFailure), Description = AccountResources.ConcurrencyFailure };
    public override IdentityError PasswordMismatch() => new() { Code = nameof(PasswordMismatch), Description = AccountResources.IncorrectPassword };
    public override IdentityError InvalidToken() => new() { Code = nameof(InvalidToken), Description = AccountResources.InvalidToken };
    public override IdentityError InvalidUserName(string? userName) => new() { Code = nameof(InvalidUserName), Description = string.Format(CultureInfo.CurrentCulture, AccountResources.InvalidUserName, userName) };
    public override IdentityError InvalidEmail(string? email) => new() { Code = nameof(InvalidEmail), Description = string.Format(CultureInfo.CurrentCulture, AccountResources.InvalidEmail, email) };
    public override IdentityError DuplicateUserName(string userName) => new() { Code = nameof(DuplicateUserName), Description = string.Format(CultureInfo.CurrentCulture, AccountResources.DuplicateUserName, userName) };
    public override IdentityError DuplicateEmail(string email) => new() { Code = nameof(DuplicateEmail), Description = string.Format(CultureInfo.CurrentCulture, AccountResources.DuplicateEmail, email) };
    public override IdentityError PasswordTooShort(int length) => new() { Code = nameof(PasswordTooShort), Description = string.Format(CultureInfo.CurrentCulture, AccountResources.PasswordTooShort, length) };
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => new() { Code = nameof(PasswordRequiresUniqueChars), Description = string.Format(CultureInfo.CurrentCulture, AccountResources.PasswordRequiresUniqueChars, uniqueChars) };
    public override IdentityError PasswordRequiresNonAlphanumeric() => new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = AccountResources.PasswordRequiresNonAlphanumeric };
    public override IdentityError PasswordRequiresDigit() => new() { Code = nameof(PasswordRequiresDigit), Description = AccountResources.PasswordRequiresDigit };
    public override IdentityError PasswordRequiresLower() => new() { Code = nameof(PasswordRequiresLower), Description = AccountResources.PasswordRequiresLower };
    public override IdentityError PasswordRequiresUpper() => new() { Code = nameof(PasswordRequiresUpper), Description = AccountResources.PasswordRequiresUpper };
}
