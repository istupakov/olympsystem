using System.Data;
using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Olymp.IntegrationTests.Shared;

// Forces two serializable transactions to read the same empty range before either writes.
internal sealed class SqlReadBarrier(string marker, bool existsQuery = true) : DbCommandInterceptor
{
    private int _arrivals;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Arrivals => _arrivals;

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.Transaction?.IsolationLevel == IsolationLevel.Serializable
            && (!existsQuery || command.CommandText.Contains("SELECT CASE", StringComparison.Ordinal))
            && command.CommandText.Contains(marker, StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        return result;
    }
}
