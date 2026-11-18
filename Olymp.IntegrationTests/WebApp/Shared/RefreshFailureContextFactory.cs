using System.Data.Common;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;

namespace Olymp.IntegrationTests.WebApp.Shared;

internal sealed class RefreshFailureContextFactory(IDbContextFactory<OlympContext> inner) : IDbContextFactory<OlympContext>
{
    public bool Fail { get; set; } = true;

    public OlympContext CreateDbContext() => inner.CreateDbContext();

    public Task<OlympContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Fail ? throw new RefreshException() : inner.CreateDbContextAsync(cancellationToken);

    private sealed class RefreshException : DbException;
}
