using Microsoft.AspNet.Identity;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Web.Security;

namespace OlympSystem.Models
{
    /// <summary>
    /// For backward compatibility with YafMembershipProvider
    /// </summary>
    public class YafPasswordHasher: PasswordHasher
    {
        public override PasswordVerificationResult VerifyHashedPassword(string hashedPassword, string providedPassword)
        {
            string[] passwordProperties = hashedPassword.Split('|');
            if (passwordProperties.Length != 3)
            {
                return base.VerifyHashedPassword(hashedPassword, providedPassword);
            }
            else
            {
                string passwordHash = passwordProperties[0];
                string salt = passwordProperties[2];
                if (String.Equals(HashPassword(providedPassword, salt), passwordHash, StringComparison.CurrentCultureIgnoreCase))
                {
                    return PasswordVerificationResult.SuccessRehashNeeded;
                }
                else
                {
                    return PasswordVerificationResult.Failed;
                }
            }
        }

        private static string HashPassword(string password, string salt)
        {
            var unencodedBytes = Encoding.Unicode.GetBytes(password);
            var saltBytes = Convert.FromBase64String(salt);
            var buffer = new byte[unencodedBytes.Length + saltBytes.Length];
            Buffer.BlockCopy(unencodedBytes, 0, buffer, 0, unencodedBytes.Length);
            Buffer.BlockCopy(saltBytes, 0, buffer, unencodedBytes.Length - 1, saltBytes.Length);
            var hashedBytes = HashAlgorithm.Create("SHA1").ComputeHash(buffer);
            return Convert.ToBase64String(hashedBytes);
        }
    }
}