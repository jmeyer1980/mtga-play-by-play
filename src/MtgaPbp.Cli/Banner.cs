using System.Text;
using MtgaPbp.Render;

namespace MtgaPbp.Cli;

/// <summary>
/// The identity block every command opens with.
/// </summary>
/// <remarks>
/// The version used to be printed by <c>watch</c> alone, on the eighth line of twelve,
/// underneath the build summary. That is the wrong place for the one fact that settles
/// "am I running the build I think I am" — the question that cost two mornings, and the
/// reason the stamp exists at all. It now comes first, before any work is done, on every
/// command.
/// </remarks>
public static class Banner
{
    /// <remarks>
    /// Plain ASCII rather than box-drawing characters. <c>Console.OutputEncoding</c> is
    /// left alone, so anything outside the console's code page is mangled when output is
    /// redirected or on a legacy code page — the em dashes elsewhere in this tool already
    /// come out as "-" in a redirected log. A banner that degrades into rubble is worse
    /// than a plain one that never does.
    /// </remarks>
    private const string Art = """
         __  __ _____ ____    _      ____  ____  ____
        |  \/  |_   _/ ___|  / \    |  _ \| __ )|  _ \
        | |\/| | | || |  _  / _ \   | |_) |  _ \| |_) |
        | |  | | | || |_| |/ ___ \  |  __/| |_) |  __/
        |_|  |_| |_| \____/_/   \_\ |_|   |____/|_|
        """;

    /// <summary>
    /// Writes the identity block. Call once, first, before any work.
    /// </summary>
    /// <param name="art">
    /// False for the commands whose output is read by something other than a person —
    /// <c>keep</c>, <c>unkeep</c> and the help text — where four lines of decoration
    /// ahead of a one-line answer is noise.
    /// </param>
    public static void Write(bool art = true)
    {
        var sb = new StringBuilder();
        if (art) sb.AppendLine(Art);
        sb.Append("  mtga-pbp ").Append(BuildInfo.Version);
        Console.WriteLine(sb.ToString());

        // Only ever says anything when the exe sits inside a git working copy, which a
        // released copy does not. Reads two files and starts no process — see WorkingCopy.
        if (WorkingCopy.StaleNote(BuildInfo.Version, AppContext.BaseDirectory) is { } stale)
            Console.WriteLine($"  {stale}");

        Console.WriteLine();
    }

    /// <summary>
    /// A labelled path, aligned. Printed before the work rather than after it, so a
    /// wrong archive — from a config that failed to parse and fell back to defaults, say
    /// — is visible at the top instead of being inferred from a match count at the end.
    /// </summary>
    public static void Path(string label, string value) =>
        Console.WriteLine($"  {label,-8} {value}");
}
