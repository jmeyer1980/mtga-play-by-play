using System.Diagnostics.CodeAnalysis;

namespace MtgaPbp.Cli;

/// <summary>
/// The one signal that ends a <c>watch</c>: Ctrl+C sets it, the icon's Quit sets it,
/// and <c>mtga-pbp stop</c> sets it from another process.
/// </summary>
/// <remarks>
/// A named event rather than a request to the loopback server: the server's Host and
/// Origin checks exist to keep hostile pages out (#116), and a stop endpoint would be one
/// more thing they had to be right about. A kernel object in the <c>Local\</c> namespace
/// is visible only inside this logon session, needs no port of its own, and disappears
/// with the process that created it — so "is a watch running on 8787" is answered by
/// whether the name still opens (#213).
/// <para>
/// Named events are a Windows feature. Elsewhere the signal still serves the in-process
/// callers; <c>stop</c> simply finds nothing, which is what it says.
/// </para>
/// </remarks>
public sealed class StopSignal : IDisposable
{
    private const string Prefix = @"Local\mtga-pbp-stop-";

    private readonly EventWaitHandle _handle;

    private StopSignal(EventWaitHandle handle, bool named)
    {
        _handle = handle;
        IsNamed = named;
    }

    /// <summary>Whether another process can find this signal by port.</summary>
    public bool IsNamed { get; }

    /// <summary>The kernel object a watch on <paramref name="port"/> listens under.</summary>
    public static string NameFor(int port) => Prefix + port;

    /// <summary>Creates the signal a watch on <paramref name="port"/> waits on.</summary>
    public static StopSignal Listen(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new StopSignal(
                    new EventWaitHandle(false, EventResetMode.ManualReset, NameFor(port)),
                    named: true);
            }
            catch (PlatformNotSupportedException) { /* the local one below still works */ }
        }
        return new StopSignal(new EventWaitHandle(false, EventResetMode.ManualReset), named: false);
    }

    /// <summary>
    /// Sets the signal of a watch on <paramref name="port"/>. False when nothing listens
    /// there — which, without named events, is always.
    /// </summary>
    public static bool Fire(int port)
    {
        if (!TryOpen(port, out var handle)) return false;
        using (handle) return handle.Set();
    }

    /// <summary>Whether a watch on <paramref name="port"/> is still holding its signal.</summary>
    public static bool IsListening(int port)
    {
        if (!TryOpen(port, out var handle)) return false;
        handle.Dispose();
        return true;
    }

    private static bool TryOpen(int port, [NotNullWhen(true)] out EventWaitHandle? handle)
    {
        handle = null;
        if (!OperatingSystem.IsWindows()) return false;
        try { return EventWaitHandle.TryOpenExisting(NameFor(port), out handle); }
        catch (PlatformNotSupportedException) { return false; }
    }

    public void Set() => _handle.Set();

    /// <summary>True once set; false when <paramref name="timeout"/> passes first.</summary>
    public bool Wait(TimeSpan timeout) => _handle.WaitOne(timeout);

    public void Dispose() => _handle.Dispose();
}
