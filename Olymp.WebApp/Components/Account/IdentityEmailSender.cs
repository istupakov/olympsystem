using System.Globalization;

using Microsoft.AspNetCore.Identity;

using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Mail;

namespace Olymp.WebApp.Components.Account;

internal sealed class IdentityEmailSender(IEmailSender emailSender) : IEmailSender<User>
{
    public Task SendConfirmationLinkAsync(User user, string email, string confirmationLink) =>
        emailSender.SendEmailAsync(email, AccountResources.ConfirmationEmailSubject,
            string.Format(CultureInfo.CurrentCulture, AccountResources.ConfirmationEmailBody, confirmationLink));

    public Task SendPasswordResetLinkAsync(User user, string email, string resetLink) =>
        emailSender.SendEmailAsync(email, AccountResources.PasswordResetEmailSubject,
            string.Format(CultureInfo.CurrentCulture, AccountResources.PasswordResetEmailBody, resetLink));

    public Task SendPasswordResetCodeAsync(User user, string email, string resetCode) =>
        emailSender.SendEmailAsync(email, AccountResources.PasswordResetEmailSubject,
            string.Format(CultureInfo.CurrentCulture, AccountResources.PasswordResetCodeEmailBody, resetCode));
}
