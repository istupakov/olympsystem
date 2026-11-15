using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Olymp.IntegrationTests.Shared;

internal static class TestHostConfiguration
{
    public static void Configure(IWebHostBuilder builder, string connectionString)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["Checker:FakeRunners"] = "false",
                ["MailService:From"] = "tests@example.test",
                ["MailService:SmtpServer"] = "localhost",
                ["MailService:Port"] = "587",
                ["MailService:UserName"] = "unused",
                ["MailService:Password"] = "unused"
            }));
    }
}
