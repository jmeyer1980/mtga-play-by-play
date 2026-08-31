namespace MtgaPbp.Cli;

/// <summary>
/// Remembers how large each log was when its growth was last captured, and answers
/// whether any of them has moved since.
/// </summary>
/// <remarks>
/// This was four lines inside `watch`'s poll loop, and both of the things it now does
/// differently cost matches rather than convenience.
/// <para>
/// The size is read through a single <see cref="FileInfo"/>, so <c>Exists</c> and
/// <c>Length</c> are answered from one snapshot of the file. Asking the file system
/// twice — <c>Where(File.Exists)</c> and then <c>new FileInfo(log).Length</c> — leaves a
/// window between the two questions, and Arena's restart deletes Player.log inside it.
/// The second question then threw, the exception left the poll loop and left Main, and
/// `watch` ended at exactly the moment it was the only thing standing between a restart
/// and a permanently lost session (#132). Both halves of that are measured rather than
/// reasoned about, in
/// <c>LogGrowthTests.One_FileInfo_answers_both_questions_from_the_same_snapshot</c>:
/// deleting the file after <c>Exists</c> still yields its length, and the two-question
/// form still throws <see cref="FileNotFoundException"/>.
/// </para>
/// <para>
/// And a size is <em>measured</em> in one step and <em>committed</em> in another, with
/// the capture in between. Recording it where it is read is what turns a failed capture
/// into a silently skipped one: the loop already believes it has seen that growth, so
/// the next poll finds nothing to do. While Arena keeps writing, the log grows again and
/// the miss heals itself — but on the poll after the night's last match it does not, and
/// the match waits for tomorrow's first game to be noticed.
/// </para>
/// </remarks>
public sealed class LogGrowth
{
    // Ordinal-ignore-case for both: these are Windows paths, and the same log reached
    // through a differently-cased LogPaths entry is the same file.
    private readonly Dictionary<string, long> _captured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _measured = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Measures every log that can be measured at this instant.
    /// </summary>
    /// <returns>
    /// True when some log's size differs from what it was at the last <see cref="Commit"/>
    /// — including a log seen for the first time, and including one that has grown
    /// smaller, which is what an Arena restart truncating the log looks like.
    /// </returns>
    /// <remarks>
    /// A log that cannot be measured right now keeps the size it last had rather than
    /// dropping out, so a rotation is not mistaken for growth when the file returns at
    /// the same length, and a log that goes away for good stops being mentioned rather
    /// than being reported as changed forever.
    /// </remarks>
    public bool Measure(IEnumerable<string> logPaths)
    {
        foreach (var path in logPaths)
            if (LengthOf(path) is { } length) _measured[path] = length;

        foreach (var (path, length) in _measured)
            if (!_captured.TryGetValue(path, out var was) || was != length)
                return true;

        return false;
    }

    /// <summary>
    /// Accepts the last measurement as captured, so it stops counting as growth.
    /// </summary>
    /// <remarks>
    /// Called after the capture that consumed the measurement has returned, and never
    /// before it. Not calling it is how a poll that threw gets retried on the next tick
    /// instead of being stepped over.
    /// </remarks>
    public void Commit()
    {
        foreach (var (path, length) in _measured) _captured[path] = length;
    }

    /// <summary>The log's length, or null when it cannot be measured at this instant.</summary>
    /// <remarks>
    /// A log that is missing, mid-replacement, or briefly held by something else is not
    /// an error here — it is one poll's worth of nothing to say, three seconds before
    /// the same question is asked again. <c>Exists</c> is what fills the snapshot that
    /// <c>Length</c> then reads, so those two cannot disagree with each other; the catch
    /// covers what a snapshot cannot, which is failing to take one at all.
    /// </remarks>
    private static long? LengthOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
