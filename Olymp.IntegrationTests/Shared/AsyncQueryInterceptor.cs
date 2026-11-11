using System.Data;
using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Olymp.IntegrationTests.Shared;

// Makes preceding reads synchronous to exercise rendering while the selected query is pending.
internal sealed class AsyncQueryInterceptor(string marker) : DbCommandInterceptor, IDbConnectionInterceptor
{
    public bool WasDelayed { get; private set; }

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
        ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        connection.Open();
        return ValueTask.FromResult(InterceptionResult.Suppress());
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (eventData.CommandSource != CommandSource.LinqQuery)
            return result;
        if (command.CommandText.Contains(marker, StringComparison.Ordinal))
        {
            WasDelayed = true;
            await Task.Yield();
            return result;
        }
        using var reader = command.ExecuteReader();
        var rows = new DataTable();
        rows.Load(reader);
        return InterceptionResult<DbDataReader>.SuppressWithResult(rows.CreateDataReader());
    }
}
