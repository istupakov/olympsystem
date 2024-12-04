using Olymp.Runner;
using Olymp.Runner.Windows;
using Olymp.Site.Protos;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.Configure<RunnerClientConfig>(context.Configuration.GetSection(RunnerClientConfig.Section));
        services.AddHostedService<RunnerClientService>();

        services.AddGrpcClient<Runner.RunnerClient>(options =>
        {
            options.Address = new Uri(context.Configuration["RunnerServerAddress"]!);
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
            if (context.Configuration["ApiKey"] is string apiKey)
                metadata.Add("Authorization", apiKey);
            return Task.CompletedTask;
        });

        if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
            services.AddTransient<IRestrictedProcessFactory, RestrictedWindowsProcessFactory>();
    })
    .Build();

host.Run();
