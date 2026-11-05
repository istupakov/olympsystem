using Microsoft.AspNetCore.Identity;

using Olymp.Domain.Models;
using Olymp.Site.IdentityUI;

namespace Olymp.WebApp.Components.Account;

internal sealed class IdentityEmailSender(IEmailSender emailSender) : IEmailSender<User>
{
    public Task SendConfirmationLinkAsync(User user, string email, string confirmationLink) =>
        emailSender.SendEmailAsync(email, "Confirm your email", $"Please confirm your account by <a href='{confirmationLink}'>clicking here</a>. If you didn't request this email confirmation, you can ignore this email.");

    public Task SendPasswordResetLinkAsync(User user, string email, string resetLink) =>
        emailSender.SendEmailAsync(email, "Reset your password", $"Please reset your password by <a href='{resetLink}'>clicking here</a>. If you didn't request a password reset, you can ignore this email.");

    public Task SendPasswordResetCodeAsync(User user, string email, string resetCode) =>
        emailSender.SendEmailAsync(email, "Reset your password", $"Please reset your password using the following code: {resetCode}. If you didn't request a password reset, you can ignore this email.");
}
