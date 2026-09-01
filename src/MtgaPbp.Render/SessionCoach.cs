using MtgaPbp.Core;

namespace MtgaPbp.Render;

/// <summary>
/// Something worth saying between two games, or nothing at all.
/// </summary>
/// <param name="Kind">Which rule produced it, so a caller can silence one and keep another.</param>
/// <param name="Text">The sentence to show. Already a suggestion, never a verdict.</param>
/// <param name="Deck">The deck it is about.</param>
/// <param name="NextUp">What to switch to, when there is an obvious candidate.</param>
public sealed record Nudge(NudgeKind Kind, string Text, string Deck, string? NextUp);

public enum NudgeKind
{
    /// <summary>Three losses in a row this sitting. A suggestion to rotate, nothing more.</summary>
    Rotate,

    /// <summary>A deck has finished its learning window and now has a rate worth knowing.</summary>
    Verdict
}

/// <summary>
/// Watches the sitting in progress and says something only when it is useful.
/// </summary>
/// <remarks>
/// This replaces a rule that read "bench the deck after 3 consecutive or 5 total losses
/// in a session". That rule was retired because it fired in 22 of the archive's 28
/// sessions — 88% of the sessions with eight or more games — which means it was not
/// detecting a failing deck, it was detecting that somebody had played for a while. The
/// defect was that it had no denominator: five losses in ten games is a rough night and
/// five in thirty-three at 60% is a good one, and a bare count cannot tell those apart.
/// <para>
/// It failed a second way that mattered more. It was a loss counter, so the only quantity
/// it ever asked anyone to track was how many times they had failed that night — which is
/// how a 15-7 session comes to feel like a losing one.
/// </para>
/// <para>
/// So the one rule is three rules, because it was doing three jobs badly. A deck's first
/// games are a <see cref="LearningWindow"/> during which nothing is judged; a rate is
/// only worth stating once <see cref="EvaluationAt"/> games have been played; and the
/// in-session nudge is a rotation <em>suggestion</em>, which is the one thing the old
/// rule got right and then wrapped in a verdict.
/// </para>
/// </remarks>
public static class SessionCoach
{
    /// <summary>
    /// How many games a deck gets before anything is said about how good it is. Losses
    /// inside the window are what learning a deck costs, not evidence against it.
    /// </summary>
    public const int LearningWindow = 20;

    /// <summary>When a deck's win rate is finally worth quoting.</summary>
    public const int EvaluationAt = 30;

    /// <summary>Below this, the deck is worth rebuilding. Above <see cref="Keeper"/>, it is working.</summary>
    public const double Rebuild = 0.45;

    /// <inheritdoc cref="Rebuild"/>
    public const double Keeper = 0.55;

    /// <summary>Consecutive losses in one sitting before a rotation is suggested.</summary>
    public const int RotateAfter = 3;

    /// <summary>
    /// Decided games a deck needs in the sitting in progress before it can be the one
    /// recommended. Two, because one game is a coin flip and a pattern needs at least
    /// two observations — "not until I have looped once or twice and the pattern has
    /// been identified", and this is that sentence as a number.
    /// </summary>
    public const int LoopedAt = 2;

    /// <summary>
    /// What to say now, or null. Call it once per finished match.
    /// </summary>
    /// <param name="rows">Every match in the archive, in any order.</param>
    /// <param name="stats">
    /// The clustering already computed for the index, so the coach and the report agree
    /// about which matches are one deck.
    /// </param>
    /// <param name="silenced">
    /// Decks the player has already declined a rotation for this sitting. A suggestion
    /// that returns every single game is a suggestion nobody reads by the third time.
    /// </param>
    /// <param name="nowMs">
    /// The moment being asked about, as Unix milliseconds. When given, a sitting whose
    /// last match is further back than <see cref="Sessions.Gap"/> produces nothing:
    /// the whole value of this is landing between two games, and a report opened the
    /// next morning greeting somebody with "you are 0-3" is describing a night that
    /// finished hours ago. Omit it to ask about the archive rather than about now.
    /// </param>
    /// <param name="suggestRotation">
    /// Whether the rotation rule may speak at all. False switches off <see
    /// cref="NudgeKind.Rotate"/> for good rather than for one deck for one evening,
    /// which is what <paramref name="silenced"/> does. The two are different answers to
    /// different questions: "not this deck tonight" and "never, I am playing the deck I
    /// play". A player who has decided which deck is theirs is not choosing between
    /// decks, and to them a rotation suggestion is not advice, it is a losing streak
    /// read back to them by their own tools. The verdict is unaffected — see the
    /// fall-through below.
    /// </param>
    public static Nudge? Check(
        IReadOnlyList<MatchSummary> rows,
        IndexStats stats,
        IReadOnlySet<string>? silenced = null,
        long? nowMs = null,
        bool suggestRotation = true)
    {
        var labelOf = stats.ByDeck
            .Where(d => d.Slug is not null)
            .ToDictionary(d => d.Slug!, d => d.Name, StringComparer.Ordinal);

        // The same sittings the panel shows, not a second computation of them.
        var sessions = stats.Sessions;
        if (sessions.Count == 0) return null;

        // The sitting in progress is the newest one, and Sessions hands them back
        // newest first.
        var tonight = sessions[0];
        var byId = rows.GroupBy(r => r.MatchId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Oldest first: a losing streak is a claim about the order games were played in.
        var played = tonight.MatchIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .ToList();
        if (played.Count == 0) return null;

        var last = played[^1];
        if (nowMs is { } now && now - last.SortKey > (long)Sessions.Gap.TotalMilliseconds) return null;
        if (stats.DeckOf.GetValueOrDefault(last.MatchId) is not { } slug) return null;
        var deck = labelOf.GetValueOrDefault(slug, slug);

        // Counted only over this deck's games in this sitting. Two decks alternated
        // through an evening are two runs, and reading the session as one stream would
        // credit a streak to whichever deck happened to be holding the controller.
        var mine = played.Where(p => stats.DeckOf.GetValueOrDefault(p.MatchId) == slug).ToList();
        var streak = 0;
        foreach (var p in Enumerable.Reverse(mine))
        {
            if (Sessions.Outcome(p) != 'L') break;
            streak++;
        }

        if (suggestRotation && streak >= RotateAfter && silenced?.Contains(slug) != true)
        {
            var next = NextUp(stats, slug);
            return new Nudge(
                NudgeKind.Rotate,
                $"{deck} is 0-{streak} this session." +
                (next is null ? " Want to switch it up?" : $" Next in rotation: {next}. Keep playing?"),
                deck,
                next);
        }

        // The verdict fires once, on the game that crosses the line, and only for a deck
        // that has left its learning window. Anything earlier is an opinion about a
        // sample too small to have one about.
        var record = stats.ByDeck.FirstOrDefault(d => d.Slug == slug);
        if (record is { Played: EvaluationAt } && record.WinRate is { } rate)
        {
            var verdict = rate < Rebuild
                ? $"worth a rebuild — {rate:P0} over {record.Played}"
                : rate > Keeper
                    ? $"a keeper — {rate:P0} over {record.Played}"
                    : $"holding even at {rate:P0} over {record.Played}";
            return new Nudge(NudgeKind.Verdict, $"{deck} has {EvaluationAt} games in: {verdict}.", deck, null);
        }

        return null;
    }

    /// <summary>
    /// The deck worth switching to, or null when tonight has not yet said which one
    /// that is.
    /// </summary>
    /// <remarks>
    /// The question is about the sitting in progress, so the sitting in progress is what
    /// answers it. A candidate must have <see cref="LoopedAt"/> decided games tonight —
    /// until the rotation has looped, there is no pattern to read and nothing is named —
    /// and the candidates are ranked by how tonight has gone, with the lifetime rate
    /// only breaking ties. The <see cref="LearningWindow"/> filter stays: a deck still
    /// being learned is not judged, in either direction.
    /// <para>
    /// This used to rank by lifetime win rate, with tonight entering only as a demotion.
    /// On the board as a standing line, that meant recommending whatever led the archive
    /// — the same deck every night, sight unseen, and with a stable whose bench sits
    /// below 50% lifetime, a standing suggestion to switch to a losing deck. A lifetime
    /// record answers "which deck is good", and the line is asking "which deck is good
    /// <em>tonight</em>".
    /// </para>
    /// <para>
    /// A deck on a losing run tonight is excluded outright, not demoted and named
    /// anyway. The old rule kept it as a last resort, reasoning that on a bad night
    /// every answer is imperfect; the player's actual complaint was the imperfect
    /// answers. Silence beats naming a deck the numbers cannot stand behind.
    /// </para>
    /// </remarks>
    public static string? NextUp(IndexStats stats, string? exclude)
    {
        // Tonight's pattern, per deck. Most-played first, so if two clusters ever share
        // a label the one with more of the evening behind it is the one read.
        var tonight = new Dictionary<string, SessionDeck>(StringComparer.Ordinal);
        foreach (var d in stats.Sessions.FirstOrDefault()?.Decks ?? [])
            if (d.Played >= LoopedAt && d.Streak < 2)
                tonight.TryAdd(d.Name, d);

        return stats.ByDeck
            .Where(d => d.Slug is not null && d.Slug != exclude)
            .Where(d => d.Played >= LearningWindow && d.WinRate is not null)
            .Where(d => tonight.ContainsKey(d.Name))
            .OrderByDescending(d => (double)tonight[d.Name].Won / tonight[d.Name].Played)
            .ThenByDescending(d => d.WinRate)
            .Select(d => d.Name)
            .FirstOrDefault();
    }
}
