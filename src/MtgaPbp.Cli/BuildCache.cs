using System.Text.Json;
using System.Text.Json.Serialization;
using MtgaPbp.Render;

namespace MtgaPbp.Cli;

/// <summary>
/// What a build already knows about one match, so that the next build can leave it
/// alone.
/// </summary>
/// <param name="RawSize">The archived slice's size, as the freshness check's first half.</param>
/// <param name="RawModifiedMs">And when it was last written, as the second.</param>
/// <param name="Summary">
/// The row this match contributes to the index — <em>without</em> its star. See
/// <see cref="BuildCache"/> for why that one field is deliberately not here.
/// </param>
/// <param name="Unresolved">
/// The card names this match could not resolve. Cached because unresolved.txt is
/// derived from every match at once: a build that skipped a match and forgot its
/// unresolved names would quietly shorten that file to only the matches it rebuilt.
/// </param>
public sealed record CachedMatch(
    long RawSize,
    long RawModifiedMs,
    string? NewerId,
    string? NewerWhen,
    string? OlderId,
    string? OlderWhen,
    MatchSummary Summary,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Lets a build skip the matches nothing has happened to.
/// </summary>
/// <remarks>
/// A build used to re-derive the whole archive on every event: gunzip, re-parse and
/// unconditionally rewrite the page and the markdown for every match, per captured match
/// and per star click. Measured at 1,223 matches that is around 2 GB decompressed and
/// 137 MB across 2,446 files, every time, growing forever — and `watch` queues those back
/// to back (#122).
/// <para>
/// The star is the one thing about a match that changes without the match changing, so
/// <see cref="CachedMatch.Summary"/> is stored as the renderer produced it and the
/// favourite flag is re-applied from the ledger on every build. Caching it would leave a
/// star that had just been clicked reading as unclicked until something else happened to
/// that match.
/// </para>
/// <para>
/// The whole cache is thrown away when the tool itself changes. The issue this came from
/// proposed a renderer-version constant to bump by hand; a constant somebody has to
/// remember fails silently and permanently the first time they forget — stale pages, no
/// error, nothing on the page to say why, which is the very failure
/// <see cref="BuildInfo"/> exists to make visible. <see cref="BuildInfo.Version"/> already
/// carries the version and the commit, so it moves whenever the code does. The price is
/// one full rebuild after an upgrade, against thousands of cached captures between them.
/// </para>
/// </remarks>
public sealed class BuildCache
{
    /// <summary>Bumped when the shape of this file changes, not when the renderer does.</summary>
    private const int SchemaVersion = 1;

    private const string FileName = ".build-cache.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Dictionary<string, CachedMatch> _known;
    private readonly string? _knownCardDb;
    private readonly Dictionary<string, CachedMatch> _next = new(StringComparer.Ordinal);

    private BuildCache(Dictionary<string, CachedMatch> known, string? knownCardDb = null)
    {
        _known = known;
        _knownCardDb = knownCardDb;
    }

    /// <summary>
    /// The cache written beside a previous build's output, or an empty one.
    /// </summary>
    /// <remarks>
    /// Empty on anything unexpected — a missing file, a file from an older schema, a
    /// build of a different version of the tool, or one that will not parse. Every one of
    /// those means "re-derive everything", which is only ever slow. There is no failure
    /// here worth reporting to a user, because the correct output is produced either way.
    /// </remarks>
    public static BuildCache Load(string outputDir, bool ignore = false)
    {
        if (ignore) return new BuildCache([]);

        try
        {
            var path = Path.Combine(outputDir, FileName);
            if (!File.Exists(path)) return new BuildCache([]);

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), Json);
            if (file is null
                || file.SchemaVersion != SchemaVersion
                || file.Tool != BuildInfo.Version
                || file.Matches is null)
                return new BuildCache([]);

            return new BuildCache(
                new Dictionary<string, CachedMatch>(file.Matches, StringComparer.Ordinal),
                file.CardDb);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new BuildCache([]);
        }
    }

    /// <summary>
    /// What the previous build recorded for this match, when nothing it depended on has
    /// moved since — or null, meaning render it.
    /// </summary>
    /// <param name="cardDb">
    /// The card database's own stamp. Names, faces and ability text all come from it, so
    /// a database that has been updated can change a page whose match never moved.
    /// </param>
    public CachedMatch? Reusable(
        string matchId, long rawSize, long rawModifiedMs,
        Neighbours neighbours, string gamePath, string textPath, string cardDb)
    {
        // Compared per match rather than at load, so the rule sits with the rest of
        // them and a test can reach it.
        if (_knownCardDb != cardDb) return null;

        if (!_known.TryGetValue(matchId, out var hit)) return null;

        if (hit.RawSize != rawSize || hit.RawModifiedMs != rawModifiedMs) return null;

        // The links on the page name the matches either side of this one, so appending a
        // match makes exactly one older page wrong — the one that used to be newest.
        if (hit.NewerId != neighbours.NewerId || hit.NewerWhen != neighbours.NewerWhen
            || hit.OlderId != neighbours.OlderId || hit.OlderWhen != neighbours.OlderWhen)
            return null;

        // Cheap, and the only thing standing between a deleted output file and a report
        // that links to a page which is not there.
        if (!File.Exists(gamePath) || !File.Exists(textPath)) return null;

        return hit;
    }

    /// <summary>Remembers a match for the next build, whether it was rendered or reused.</summary>
    public void Keep(string matchId, CachedMatch entry) => _next[matchId] = entry;

    /// <summary>
    /// Writes what this build learned, replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// Only at the end, and only what this build actually saw: a match that has left the
    /// archive leaves the cache with it, and a build that was interrupted writes nothing,
    /// so the next one is merely full rather than wrong. Failure is silence for
    /// <see cref="Load"/>'s reason — the output is already correct, and a cache is a way
    /// of being faster, never a way of being right.
    /// </remarks>
    public void Save(string outputDir, string cardDb)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(outputDir, FileName),
                JsonSerializer.Serialize(
                    new CacheFile(SchemaVersion, BuildInfo.Version, cardDb, _next), Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or NotSupportedException)
        {
            // Next build does the work again, which is the same answer more slowly.
        }
    }

    private sealed record CacheFile(
        int SchemaVersion,
        string Tool,
        string CardDb,
        Dictionary<string, CachedMatch>? Matches);
}
