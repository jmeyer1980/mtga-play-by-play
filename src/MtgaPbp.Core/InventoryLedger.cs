using System.Text.Json;

namespace MtgaPbp.Core;

/// <summary>
/// The record of what a player held over time, kept beside the match archive.
/// </summary>
/// <remarks>
/// Separate from <see cref="RawArchive"/> on purpose. That archive is keyed by match and
/// is the source of truth for matches everywhere else in the tool; this is not per-match
/// data — the snapshots arrive on session and course events, 30 of them against a day
/// that produced about 40 matches — and threading it through the match archive would put
/// a second kind of thing into the one structure everything else relies on.
/// <para>
/// The archive cannot be backfilled. These snapshots were discarded at capture time for
/// the whole life of the project and the logs holding the old ones are gone, so this
/// starts empty however long the archive is. Its worth is the future it records (#51).
/// </para>
/// </remarks>
public sealed class InventoryLedger
{
    private readonly string _path;
    private readonly List<InventorySnapshot> _entries;
    private InventorySnapshot? _newest;

    public InventoryLedger(string archiveDir)
    {
        _path = Path.Combine(archiveDir, "inventory.json");
        _entries = Load(_path);
    }

    public IReadOnlyList<InventorySnapshot> Entries => _entries;

    /// <summary>
    /// Offers one scanned envelope to the ledger. Anything that is not an inventory
    /// snapshot is ignored, so this can be hung off the main scan without filtering.
    /// </summary>
    public void Observe(JsonElement root)
    {
        if (Inventory.TryRead(root) is { } snapshot) _newest = snapshot;
    }

    /// <summary>
    /// Records where this capture left the player, and says whether anything was written.
    /// </summary>
    /// <remarks>
    /// Only the newest snapshot of the run is considered, and this is what makes a
    /// re-read harmless. Every capture reads the whole log from the start, so the same
    /// snapshots arrive over and over; a rule that compared each sighting against the
    /// stored tail would re-append the middle of any sequence that moved and moved back
    /// — 710 → 560 → 710 would add 560 again on every single capture. Where the log
    /// leaves the player is the one thing that is still true by the end of it.
    /// <para>
    /// The cost is that a change made and undone between two captures is never seen.
    /// With <c>watch</c> re-reading every few seconds that window is seconds wide.
    /// </para>
    /// </remarks>
    public bool Commit()
    {
        if (_newest is not { } snapshot) return false;
        if (_entries.Count > 0 && _entries[^1].SameHoldings(snapshot)) return false;

        _entries.Add(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Options));
        return true;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// What is on disk, or nothing when there is no ledger yet.
    /// </summary>
    /// <remarks>
    /// A file that will not parse is moved aside rather than overwritten. It is the only
    /// copy of a history that cannot be rebuilt from anything else — unlike a transcript,
    /// which can always be re-rendered from the match archive — so losing it silently to
    /// the next successful capture would be the one unrecoverable mistake this class can
    /// make.
    /// </remarks>
    private static List<InventorySnapshot> Load(string path)
    {
        if (!File.Exists(path)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<InventorySnapshot>>(File.ReadAllText(path))
                   ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            var aside = path + ".unreadable";
            try
            {
                File.Move(path, aside, overwrite: true);
                Console.Error.WriteLine($"Could not read {path}; kept it as {aside}.");
            }
            catch (IOException)
            {
                // Could not even move it. Say so and start fresh rather than throw:
                // a capture that cannot write a currency total must still archive the
                // match it was called for.
                Console.Error.WriteLine($"Could not read or move {path}; starting a new ledger.");
            }
            return [];
        }
    }
}
