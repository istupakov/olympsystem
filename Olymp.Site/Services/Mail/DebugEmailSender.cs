using System.Net;

using Olymp.Site.IdentityUI;

namespace Olymp.Site.Services.Mail;

public class DebugEmailSender : IEmailSender
{
    private readonly ILogger _logger;

    public DebugEmailSender(ILogger<DebugEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendEmailAsync(string toEmail, string subject, string message)
    {
        _logger.LogDebug($"{toEmail} {subject} {WebUtility.HtmlDecode(message)}");
        return Task.CompletedTask;
    }
}
