namespace Olymp.WebApp.Services.Mail;

public interface IEmailSender
{
    Task SendEmailAsync(string email, string subject, string htmlMessage);
}
