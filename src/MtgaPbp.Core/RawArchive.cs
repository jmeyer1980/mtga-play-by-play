using System.IO.Compression;
using System.Text.Json;

namespace MtgaPbp.Core;

public sealed record ArchiveEntry(string MatchId, long StartedAtMs, long EndedAtMs, bool Incomplete);

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
    /// Writes a match. Returns false when an equally-or-more complete copy is
    /// already archived. An incomplete entry is replaced by a complete one so a
    /// match split across Player-prev.log and Player.log heals on the next run.
    /// </summary>
    public bool Write(MatchSlice slice)
    {
        if (_ledger.TryGetValue(slice.MatchId, out var existing) &&
            (!existing.Incomplete || slice.Incomplete))
            return false;

        var path = Path.Combine(_rawDir, $"{slice.MatchId}.json.gz");
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var w = new StreamWriter(gz))
        {
            foreach (var line in slice.RawLines) w.WriteLine(line);
        }

        _ledger[slice.MatchId] =
            new ArchiveEntry(slice.MatchId, slice.StartedAtMs, slice.EndedAtMs, slice.Incomplete);
        SaveLedger();
        return true;
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
