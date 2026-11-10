using Olymp.Checker;

namespace Olymp.WebApp;

public static class BuilderExtensions
{
    public static IServiceCollection AddCheckerTests(this IServiceCollection services)
    {
        var tests = typeof(ICheckerTest).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && x.IsClass && typeof(ICheckerTest).IsAssignableFrom(x));

        foreach (var test in tests)
            services.AddSingleton(typeof(ICheckerTest), test);

        return services;
    }

    public static IServiceCollection AddSimpleCheckers(this IServiceCollection services)
    {
        var checkers = typeof(ISimpleChecker).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && x.IsClass && typeof(ISimpleChecker).IsAssignableFrom(x));

        foreach (var checker in checkers)
            services.AddSingleton(typeof(ISimpleChecker), checker);

        return services;
    }

    public static IServiceCollection AddCompilators(this IServiceCollection services)
    {
        var compilators = typeof(ICompilatorConfiguration).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && x.IsClass && typeof(ICompilatorConfiguration).IsAssignableFrom(x));

        foreach (var compilator in compilators)
            services.AddSingleton(typeof(ICompilatorConfiguration), compilator);

        return services;
    }
}
