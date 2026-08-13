using System.Text.RegularExpressions;

namespace MtgaPbp.Core;

/// <summary>One card you own, and how many copies.</summary>
public sealed record OwnedCard(string Name, int Count);

/// <summary>
/// Reads a collection exported from somewhere else.
/// </summary>
/// <remarks>
/// Arena stopped writing the collection to its log. It is not a gap in what this tool
/// reads: across a clean login, a craft and a full played session — 21,651 lines — there
/// is no collection endpoint, no <c>GetPlayerCardsV3</c>, no <c>GetPlayerCollection</c>,
/// and no line holding a run of card-id-to-quantity pairs in either the inline or the
/// pretty-printed shape. Companion apps that show a collection read it out of the game's
/// process memory instead.
/// <para>
/// So this tool imports one rather than extracting it. That keeps it a program that only
/// ever reads files, which is the whole of its security posture, and it means any source
/// works — a tracker's copy button, a memory-scanning script, a hand-written list.
/// </para>
/// <para>
/// The format is Arena's own decklist text, because every source already emits it and
/// Arena itself imports it. Set codes, collector numbers and rarities are accepted and
/// discarded: this answers "do I own it and how many", and a card owned in two printings
/// is still that one card.
/// </para>
/// </remarks>
public static class CollectionFile
{
    /// <summary>
    /// <c>4 Hare Apparent</c>, <c>4x Hare Apparent</c>, and the same with any of
    /// <c>(FDN) 123</c>, <c>#123</c> or <c>[rare]</c> trailing.
    /// </summary>
    private static readonly Regex Line = new(
        @"^\s*(?<count>\d+)\s*[xX]?\s+(?<name>.+?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Trailing printing detail. Removed rather than parsed — see the class remarks.
    /// </summary>
    private static readonly Regex Trailing = new(
        @"(\s*(\([A-Za-z0-9_]{2,5}\)|\[[^\]]+\]|#\S+|<[^>]+>))+\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Headings the exporters write. Matched whole so a card named "Deck of Cards" is
    /// not mistaken for one — and they can only ever match a line with no leading count,
    /// which the line pattern already rejects.
    /// </summary>
    private static readonly HashSet<string> Headings = new(StringComparer.OrdinalIgnoreCase)
    {
        "deck", "sideboard", "commander", "companion", "maybeboard", "about"
    };

    /// <summary>
    /// Parses a collection, merging repeated names and keeping the file's order of first
    /// appearance so a diff against it reads the way the file did.
    /// </summary>
    /// <param name="unreadable">
    /// Lines that looked like they meant something and could not be read — anything left
    /// after blanks, comments and headings are dropped. Reported rather than swallowed:
    /// a collection silently missing a tenth of its cards would quietly answer "you do
    /// not own that" about cards you do.
    /// </param>
    public static IReadOnlyList<OwnedCard> Parse(
        IEnumerable<string> lines, out IReadOnlyList<string> unreadable)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var bad = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('#')) continue;
            if (line.All(c => c is '=' or '-' or '_')) continue;
            if (Headings.Contains(line)) continue;

            var m = Line.Match(line);
            if (!m.Success)
            {
                // Only a line beginning with a count could have been a card, so only
                // those are worth reporting. The exporters write a preamble — "MTGA
                // Collection Export", "Exported: ...", "Unique cards: 1578" — and prose
                // that was never an entry is not a card anyone lost.
                if (char.IsAsciiDigit(line[0])) bad.Add(line);
                continue;
            }

            var name = Trailing.Replace(m.Groups["name"].Value, "").Trim();
            if (name.Length == 0) { bad.Add(line); continue; }

            if (!int.TryParse(m.Groups["count"].Value, out var n) || n <= 0)
            {
                bad.Add(line);
                continue;
            }

            if (counts.TryGetValue(name, out var already)) counts[name] = already + n;
            else { counts[name] = n; order.Add(name); }
        }

        unreadable = bad;
        return order.Select(n => new OwnedCard(n, counts[n])).ToList();
    }
}
