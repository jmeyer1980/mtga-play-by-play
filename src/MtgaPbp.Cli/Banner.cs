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
    /// The commands whose output carries a build stamp, and so the only ones a stale
    /// published copy actually misleads.
    /// </summary>
    /// <remarks>
    /// <c>capture</c> is absent deliberately rather than by oversight. It writes, but it
    /// writes to the archive, and the archive carries no stamp — everything downstream is
    /// re-derived from it by <c>build</c>. Re-publishing before a capture changes nothing
    /// about what the capture stores.
    /// <para>
    /// An unrecognised word is absent too, and reaches the usage text without a warning
    /// on the way. Someone who mistyped a command is not being told about their build.
    /// </para>
    /// </remarks>
    private static readonly string[] Stamped = ["build", "watch", "all"];

    /// <summary>
    /// The stale-build warning for <paramref name="command"/>, or null when this command
    /// would not be changed by a re-publish — or when nothing is stale.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Write"/>, and taking the version and directory rather
    /// than reading them, for the reason <see cref="Why.ParseTurns"/> is kept apart from
    /// <see cref="Why.Run"/>: this is the half that can be tested. A test can hand it a
    /// working copy that has genuinely moved on and watch the quiet commands stay quiet,
    /// which a test of the command list alone would not prove.
    /// </remarks>
    public static string? StaleNoteFor(string command, string version, string exeDir) =>
        Stamped.Contains(command, StringComparer.Ordinal)
            ? WorkingCopy.StaleNote(version, exeDir)
            : null;

    /// <summary>The identity block as text, so that it can be asserted on.</summary>
    public static string Compose(bool art, string version, string? staleNote)
    {
        var sb = new StringBuilder();
        if (art) sb.AppendLine(Art);
        sb.Append("  mtga-pbp ").AppendLine(version);
        if (staleNote is not null) sb.Append("  ").AppendLine(staleNote);
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Writes the identity block. Call once, first, before any work.
    /// </summary>
    /// <param name="command">
    /// What is about to run, which decides whether a build lagging behind the working
    /// copy is worth saying. It used to be said on every command, so <c>--version</c> and
    /// <c>why</c> nagged about output they do not write, and a docs-only merge was enough
    /// to set it off — the stamp carries the commit hash, and any commit moves it (#196).
    /// </param>
    /// <param name="art">
    /// False for the commands whose output is read by something other than a person —
    /// <c>keep</c>, <c>unkeep</c> and the help text — where four lines of decoration
    /// ahead of a one-line answer is noise.
    /// </param>
    public static void Write(string command, bool art = true)
    {
        // Only ever says anything when the exe sits inside a git working copy, which a
        // released copy does not. Reads two files and starts no process — see WorkingCopy.
        var stale = StaleNoteFor(command, BuildInfo.Version, AppContext.BaseDirectory);
        Console.Write(Compose(art, BuildInfo.Version, stale));
    }

    /// <summary>
    /// A labelled path, aligned. Printed before the work rather than after it, so a
    /// wrong archive — from a config that failed to parse and fell back to defaults, say
    /// — is visible at the top instead of being inferred from a match count at the end.
    /// </summary>
    public static void Path(string label, string value) =>
        Console.WriteLine($"  {label,-8} {value}");
}
