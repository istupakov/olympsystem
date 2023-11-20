using System.Net;

using Olymp.Site.IdentityUI;

namespace Olymp.Site.Services.Mail;

public class DebugEmailSender(ILogger<DebugEmailSender> logger) : IEmailSender
{
    private readonly ILogger _logger = logger;

    public Task SendEmailAsync(string toEmail, string subject, string message)
    {
        _logger.LogDebug("{toEmail} {subject} {message}", toEmail, subject, WebUtility.HtmlDecode(message));
        return Task.CompletedTask;
    }
}
