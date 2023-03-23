using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Identity;

using Olymp.Domain.Models;

namespace Olymp.Site.Services.Identity;

public class OlympPasswordHasher : PasswordHasher<User>
{
    public override PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
    {
        if (user is Competitor)
        {
            return string.Equals(hashedPassword, providedPassword, StringComparison.Ordinal)
                ? PasswordVerificationResult.Success
                : PasswordVerificationResult.Failed;
        }

        // Legacy: hashing for old users
        if (hashedPassword.Split('|') is [string passwordHash, _, string salt])
        {
            return string.Equals(HashPassword(providedPassword, salt), passwordHash, StringComparison.CurrentCultureIgnoreCase)
                ? PasswordVerificationResult.SuccessRehashNeeded
                : PasswordVerificationResult.Failed;
        }

        return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    private static string HashPassword(string password, string salt)
    {
        var unencodedBytes = Encoding.Unicode.GetBytes(password);
        var saltBytes = Convert.FromBase64String(salt);
        var buffer = new byte[unencodedBytes.Length + saltBytes.Length];
        Buffer.BlockCopy(unencodedBytes, 0, buffer, 0, unencodedBytes.Length);
        Buffer.BlockCopy(saltBytes, 0, buffer, unencodedBytes.Length - 1, saltBytes.Length);
        var hashedBytes = SHA1.HashData(buffer);
        return Convert.ToBase64String(hashedBytes);
    }
}
