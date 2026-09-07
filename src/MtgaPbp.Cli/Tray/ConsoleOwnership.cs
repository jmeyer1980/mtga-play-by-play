using System.Runtime.Versioning;

namespace MtgaPbp.Cli.Tray;

/// <summary>
/// Whether this process is the only thing attached to its console — and so whether
/// the window would close if it let go.
/// </summary>
/// <remarks>
/// A shortcut, a double-click, the Startup folder and a scheduled task all give the
/// process a console of its own: the attached-process list is just us, and
/// <c>FreeConsole</c> closes the window. (Measured under Windows Terminal, where hiding
/// the window is a no-op — see the design note for #213.) Typed into a terminal, the
/// shell is attached too; letting go would leave its prompt held by a process it can no
/// longer Ctrl+C, so the console is kept and the icon is simply added beside it.
/// </remarks>
public static class ConsoleOwnership
{
    /// <summary>The rule, apart from the call, so it can be tested without a console.</summary>
    public static bool ShouldDetach(uint attachedProcesses) => attachedProcesses == 1;

    /// <summary>How many processes share this console; 0 when there is none.</summary>
    /// <remarks>
    /// Two slots are enough: a buffer too small for the list makes the call return the
    /// count it would have needed, and 2 already means "not alone".
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static uint AttachedProcesses()
    {
        var ids = new uint[2];
        return NativeMethods.GetConsoleProcessList(ids, (uint)ids.Length);
    }

    /// <summary>
    /// Lets go of the console and points the standard streams at nothing, so that
    /// everything the watch would have printed is dropped rather than thrown.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool Detach()
    {
        if (!NativeMethods.FreeConsole()) return false;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        return true;
    }
}
