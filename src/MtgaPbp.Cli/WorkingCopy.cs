namespace MtgaPbp.Cli;

/// <summary>
/// Whether this executable was built from the source sitting next to it.
/// </summary>
/// <remarks>
/// A build published into a working copy goes stale the moment the next commit lands,
/// and nothing about running it says so — the report's build stamp names the commit it
/// came from, which only helps once you already suspect something. This closes that:
/// when the exe lives inside a git working copy, it says so on startup rather than
/// waiting to be asked.
/// <para>
/// A released copy — unzipped into Downloads, installed by a package manager — has no
/// working copy above it and this stays completely silent. It is a developer's warning,
/// not a user's.
/// </para>
/// <para>
/// <b>What it reads, and what it does not.</b> Two plain text files inside <c>.git</c>:
/// <c>HEAD</c>, and whichever ref that names, falling back to <c>packed-refs</c>.
/// Nothing else is opened, no <c>git</c> process is started, and no network request is
/// made — this tool makes none anywhere and this is not the exception. It cannot tell
/// you about a remote you have not fetched, and does not try: the comparison is
/// entirely between this exe and the files already on your disk.
/// </para>
/// </remarks>
public static class WorkingCopy
{
    /// <summary>
    /// A one-line warning when the working copy has moved past this build, or null when
    /// there is no working copy, when it agrees, or when anything at all is unreadable.
    /// </summary>
    /// <param name="stampedVersion">
    /// <see cref="MtgaPbp.Render.BuildInfo.Version"/> — <c>0.3.1+5c63fa3e</c>. The part
    /// after the plus is the commit this exe was built from.
    /// </param>
    public static string? StaleNote(string stampedVersion, string exeDir)
    {
        var built = stampedVersion.Split('+', 2);
        if (built.Length != 2 || built[1].Length < 7) return null;

        var head = HeadOf(exeDir);
        if (head is null) return null;

        // The stamp is abbreviated and HEAD is not, so compare on the shorter.
        if (head.StartsWith(built[1], StringComparison.OrdinalIgnoreCase)) return null;

        return $"note: built from {built[1]}, but the working copy is at {head[..8]}. " +
               "Re-publish to pick up newer commits.";
    }

    /// <summary>
    /// The commit HEAD points at, found by walking up from the executable. Null for any
    /// reason at all — this is a courtesy, and a courtesy that throws is worse than one
    /// that stays quiet.
    /// </summary>
    private static string? HeadOf(string exeDir)
    {
        try
        {
            var dir = new DirectoryInfo(exeDir);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
                dir = dir.Parent;
            if (dir is null) return null;

            var git = Path.Combine(dir.FullName, ".git");
            var headFile = Path.Combine(git, "HEAD");
            if (!File.Exists(headFile)) return null;

            var head = File.ReadAllText(headFile).Trim();

            // Detached: HEAD holds the commit outright.
            if (!head.StartsWith("ref:", StringComparison.Ordinal)) return head;

            var reference = head[4..].Trim();
            var loose = Path.Combine(git, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose)) return File.ReadAllText(loose).Trim();

            // A ref that has been packed away lives in one line of packed-refs.
            var packed = Path.Combine(git, "packed-refs");
            if (!File.Exists(packed)) return null;

            foreach (var line in File.ReadLines(packed))
            {
                if (line.Length == 0 || line[0] is '#' or '^') continue;
                var parts = line.Split(' ', 2);
                if (parts.Length == 2 && parts[1].Trim() == reference) return parts[0].Trim();
            }

            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
