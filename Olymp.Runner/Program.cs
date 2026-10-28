using Microsoft.Extensions.Options;

using Olymp.Runner;
using Olymp.Runner.Windows;
using Olymp.Site.Protos;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<RunnerConnectionConfig>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .Validate(config =>
        config.RunnerServerAddress is { IsAbsoluteUri: true } address
        && address.Scheme == Uri.UriSchemeHttps,
        "RunnerServerAddress must be an absolute HTTPS URL.")
    .ValidateOnStart();
builder.Services.AddOptions<RunnerClientConfig>()
    .Bind(builder.Configuration.GetSection(RunnerClientConfig.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<RunnerClientService>();

builder.Services.AddGrpcClient<Runner.RunnerClient>((services, options) =>
            options.Address = services.GetRequiredService<IOptions<RunnerConnectionConfig>>().Value.RunnerServerAddress)
        .ConfigureChannel(options =>
        {
            options.MaxReceiveMessageSize = null;
            options.HttpHandler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(5)
            };
        }).AddCallCredentials((authContext, metadata, services) =>
        {
            var config = services.GetRequiredService<IOptions<RunnerConnectionConfig>>().Value;
            metadata.Add("Authorization", config.ApiKey);
            return Task.CompletedTask;
        });

if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
    builder.Services.AddTransient<IRestrictedProcessFactory, RestrictedWindowsProcessFactory>();

var host = builder.Build();
host.Run();
