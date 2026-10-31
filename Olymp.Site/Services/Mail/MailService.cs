using System.ComponentModel.DataAnnotations;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

using Olymp.Site.IdentityUI;

namespace Olymp.Site.Services.Mail;

public class MailServiceConfiguration
{
    [Required]
    public required string From { get; init; }

    [Required]
    public required string SmtpServer { get; init; }

    [Range(1, 65535)]
    public required int Port { get; init; }

    [Required]
    public required string UserName { get; init; }

    [Required]
    public required string Password { get; init; }
}

public class MailService(ILogger<MailService> logger, IOptions<MailServiceConfiguration> mailServiceConfiguration) : IEmailSender
{
    private readonly MailServiceConfiguration _configuration = mailServiceConfiguration.Value;

    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var builder = new BodyBuilder { HtmlBody = htmlMessage };
        var message = new MimeMessage(
            [MailboxAddress.Parse(_configuration.From)],
            [MailboxAddress.Parse(email)],
            subject, builder.ToMessageBody());

        logger.LogInformation("Sending an email to {email} with the subject '{subject}'", email, subject);
        _ = Task.Run(() => SendAsync(message, CancellationToken.None));
        return Task.CompletedTask;
    }

    private async Task SendAsync(MimeMessage mailMessage, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(_configuration.SmtpServer, _configuration.Port, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(_configuration.UserName, _configuration.Password, cancellationToken);
            await client.SendAsync(mailMessage, cancellationToken);
            logger.LogInformation("Email to {to} sent successfully", mailMessage.To.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending email");
        }
        finally
        {
            await client.DisconnectAsync(true, cancellationToken);
        }
    }
}
