using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Olymp.Checker;

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

    public static IMvcBuilder AddModelBindingLocalization<T>(this IMvcBuilder builder)
    {
        builder.Services.AddOptions<MvcOptions>()
            .Configure<IStringLocalizer<T>>((options, localizer) =>
            {
                var messages = options.ModelBindingMessageProvider;
                messages.SetAttemptedValueIsInvalidAccessor((value, field) => localizer["The value '{0}' is not valid for {1}.", value, field]);
                messages.SetNonPropertyAttemptedValueIsInvalidAccessor(value => localizer["The value '{0}' is not valid.", value]);
                messages.SetUnknownValueIsInvalidAccessor(field => localizer["The supplied value is invalid for {0}.", field]);
                messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => localizer["The supplied value is invalid."]);
                messages.SetValueIsInvalidAccessor(value => localizer["The value '{0}' is invalid.", value]);
                messages.SetValueMustNotBeNullAccessor(value => localizer["The value '{0}' is invalid.", value]);
                messages.SetValueMustBeANumberAccessor(field => localizer["The field {0} must be a number.", field]);
                messages.SetNonPropertyValueMustBeANumberAccessor(() => localizer["The field must be a number."]);
                messages.SetMissingBindRequiredValueAccessor(field => localizer["A value for the '{0}' parameter or property was not provided.", field]);
                messages.SetMissingKeyOrValueAccessor(() => localizer["A value is required."]);
                messages.SetMissingRequestBodyRequiredValueAccessor(() => localizer["A non-empty request body is required."]);
            });

        return builder;
    }

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
