using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Platform.Api.Data;

/// <summary>
/// Runs work under EF Core's retrying execution strategy with an optional owned transaction.
/// Required when <c>EnableRetryOnFailure</c> is configured — user-initiated transactions
/// cannot be started outside <see cref="DatabaseFacade.CreateExecutionStrategy"/>.
/// </summary>
public static class RetriableTransaction
{
    public static Task<TResult> ExecuteAsync<TResult>(
        DbContext db,
        Func<IDbContextTransaction?, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var isFirstAttempt = true;
        return strategy.ExecuteAsync(async () =>
        {
            IDbContextTransaction? transaction = null;
            var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
            if (ownsTransaction)
            {
                // After a rolled-back attempt, tracked entities from SaveChanges remain on the
                // scoped DbContext; clearing prevents duplicate inserts on retry.
                if (!isFirstAttempt)
                    db.ChangeTracker.Clear();
                isFirstAttempt = false;
                transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            }

            await using var owned = transaction;
            return await operation(transaction, cancellationToken);
        });
    }

    public static Task ExecuteAsync(
        DbContext db,
        Func<IDbContextTransaction?, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(db, async (tx, ct) =>
        {
            await operation(tx, ct);
            return null;
        }, cancellationToken);
}
