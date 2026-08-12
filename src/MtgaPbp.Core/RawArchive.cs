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

    public RawArchive(string archiveRoot)
    {
        _rawDir = Path.Combine(archiveRoot, "raw");
        Directory.CreateDirectory(_rawDir);
        _ledgerPath = Path.Combine(archiveRoot, "index.json");
        _ledger = File.Exists(_ledgerPath)
            ? JsonSerializer.Deserialize<Dictionary<string, ArchiveEntry>>(
                  File.ReadAllText(_ledgerPath)) ?? []
            : [];
    }

    public bool Contains(string matchId) => _ledger.ContainsKey(matchId);

    public IEnumerable<string> MatchIds() => _ledger.Keys;

    public ArchiveEntry? Meta(string matchId) =>
        _ledger.TryGetValue(matchId, out var e) ? e : null;

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

    /// <summary>Marks a match as kept, so pruning will never remove it.</summary>
    public bool SetFavorite(string matchId, bool favorite)
    {
        if (!_ledger.TryGetValue(matchId, out var e)) return false;
        _ledger[matchId] = e with { Favorite = favorite };
        SaveLedger();
        return true;
    }

    /// <summary>
    /// Drops the oldest matches until at most <paramref name="keep"/> remain, and
    /// returns what was removed so the caller can delete the rendered output too.
    /// Favourites are never counted against the cap and never removed — a cap of 60
    /// with 70 favourites keeps all 70.
    /// </summary>
    public IReadOnlyList<string> Prune(int keep)
    {
        if (keep <= 0) return [];

        var prunable = _ledger.Values
            .Where(e => !e.Favorite)
            .OrderBy(e => e.StartedAtMs)
            .ToList();

        var excess = _ledger.Count - keep;
        if (excess <= 0) return [];

        var removed = new List<string>();
        foreach (var entry in prunable.Take(excess))
        {
            var path = Path.Combine(_rawDir, $"{entry.MatchId}.json.gz");
            if (File.Exists(path)) File.Delete(path);
            _ledger.Remove(entry.MatchId);
            removed.Add(entry.MatchId);
        }

        if (removed.Count > 0) SaveLedger();
        return removed;
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

    private void SaveLedger() =>
        File.WriteAllText(_ledgerPath,
            JsonSerializer.Serialize(_ledger, new JsonSerializerOptions { WriteIndented = true }));
}
