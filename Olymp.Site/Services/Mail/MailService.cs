using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

using Olymp.Site.IdentityUI;

namespace Olymp.Site.Services.Mail;

public class MailServiceConfiguration
{
    public required string From { get; init; }
    public required string SmtpServer { get; init; }
    public required int Port { get; init; }
    public required string UserName { get; init; }
    public required string Password { get; init; }
}

public class MailService : IEmailSender
{
    private readonly ILogger _logger;
    private readonly MailServiceConfiguration _configuration;

    public MailService(ILogger<MailService> logger, IOptions<MailServiceConfiguration> mailServiceConfiguration)
    {
        _logger = logger;
        _configuration = mailServiceConfiguration.Value;
    }

    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var builder = new BodyBuilder { HtmlBody = htmlMessage };
        var message = new MimeMessage(
            new[] { MailboxAddress.Parse(_configuration.From) },
            new[] { MailboxAddress.Parse(email) },
            subject, builder.ToMessageBody());

        _logger.LogInformation("Sending an email to {email} with the subject '{subject}'", email, subject);
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
            _logger.LogInformation("Email to {to} sent successfully", mailMessage.To.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending email");
        }
        finally
        {
            await client.DisconnectAsync(true, cancellationToken);
        }
    }
}
