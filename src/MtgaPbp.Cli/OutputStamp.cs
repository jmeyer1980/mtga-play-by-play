namespace MtgaPbp.Cli;

/// <summary>
/// Gives a rendered file the modification time of the match it is about.
/// </summary>
/// <remarks>
/// A build rewrites every page and every markdown export, so without this all ~1,200
/// files carry one timestamp — the moment the build ran — and sorting the directory by
/// "newest" returns an arbitrary match (#147). The rendered pages and the index are
/// unaffected either way, because they sort on the archive's own timestamps; this is
/// only about the files as files. But the exports are offered as plain files on disk,
/// and file times are part of how anyone treats a directory of those: <c>ls -t</c>,
/// <c>Sort-Object LastWriteTime</c>, Explorer's Date column, and any script written over
/// <c>out/text/</c>. Every one of them silently gives a wrong answer after a rebuild and
/// a right one between rebuilds, which is the worst combination — it works until it
/// doesn't.
/// <para>
/// The match's START, not its end, though either would beat the build clock. The index,
/// the neighbour links and the whole chronological ordering are all built on
/// <c>StartedAtMs</c>, so using it here means the order a file browser shows is the same
/// order the report shows. Ending times would agree almost always and disagree exactly
/// when one match started before another finished, which is the kind of nearly-right
/// that is harder to trust than being plainly different.
/// </para>
/// <para>
/// <c>index.html</c> is deliberately not stamped. It describes the build, not a match,
/// so the build's own time is the true one.
/// </para>
/// </remarks>
public static class OutputStamp
{
    /// <summary>
    /// Stamps each path with <paramref name="startedAtMs"/>, and returns whether it did.
    /// </summary>
    /// <remarks>
    /// Every failure is silence. A file time is a convenience on top of a file whose
    /// contents are already correct and already written, so there is no version of
    /// "could not set a timestamp" — a locked file, a scanner holding a handle, a
    /// timestamp the filesystem will not represent — worth failing a build over.
    /// A match with no recorded start is left alone rather than stamped with the epoch,
    /// which would sort it to the top of a directory as confidently as the build clock
    /// sorted it to the bottom.
    /// </remarks>
    public static bool MatchTime(long startedAtMs, params string[] paths)
    {
        if (startedAtMs <= 0) return false;

        try
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs).UtcDateTime;
            foreach (var path in paths) File.SetLastWriteTimeUtc(path, when);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or ArgumentOutOfRangeException
                                    or NotSupportedException)
        {
            return false;
        }
    }
}
