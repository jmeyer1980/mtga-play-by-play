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
/// </remarks>
public sealed class RebuildGate
{
    private readonly object _gate = new();

    /// <summary>Runs a rebuild now, after any rebuild already in flight finishes.</summary>
    public void Run(Action rebuild)
    {
        lock (_gate) rebuild();
    }

    /// <summary>Queues a rebuild and returns without waiting for it.</summary>
    public Task RunInBackground(Action rebuild) => Task.Run(() => Run(rebuild));
}
