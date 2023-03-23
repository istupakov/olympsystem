using Olymp.Checker;
using Olymp.Site;

namespace Olymp.Site;

public static class BuilderExtensions
{
    public static IMvcBuilder AddDataAnnotationsLocalization<T>(this IMvcBuilder builder)
    {
        return builder.AddDataAnnotationsLocalization(options =>
        {
            var assembly = typeof(T).Assembly;
            var oldProvider = options.DataAnnotationLocalizerProvider;
            options.DataAnnotationLocalizerProvider = (type, factory) =>
                type.Assembly == assembly ? factory.Create(typeof(T)) :
                oldProvider?.Invoke(type, factory) ?? null!;
        });
    }

    public static IServiceCollection AddCheckerTests(this IServiceCollection services)
    {
        var tests = typeof(ICheckerTest).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && x.IsClass && typeof(ICheckerTest).IsAssignableFrom(x));

        foreach (var test in tests)
            services.Add(new ServiceDescriptor(typeof(ICheckerTest), test, ServiceLifetime.Transient));

        return services;
    }

    public static IServiceCollection AddSimpleCheckers(this IServiceCollection services)
    {
        var checkers = typeof(ISimpleChecker).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && x.IsClass && typeof(ISimpleChecker).IsAssignableFrom(x));

        foreach (var checker in checkers)
            services.Add(new ServiceDescriptor(typeof(ISimpleChecker), checker, ServiceLifetime.Transient));

        return services;
    }
}
