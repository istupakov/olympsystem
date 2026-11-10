using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.WebApp.Components;
using Olymp.WebApp.Services.Mail;

using Xunit;

namespace Olymp.IntegrationTests.Services;

public sealed class ServiceFactory : WebApplicationFactory<App>, IAsyncLifetime
{
    private readonly string _connectionString;
    private bool _databaseCreated;

    public ServiceFactory()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException(
                "Set ConnectionStrings__DefaultConnection to SQL Server. Tests create a separate temporary database.");
        var connection = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = $"OlympTests_{Guid.NewGuid():N}"
        };
        _connectionString = connection.ConnectionString;
    }

    public ServiceData Data { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["MailService:From"] = "tests@example.test",
                ["MailService:SmtpServer"] = "localhost",
                ["MailService:Port"] = "587",
                ["MailService:UserName"] = "unused",
                ["MailService:Password"] = "unused"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<OlympContext>>();
            services.AddDbContextFactory<OlympContext>(options => options.UseSqlServer(_connectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution));
            services.RemoveAll<IEmailSender>();
            services.AddTransient<IEmailSender, DebugEmailSender>();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
    }

    public async ValueTask InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        _databaseCreated = true;
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        Data = await ServiceData.SeedAsync(scope.ServiceProvider);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_databaseCreated)
            {
                await using var scope = Services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
                await context.Database.EnsureDeletedAsync(CancellationToken.None);
                _databaseCreated = false;
            }
        }
        finally
        {
            await base.DisposeAsync();
        }
    }

}
