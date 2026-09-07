namespace MtgaPbp.Cli;

/// <summary>
/// <c>mtga-pbp stop [port]</c>: ends a running <c>watch</c> from any terminal or script.
/// </summary>
/// <remarks>
/// A <c>watch</c> can be started where nobody can see it — a scheduled task without
/// <c>/it</c>, a hidden window, a session that has since closed — and the only way to
/// end that used to be Task Manager (#213). This finds the watch by the named event
/// <see cref="StopSignal"/> creates, fires it, then waits for the name to disappear,
/// which it does when the watch's process exits: "stopped." here means what it means in
/// the watch's own window.
/// <para>
/// Kept apart from <c>Program</c>, with its writers and its patience as parameters, so
/// each answer it can give is a test rather than a manual run.
/// </para>
/// </remarks>
public static class StopCommand
{
    /// <summary>The port <c>watch</c> serves on when none is given.</summary>
    public const int DefaultPort = 8787;

    /// <param name="portArg">The first operand, or null for the default port.</param>
    /// <param name="patience">
    /// How long to wait for the watch to be gone before saying that it is still going.
    /// A watch mid-rebuild finishes the rebuild first, which at a large archive is
    /// seconds; the request has been delivered either way.
    /// </param>
    public static int Run(string? portArg, TimeSpan patience, TextWriter stdout, TextWriter stderr)
    {
        int port;
        if (portArg is null) port = DefaultPort;
        else if (!int.TryParse(portArg, out port) || port is < 1 or > 65535)
        {
            stderr.WriteLine($"usage: mtga-pbp stop [port]   ({portArg} is not a port)");
            return 2;
        }

        if (!StopSignal.Fire(port))
        {
            stderr.WriteLine($"no watch is running on port {port}");
            return 1;
        }

        var deadline = DateTime.UtcNow + patience;
        while (StopSignal.IsListening(port))
        {
            if (DateTime.UtcNow >= deadline)
            {
                stdout.WriteLine($"asked the watch on port {port} to stop; it is still finishing " +
                                 "(a rebuild in progress takes a few seconds more)");
                return 0;
            }
            Thread.Sleep(100);
        }
        stdout.WriteLine("stopped.");
        return 0;
    }
}
