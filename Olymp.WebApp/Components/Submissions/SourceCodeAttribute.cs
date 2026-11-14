using System.ComponentModel.DataAnnotations;

using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Submissions;

[AttributeUsage(AttributeTargets.Property)]
public sealed class SourceCodeAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        string? error = value is not string source || string.IsNullOrWhiteSpace(source) ? SiteResources.SourceCodeRequired
            : source.Length is < 10 or > 32 * 1024 ? SiteResources.SourceCodeLength : null;
        return error is null ? ValidationResult.Success : new(error, validationContext.MemberName is { } member ? [member] : []);
    }
}
