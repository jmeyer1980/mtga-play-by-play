using System.IO.Compression;
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record ArchiveEntry(
    string MatchId, long StartedAtMs, long EndedAtMs, bool Incomplete, bool Favorite = false,
    int Gaps = 0, bool HasDeck = false);

public sealed class RawArchive
{
    private readonly string _rawDir;
    private readonly string _ledgerPath;
    private readonly Dictionary<string, ArchiveEntry> _ledger;

    /// <summary>
    /// Guards <see cref="_ledger"/>, and every write of it to disk once construction
    /// has finished.
    /// </summary>
    /// <remarks>
    /// The ledger is loaded once and written back whole, so two instances of this class
    /// over one archive lose each other's changes: a star saved while a capture is in
    /// flight is reverted the moment that capture saves the copy it loaded before the
    /// click (#146). The fix is for `watch` to hold ONE instance — but an instance
    /// reached from a poll loop, a rebuild and a web request at once has to be safe to
    /// share, and locking at each call site is discipline that decays. So it is safe
    /// here, once, and sharing needs no ceremony.
    /// <para>
    /// <see cref="ReadLines"/> deliberately does NOT take this. It touches no ledger
    /// state, and a build reads every match in the archive — around 1,200 files and
    /// some gigabytes decompressed. Holding this across that would block a star click
    /// for the length of a whole build, which is the delay #113 reported and #145 went
    /// to some trouble to remove.
    /// </para>
    /// <para>
    /// The constructor is the one place that loads and saves without taking it —
    /// <see cref="Reindex"/> is called from there and nowhere else, and until the
    /// constructor returns nothing else can be holding a reference to this instance to
    /// race with. That is a fact about when it runs rather than a gap in the guard, but
    /// it is the kind of fact a later edit can quietly falsify: anything that starts
    /// calling Reindex after construction has to take the lock.
    /// </para>
    /// </remarks>
    private readonly Lock _ledgerLock = new();

    public RawArchive(string archiveRoot)
    {
        _rawDir = Path.Combine(archiveRoot, "raw");
        Directory.CreateDirectory(_rawDir);
        _ledgerPath = Path.Combine(archiveRoot, "index.json");
        _ledger = LoadLedger(_ledgerPath) ?? LoadLedger(_ledgerPath + ".bak") ?? [];
        Reindex();
    }

    /// <summary>
    /// Reads one ledger candidate, or null when it is absent or will not parse.
    /// </summary>
    /// <remarks>
    /// A ledger that will not parse is moved aside rather than abandoned in place,
    /// for <see cref="InventoryLedger"/>'s reason: overwriting it on the next save
    /// would destroy the evidence of what went wrong. Unlike the inventory, though,
    /// losing this file must not lose anything — the backup from the previous save
    /// and the raw files themselves (see <see cref="Reindex"/>) are always enough to
    /// keep every match reachable, which is why this returns null and lets the
    /// constructor fall through instead of starting empty.
    /// </remarks>
    private static Dictionary<string, ArchiveEntry>? LoadLedger(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ArchiveEntry>>(
                File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            var aside = path.EndsWith(".bak", StringComparison.Ordinal)
                ? null                       // an unreadable backup has nothing to prove
                : path + ".unreadable";
            try
            {
                if (aside is not null)
                {
                    File.Move(path, aside, overwrite: true);
                    Console.Error.WriteLine($"Could not read {path}; kept it as {aside}.");
                }
            }
            catch (IOException)
            {
                Console.Error.WriteLine($"Could not read or move {path}; continuing without it.");
            }
            return null;
        }
    }

    /// <summary>
    /// Adds a ledger entry for any raw file that lacks one, so a match is reachable
    /// as long as its <c>.json.gz</c> survives — whatever happened to the ledger.
    /// </summary>
    /// <remarks>
    /// The filenames are the match ids, which makes the raw directory a ledger of
    /// last resort. A rebuilt entry claims the least the archive can prove from a
    /// filename — no gaps, no deck, complete, not a favourite, timestamps from the
    /// file itself — so the recapture rules in <see cref="Write"/> are free to win
    /// again and restore the richer metadata where the log still holds it. This runs
    /// on every open rather than behind a repair command because the failure it heals
    /// is exactly the one a user cannot be expected to diagnose: every file intact,
    /// nothing on the report.
    /// </remarks>
    private void Reindex()
    {
        var added = 0;
        foreach (var file in Directory.EnumerateFiles(_rawDir, "*.json.gz"))
        {
            var matchId = Path.GetFileName(file)[..^".json.gz".Length];
            if (_ledger.ContainsKey(matchId)) continue;

            var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(file))
                .ToUnixTimeMilliseconds();
            _ledger[matchId] = new ArchiveEntry(
                matchId, stamp, stamp, Incomplete: false);
            added++;
        }

        if (added > 0)
        {
            Console.Error.WriteLine(
                $"{added} archived match(es) were missing from the ledger and were " +
                "re-indexed from the raw files.");
            SaveLedger();
        }
    }

    public bool Contains(string matchId)
    {
        lock (_ledgerLock) return _ledger.ContainsKey(matchId);
    }

    /// <summary>
    /// Every archived match id, as a snapshot.
    /// </summary>
    /// <remarks>
    /// Materialized rather than handed out as <c>_ledger.Keys</c>, which is a live view:
    /// a caller enumerating it while another thread writes a match throws. That could
    /// not happen while every caller had its own copy of the ledger — the bug in #146
    /// was hiding this one — and a build enumerating ids while the poll loop captures
    /// is exactly the pair that now shares an instance.
    /// </remarks>
    public IReadOnlyList<string> MatchIds()
    {
        lock (_ledgerLock) return _ledger.Keys.ToList();
    }

    public ArchiveEntry? Meta(string matchId)
    {
        lock (_ledgerLock) return _ledger.TryGetValue(matchId, out var e) ? e : null;
    }

    /// <summary>
    /// Writes a match. Returns false when the archived copy already knows at least as
    /// much. An incomplete entry is replaced by a complete one so a match split across
    /// Player-prev.log and Player.log heals on the next run.
    /// <para>
    /// A capture that found gaps the stored copy has none of also wins, even when both
    /// are complete. Gap detection arrived after matches had already been archived, and
    /// the markers that prove them sit in logs that have not rotated yet — so without
    /// this, the only matches known to be missing data would stay silent forever, which
    /// is precisely the outcome the detection exists to prevent. Gaps only ever
    /// increase, so a healed match settles after one rewrite instead of churning.
    /// </para>
    /// <para>
    /// A capture that carries a decklist the stored copy lacks wins for exactly the
    /// same reason, and settles the same way: the slicer used to throw those lines
    /// away, so every match archived before it stopped doing that is stored without
    /// one even though the line is still sitting in a log that has not rotated.
    /// </para>
    /// </summary>
    public bool Write(MatchSlice slice)
    {
        lock (_ledgerLock)
        {
            _ledger.TryGetValue(slice.MatchId, out var existing);
            if (existing is not null)
            {
                // Never trade a finished capture for a partial one, whatever else it found.
                if (slice.Incomplete && !existing.Incomplete) return false;

                var completesTheMatch = existing.Incomplete && !slice.Incomplete;
                var revealsNewGaps = slice.Gaps > existing.Gaps;
                var revealsTheDeck = slice.HasDeck && !existing.HasDeck;
                if (!completesTheMatch && !revealsNewGaps && !revealsTheDeck) return false;
            }

            // Re-capturing a match must not silently unfavourite it.
            var favorite = existing?.Favorite ?? false;

            var path = Path.Combine(_rawDir, $"{slice.MatchId}.json.gz");
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var w = new StreamWriter(gz))
            {
                foreach (var line in slice.RawLines) w.WriteLine(line);
            }

            _ledger[slice.MatchId] = new ArchiveEntry(
                slice.MatchId, slice.StartedAtMs, slice.EndedAtMs, slice.Incomplete, favorite,
                Gaps: slice.Gaps, HasDeck: slice.HasDeck);
            SaveLedger();
            return true;
        }
    }

    /// <summary>Marks a match as kept, so pruning will never remove it.</summary>
    public bool SetFavorite(string matchId, bool favorite)
    {
        lock (_ledgerLock)
        {
            if (!_ledger.TryGetValue(matchId, out var e)) return false;
            _ledger[matchId] = e with { Favorite = favorite };
            SaveLedger();
            return true;
        }
    }

    /// <summary>
    /// Drops the oldest matches until at most <paramref name="keep"/> remain, and
    /// returns what was removed so the caller can delete the rendered output too.
    /// Favourites are never counted against the cap and never removed — a cap of 60
    /// with 70 favourites keeps all 70.
    /// </summary>
    public IReadOnlyList<string> Prune(int keep)
    {
        lock (_ledgerLock)
        {
            var doomed = Prunable(keep);

            foreach (var id in doomed)
            {
                var path = Path.Combine(_rawDir, $"{id}.json.gz");
                if (File.Exists(path)) File.Delete(path);
                _ledger.Remove(id);
            }

            if (doomed.Count > 0) SaveLedger();
            return doomed;
        }
    }

    /// <summary>
    /// Which matches <see cref="Prune"/> would remove, oldest first, without removing
    /// them. Empty when the cap is off or nothing is over it.
    /// </summary>
    /// <remarks>
    /// Split out from the deletion so a caller can ask what is about to happen before
    /// it happens. Nothing here is recoverable — the archive is the only copy, and
    /// File.Delete does not go via the recycle bin — so "how many would go" has to be
    /// answerable without the answer being "they went" (#133).
    /// </remarks>
    public IReadOnlyList<string> Prunable(int keep)
    {
        if (keep <= 0) return [];

        lock (_ledgerLock)
        {
            var prunable = _ledger.Values
                .Where(e => !e.Favorite)
                .OrderBy(e => e.StartedAtMs)
                .ToList();

            // Measured against the prunable matches, not the whole ledger. Counting
            // favourites into the total while taking the excess from a list that excludes
            // them meant every favourite silently cost one ordinary match its place — and
            // once a player had favourited `keep` matches, every match afterwards was
            // captured and deleted in the same run, while capture still reported it as
            // captured. The documented rule, in the README and in the summary above, is
            // that the cap applies to everything except favourites.
            var excess = prunable.Count - keep;
            if (excess <= 0) return [];

            return prunable.Take(excess).Select(e => e.MatchId).ToList();
        }
    }

    /// <summary>How many matches the archive holds, favourites included.</summary>
    public int Count
    {
        get { lock (_ledgerLock) return _ledger.Count; }
    }

    public IReadOnlyList<string> ReadLines(string matchId)
    {
        var path = Path.Combine(_rawDir, $"{matchId}.json.gz");
        if (!File.Exists(path)) return [];
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        var lines = new List<string>();
        while (r.ReadLine() is { } line)
            if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Saves the ledger so that a crash can never leave it torn.
    /// </summary>
    /// <remarks>
    /// This file is rewritten on every captured match and every star click, and it is
    /// the only map from match id to metadata — a truncated write used to make every
    /// archived match unreachable at once (#115). The write goes to a temp file and
    /// swaps in atomically; the swap keeps the previous generation as
    /// <c>index.json.bak</c>, which is what the constructor falls back to when the
    /// main file will not parse. The backup is at most one save stale, and
    /// <see cref="Reindex"/> covers whatever it misses.
    /// </remarks>
    private void SaveLedger()
    {
        var tmp = _ledgerPath + ".tmp";
        File.WriteAllText(tmp,
            JsonSerializer.Serialize(_ledger, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            if (File.Exists(_ledgerPath)) File.Replace(tmp, _ledgerPath, _ledgerPath + ".bak");
            else File.Move(tmp, _ledgerPath);
        }
        catch
        {
            // A swap that failed leaves the temp file holding nothing the in-memory
            // ledger does not — the next successful save rewrites both — so it is
            // removed rather than left to look like something worth recovering.
            try { File.Delete(tmp); } catch (IOException) { /* the next save overwrites it */ }
            throw;
        }
    }
}
