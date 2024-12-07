using Olymp.Runner;
using Olymp.Runner.Windows;
using Olymp.Site.Protos;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<RunnerClientConfig>(builder.Configuration.GetSection(RunnerClientConfig.Section));
builder.Services.AddHostedService<RunnerClientService>();

builder.Services.AddGrpcClient<Runner.RunnerClient>(options =>
        {
            options.Address = new Uri(builder.Configuration["RunnerServerAddress"]!);
        }).ConfigureChannel(options =>
        {
            options.MaxReceiveMessageSize = null;
            options.HttpHandler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(5)
            };
        }).AddCallCredentials((authContext, metadata) =>
        {
            if (builder.Configuration["ApiKey"] is string apiKey)
                metadata.Add("Authorization", apiKey);
            return Task.CompletedTask;
        });

if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
    builder.Services.AddTransient<IRestrictedProcessFactory, RestrictedWindowsProcessFactory>();

var host = builder.Build();
host.Run();
