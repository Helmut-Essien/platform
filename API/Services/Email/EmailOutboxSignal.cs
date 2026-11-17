namespace Platform.Api.Services.Email;

/// <summary>
/// Wakes <see cref="EmailOutboxWorker"/> as soon as a transaction that queued an email has committed, instead of
/// waiting for the next poll. Signals coalesce: any number of <see cref="Notify"/> calls while the worker is busy
/// produce one extra pass, which claims everything due. The poll stays as the fallback (another instance, a
/// missed signal, retries coming due), so a lost or early signal only costs the poll interval.
/// </summary>
public sealed class EmailOutboxSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Notify()
    {
        if (_wake.CurrentCount > 0)
            return;
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another caller signalled first: already awake.
        }
    }

    /// <summary>True when woken by <see cref="Notify"/>, false when the timeout (the poll interval) passed.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _wake.WaitAsync(timeout, cancellationToken);
}
