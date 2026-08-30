namespace MtgaPbp.Core;

/// <summary>
/// Decides when a capture that recognized nothing deserves a warning instead of
/// silence.
/// </summary>
/// <remarks>
/// The scanner has always counted what it read, but nothing read the counts back —
/// so when Arena renames a field the slicer keys on, every line still parses, zero
/// matches are produced, and "captured 0 new matches" is byte-identical to the
/// benign nothing-new-since-last-run case a user sees on almost every run. Under
/// `watch` it is worse: quiet mode prints nothing at all, the board keeps showing
/// the last good state, and matches age out of Arena's rolling log while the window
/// looks alive (#117). The one distinguishable signal is volume: a log carrying
/// match-shaped traffic that produced not a single slice.
/// </remarks>
public static class DriftCanary
{
    /// <summary>
    /// The records-read line below which an empty capture stays quiet.
    /// </summary>
    /// <remarks>
    /// A session holding even one played match streams tens of thousands of JSON
    /// records; a browse-the-store-and-quit session stays in the low thousands. The
    /// floor sits between those, and the warning's own wording hedges with "if Arena
    /// was played" because the floor is a judgment call, not a measurement — the
    /// cost of a rare false positive is one conditional sentence, while the cost of
    /// a missed true positive is matches silently aging out of the log.
    /// </remarks>
    public const long RecordFloor = 20_000;

    /// <summary>
    /// The warning to show, or null when there is nothing suspicious about this
    /// capture. <paramref name="slicesSeen"/> counts what the slicer produced, not
    /// what the archive accepted — a run that re-reads already-archived matches
    /// writes nothing and must stay quiet.
    /// </summary>
    public static string? Warn(ScanStats stats, int slicesSeen)
    {
        if (slicesSeen > 0 || stats.JsonLines < RecordFloor) return null;

        return $"read {stats.JsonLines:N0} records without recognizing a single match. " +
               "If Arena was played inside this log's window, the log format may have " +
               "changed and this build cannot see the matches — check for a newer mtga-pbp.";
    }
}
