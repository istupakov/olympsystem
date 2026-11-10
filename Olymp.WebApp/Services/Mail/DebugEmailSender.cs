using System.Net;

namespace Olymp.WebApp.Services.Mail;

public class DebugEmailSender(ILogger<DebugEmailSender> logger) : IEmailSender
{
    public Task SendEmailAsync(string toEmail, string subject, string message)
    {
        logger.LogDebug("{toEmail} {subject} {message}", toEmail, subject, WebUtility.HtmlDecode(message));
        return Task.CompletedTask;
    }
}
