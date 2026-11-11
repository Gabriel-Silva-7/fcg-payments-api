using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace FCG.Payments.Tests.Persistence;

/// <summary>Let both real SELECT snapshots observe absence before either INSERT can run.</summary>
internal sealed class ConcurrentReadBarrier(Guid orderId) : DbCommandInterceptor
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int reads;
    public int UniqueCollisions;

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
            && command.CommandText.Contains("payment_decisions", StringComparison.Ordinal)
            && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == orderId))
        {
            var count = Interlocked.Increment(ref reads);
            if (count == 2) ready.TrySetResult();
            if (count <= 2) await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        return result;
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_payment_decisions" })
            Interlocked.Increment(ref UniqueCollisions);
        return Task.CompletedTask;
    }
}
