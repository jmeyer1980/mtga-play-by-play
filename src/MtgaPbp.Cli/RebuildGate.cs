namespace MtgaPbp.Cli;

/// <summary>
/// Serializes full-site rebuilds so only one runs at a time.
/// </summary>
/// <remarks>
/// `watch` rebuilds from two places: the poll loop when the log grows, and the
/// favorite handler when a star is clicked — the first on the main thread, the
/// second on a request thread. Unserialized, those are two writers over the same
/// output files (#113). Every rebuild funnels through one gate instead.
/// <para>
/// <see cref="RunInBackground"/> exists so the favorite handler can answer the
/// click the moment the keep flag is written: the rebuild only repaints what is
/// already true, and at a thousand matches it takes long enough that a star
/// waiting on it reads as a broken button.
/// </para>
/// <para>
/// The gate is a semaphore awaited asynchronously, so queued background rebuilds
/// park no thread while they wait. The hop through <see cref="Task.Run(Action)"/>
/// is load-bearing: awaiting the semaphore directly in an async method would run
/// the rebuild on the caller's thread whenever the gate happened to be free —
/// which is the favorite handler's request thread, and the exact wait this class
/// exists to remove.
/// </para>
/// </remarks>
public sealed class RebuildGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Runs a rebuild now, after any rebuild already in flight finishes.</summary>
    public void Run(Action rebuild)
    {
        _gate.Wait();
        try { rebuild(); }
        finally { _gate.Release(); }
    }

    /// <summary>Queues a rebuild and returns without waiting for it.</summary>
    public Task RunInBackground(Action rebuild) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { rebuild(); }
            finally { _gate.Release(); }
        });
}
