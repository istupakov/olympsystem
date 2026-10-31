using Microsoft.AspNetCore.Identity;

namespace Olymp.Site.Services;

public sealed class EntityNotFoundException() : Exception("The requested entity was not found.");

public sealed class OperationValidationException(string message) : Exception(message);

public sealed class IdentityOperationException(IEnumerable<IdentityError> errors)
    : Exception("The Identity operation failed.")
{
    public IReadOnlyList<IdentityError> Errors { get; } = [.. errors];
}
