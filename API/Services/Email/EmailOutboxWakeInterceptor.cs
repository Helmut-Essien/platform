using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

/// <summary>
/// Signals <see cref="EmailOutboxSignal"/> once queued (or manually retried) emails are committed: right after SaveChanges when it ran
/// in its own transaction, or after the caller's explicit transaction commits (never before: the worker can't see
/// uncommitted rows, so an early signal would be wasted and the email would wait for the next poll). A rollback or
/// failed save drops the pending signal.
/// </summary>
public sealed class EmailOutboxWakeInterceptor(EmailOutboxSignal signal) : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private sealed class State
    {
        public bool Saving;
        public bool PendingCommit;
    }

    private readonly ConditionalWeakTable<DbContext, State> _states = new();

    private State For(DbContext context) => _states.GetValue(context, _ => new State());

    private void BeforeSave(DbContext? context)
    {
        if (context is null)
            return;
        // A new email, or an existing one put back to Pending (the admin "Retry" button).
        For(context).Saving = context.ChangeTracker.Entries<EmailOutboxMessage>().Any(e =>
            e.State == EntityState.Added
            || (e.State == EntityState.Modified && e.Entity.Status == EmailDeliveryStatus.Pending
                && e.Property(m => m.Status).IsModified));
    }

    private void AfterSave(DbContext? context)
    {
        if (context is null || !_states.TryGetValue(context, out var state) || !state.Saving)
            return;
        state.Saving = false;
        if (context.Database.CurrentTransaction is null)
            signal.Notify();
        else
            state.PendingCommit = true;
    }

    private void SaveFailed(DbContext? context)
    {
        if (context is not null && _states.TryGetValue(context, out var state))
            state.Saving = false;
    }

    private void Committed(DbContext? context)
    {
        if (context is null || !_states.TryGetValue(context, out var state) || !state.PendingCommit)
            return;
        state.PendingCommit = false;
        signal.Notify();
    }

    private void Discarded(DbContext? context)
    {
        if (context is not null && _states.TryGetValue(context, out var state))
            state.PendingCommit = false;
    }

    // ---- SaveChanges
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        BeforeSave(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        BeforeSave(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        AfterSave(eventData.Context);
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        AfterSave(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => SaveFailed(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveFailed(eventData.Context);
        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => SaveFailed(eventData.Context);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveFailed(eventData.Context);
        return Task.CompletedTask;
    }

    // ---- explicit transactions
    // A new transaction starts clean (a disposed-without-commit one may not report a rollback).
    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Discarded(eventData.Context);
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Discarded(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Committed(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Discarded(eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Discarded(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Discarded(eventData.Context);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discarded(eventData.Context);
        return Task.CompletedTask;
    }
}
