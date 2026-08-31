using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>What one Arena log looked like the last time a capture read it.</summary>
/// <param name="Startup">
/// The <c>Startup Timestamp</c> Arena writes near the top of the file, which is the only
/// thing in the header that differs between one session and the next.
/// </param>
/// <param name="Size">
/// How long the file was. Not used by the rotation check — the timestamp settles that on
/// its own — but it is what tells a person reading logs.json whether a session was one
/// game or a whole evening, and it costs nothing to record from a stream already open.
/// </param>
public sealed record LogSighting(string Startup, long Size);

/// <summary>
/// Remembers which Arena session each log held at the last capture, so a rotation that
/// happened while the tool was not running can be noticed rather than only regretted.
/// </summary>
/// <remarks>
/// <c>Player.log</c> is a rolling buffer: Arena truncates it on restart and the session
/// it displaces survives only in <c>Player-prev.log</c>. A session that begins and
/// rotates out between two captures is therefore gone from disk entirely, and until now
/// the tool said so once in the README and never again — a permanent, silent loss with
/// no sign anywhere that it had happened (#135).
/// <para>
/// The identity is the <c>Startup Timestamp</c> line. Measured rather than assumed: on
/// 2026-08-31 it sat at line 37 of both logs, and the 36 lines above it — Mono paths, a
/// physics backend id, the Unity engine version — were byte-identical between a session
/// from that morning and one from the previous afternoon, so nothing earlier in the
/// header can tell two sessions apart.
/// </para>
/// <para>
/// A logs.json that will not parse is treated as absent and overwritten, which is the
/// opposite of what <see cref="InventoryLedger"/> does with a damaged file — and the
/// difference is the point. The inventory is the only copy of a history that cannot be
/// rebuilt from anything else. This is a cache of two facts about files that are still
/// sitting there, it repairs itself on the next capture, and the whole cost of losing it
/// is one warning that does not get said.
/// </para>
/// </remarks>
public sealed class LogSessions
{
    private readonly string _path;
    private Dictionary<string, LogSighting> _seen;
    private bool _changed;

    public LogSessions(string archiveDir)
    {
        _path = Path.Combine(archiveDir, "logs.json");
        _seen = Load(_path);
    }

    /// <summary>
    /// How far into a log the header is worth searching for the startup line.
    /// </summary>
    /// <remarks>
    /// It is line 37 today. The margin is for an Arena update that adds a few lines of
    /// its own above it, which is a thing patches do; past this the file is match
    /// traffic and the line is not coming.
    /// </remarks>
    private const int HeaderLines = 400;

    private const string Marker = "Startup Timestamp: ";

    /// <summary>
    /// Compares the logs against the last capture's record and replaces it with this
    /// one.
    /// </summary>
    /// <returns>
    /// A warning when a session may have rotated away unseen, or null.
    /// </returns>
    /// <remarks>
    /// Returned rather than printed, for <see cref="DriftCanary"/>'s reason: under
    /// <c>watch</c> a bare write lands inside the pinned block and corrupts the repaint.
    /// </remarks>
    public string? Observe(IReadOnlyList<string> logPaths)
    {
        var now = new Dictionary<string, LogSighting>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in logPaths)
            if (Read(path) is { } sighting) now[path] = sighting;

        // Nothing readable says nothing about rotation — Arena may simply not have been
        // installed yet, or the logs may be mid-replacement this second — and replacing
        // the record with that would erase the only thing a future capture could notice
        // a rotation against. Keeping it costs a stale entry until the logs come back.
        if (now.Count == 0) return null;

        var warning = MayHaveLostASession(logPaths, now) ? Warning : null;

        // Otherwise wholesale, so a log that has gone away stops being remembered as
        // present. A merge would leave an entry claiming a session is still on disk
        // when it is not, which is the one error that would make the check above lie in
        // the direction that loses matches quietly.
        _changed = !Same(_seen, now);
        _seen = now;
        return warning;
    }

    /// <summary>
    /// True when the session that was current at the last capture is in none of the
    /// logs now, so Arena has restarted at least twice since.
    /// </summary>
    /// <remarks>
    /// The first entry in <c>LogPaths</c> is the live log and the rest are the copies
    /// Arena has already rotated out, which is what the shipped configuration means and
    /// what the ordering has to keep meaning for this to be worth anything.
    /// <para>
    /// This is deliberately approximate, and the wording says "may" because of exactly
    /// one case. Call the session that was current last time A. One restart moves A into
    /// Player-prev.log, where this capture reads it: nothing is lost and nothing is
    /// said. Two restarts push A off the end, but then the session now in
    /// Player-prev.log is the one that immediately followed A — it is being read right
    /// now, and again nothing is actually lost. Only at three does a session exist that
    /// no capture ever saw. Two and three cannot be told apart from the identities
    /// alone, and timestamps do not separate them either, because Arena can sit closed
    /// for hours between sessions and a gap proves nothing about how many there were.
    /// </para>
    /// <para>
    /// So it over-warns, on purpose. A false positive costs one line of advice that was
    /// already good advice; a false negative is a match that no longer exists anywhere
    /// and is never mentioned again.
    /// </para>
    /// </remarks>
    private bool MayHaveLostASession(
        IReadOnlyList<string> logPaths, Dictionary<string, LogSighting> now)
    {
        // Nothing to compare against on the very first capture, and nothing readable
        // right now is a different problem with its own message.
        if (_seen.Count == 0 || now.Count == 0) return false;

        if (logPaths.Count == 0) return false;
        if (!_seen.TryGetValue(logPaths[0], out var wasCurrent)) return false;

        return !now.Values.Any(s =>
            string.Equals(s.Startup, wasCurrent.Startup, StringComparison.Ordinal));
    }

    internal const string Warning =
        "Arena has restarted more than once since the last capture — the session that " +
        "was open then is no longer in either log. If a whole session began and ended " +
        "in between, its matches are gone from disk and are not in the archive. " +
        "Running `mtga-pbp watch` while you play is what prevents this; the README has " +
        "a recipe for starting it at logon.";

    /// <summary>Writes the record, if this capture changed it.</summary>
    public bool Commit()
    {
        if (!_changed) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_seen, Options));
        _changed = false;
        return true;
    }

    /// <summary>
    /// The session a log currently holds, or null when it cannot be read right now.
    /// </summary>
    /// <remarks>
    /// The share flags match <see cref="LogScanner.Scan"/>'s and have to: Arena holds
    /// Player.log open for writing the whole time it runs, so the default share mode
    /// raises a sharing violation, and Delete is what lets it rotate the log out from
    /// under a reader instead of being blocked by one.
    /// </remarks>
    public static LogSighting? Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = stream.Length;
            using var reader = new StreamReader(stream);

            for (var line = 0; line < HeaderLines; line++)
            {
                if (reader.ReadLine() is not { } text) break;
                var at = text.IndexOf(Marker, StringComparison.Ordinal);
                if (at < 0) continue;

                var startup = text[(at + Marker.Length)..].Trim();
                return startup.Length == 0 ? null : new LogSighting(startup, size);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            // Missing, mid-rotation, or held by something else. A log that cannot be
            // read is not a log that has rotated, and guessing either way from here
            // would be worse than saying nothing.
        }

        return null;
    }

    private static bool Same(
        Dictionary<string, LogSighting> a, Dictionary<string, LogSighting> b) =>
        a.Count == b.Count &&
        a.All(kv => b.TryGetValue(kv.Key, out var other) && other == kv.Value);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static Dictionary<string, LogSighting> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);

            var stored = JsonSerializer.Deserialize<Dictionary<string, LogSighting>>(
                File.ReadAllText(path));

            return stored is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or JsonException)
        {
            // Starting over costs one warning; see the class remarks for why that is a
            // different trade from the one InventoryLedger makes.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
