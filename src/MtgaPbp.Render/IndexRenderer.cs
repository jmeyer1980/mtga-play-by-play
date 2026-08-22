using System.Globalization;
using System.Net;
using System.Text;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public sealed record MatchSummary(
    string MatchId, string Date, long SortKey, string EventName,
    string Opponent, string Result, int Turns, bool Incomplete,
    IReadOnlyList<string> Cards, bool Favorite = false, bool HasGaps = false,

    /// <summary>
    /// How long the match ran, or null when the log does not know — which is every
    /// incomplete match. Carried as a <see cref="TimeSpan"/> rather than pre-formatted
    /// because the table needs it twice, abbreviated for the column and spelled out for
    /// a synthesiser, and a single string could only ever be one of those.
    /// </summary>
    TimeSpan? Length = null,

    /// <summary>
    /// The deck's colour identity as WUBRG letters, or null when the log carried no
    /// deck. Already derived — see <see cref="MtgaPbp.Core.DeckColors"/> — because by
    /// the time a transcript reaches this record the card ids it was worked out from
    /// are gone.
    /// </summary>
    string? Colors = null,

    /// <summary>
    /// The registered decklist, or empty when the log carried none. Carried rather than
    /// pre-grouped because which deck this is cannot be known from one match: it is
    /// decided by comparing every list in the archive against every other.
    /// </summary>
    IReadOnlyList<DeckEntry>? Deck = null,

    /// <summary>The first registered commander, when the format has one.</summary>
    string? Commander = null,

    /// <summary>
    /// Whether you were on the play in the first game, or null when the log never said —
    /// which is every match archived before the slicer kept <c>ConnectResp</c>. Null is
    /// not "on the draw", so anything counting this needs its own denominator.
    /// </summary>
    bool? OnThePlay = null);

public static class IndexRenderer
{
    public static MatchSummary Summarize(Transcript t) => new(
        t.MatchId,
        TranscriptSummary.Date(t).ToString("yyyy-MM-dd HH:mm"),
        t.StartedAtMs,
        t.EventName,
        t.Opponent?.ScreenName ?? "Opponent",
        TranscriptSummary.Result(t),
        TranscriptSummary.Turns(t),
        t.Incomplete,
        t.CardsSeen.OrderBy(c => c, StringComparer.Ordinal).ToList(),
        HasGaps: t.Gaps.Count > 0,
        Length: TurnClock.MatchLength(t),
        Colors: t.DeckColors,
        Deck: t.Deck,
        Commander: t.Commanders.FirstOrDefault(),
        // The seat active on turn one is the seat on the play, so this is "that seat was
        // mine". Null when the log never announced a turn, which is not the same as
        // having been on the draw.
        OnThePlay: t.Opening?.FirstPlayerSeat is { } first && t.You?.Seat is { } mine
            ? first == mine
            : null);

    /// <summary>
    /// Rows are rendered statically rather than built by script: the page then works
    /// with JavaScript disabled, the browser's own find-in-page sees every opponent
    /// and card name, and each link is a real anchor. Search is progressive
    /// enhancement over a data-search attribute — no fetch, which browsers block on
    /// file:// anyway.
    /// </summary>
    /// <param name="nudge">
    /// Something the coach wants to say about the sitting in progress, or null. Passed in
    /// rather than worked out here, because whether a night is still going is a question
    /// about the clock and this method is otherwise a pure function of its rows.
    /// </param>
    public static string Render(IEnumerable<MatchSummary> rows, Nudge? nudge = null)
    {
        var ordered = rows.OrderByDescending(r => r.SortKey).ToList();

        var body = new StringBuilder();
        if (ordered.Count == 0)
        {
            // No rows means nothing to search, so the field, the counter and the
            // footnotes are all omitted rather than rendered inert.
            body.Append(
                "<p class=\"empty\">No games archived yet. Play a match, then run " +
                "<code>mtga-pbp</code>.</p>");
        }
        else
        {
            // Worked out once: the panel reports the records, and every row carries the
            // deck it was played with so one click in the panel can filter to it.
            var stats = IndexStats.From(ordered);
            body.Append(Coach(nudge));
            body.Append(Panel(stats));

            // The counter is server-rendered rather than filled in by script: it is a
            // live region, and a region that gains its first text after load announces
            // that text. Starting it correct means the first announcement is a real
            // change, and the count is also right with JavaScript turned off.
            body.Append($"""
                <h2>Matches</h2>
                <label for="q">Search matches</label>
                <input id="q" type="search" placeholder="opponent, event, result, or card"
                       autocomplete="off" aria-describedby="count" />
                <p id="count" class="sub" role="status">{ordered.Count} of {ordered.Count} shown</p>
                <table id="rows">
                <caption class="vh">Archived matches, most recent first</caption>
                <thead><tr>{Col("Keep", Num)}{Col("Date", Num)}
                {Col("ID")}
                {Col("Event", Text)}{Col("Deck", Text)}
                {Col("Opponent", Text)}
                {Col("Result", Text)}{Col("Turns", Num)}
                {Col("Length", Num)}</tr></thead><tbody id="data">
                """);
            foreach (var r in ordered)
            {
                var cls = r.Result.StartsWith("Won", StringComparison.Ordinal) ? "win"
                    : r.Result.StartsWith("Drew", StringComparison.Ordinal) ? "draw" : "loss";
                // Both forms of the colours go in, so "wu" and "blue" each find the same
                // rows. A row with no deck contributes neither, which is what stops a
                // search for "white" from turning up matches whose colours nobody knows.
                var haystack = string.Join(' ',
                    r.Opponent, r.EventName, r.Result, r.Date, string.Join(' ', r.Cards),
                    r.Colors ?? "", r.Colors is null ? "" : DeckColors.Spoken(r.Colors),
                    // The deck token the panel filters by. Only matches whose log
                    // carried a decklist have one, which is what keeps a search for a
                    // deck from turning up matches nobody knows the deck of.
                    stats.DeckOf.TryGetValue(r.MatchId, out var deck) ? $"deck:{deck}" : "")
                    .ToLowerInvariant();

                // The star is a toggle button, so its state rides on aria-pressed and
                // its name stays constant; the glyph is decoration and is hidden from
                // assistive technology, which would otherwise read it as "white star".
                // It ships disabled because keeping a match needs the local server —
                // an unavailable control is better than one that silently does nothing.
                //
                // Both row controls are named for what they DO and nothing else. They
                // used to name the match as well — "Keep the 2026-08-19 23:46 match
                // against X" — so that an archive of them was not a list of identical
                // entries. That had the trade backwards. The date is this row's header
                // and the opponent is a column of the same row, so a screen reader
                // announces both before it reaches the cell: every identity the name
                // could carry is one the reader has just been given, and the verb — the
                // only part they did not have — arrived last, after a timestamp read
                // digit by digit (#48).
                //
                // There is no wording that is both self-identifying and non-repetitive,
                // because self-identifying means repeating the row. Naming the match by
                // opponent alone was measured and does not work either: 97 of 582
                // matches share an opponent, and one opponent appears sixteen times.
                //
                // The list the long name was protecting is NVDA's elements list, and at
                // 582 rows that holds 582 keep buttons whatever they are called. Nobody
                // finds a match that way; they reach the row and then act. That cost is
                // theoretical, and the one it was paying fell on every row.
                body.Append($"""
                    <tr data-search="{E(haystack)}">
                    <td{Key(r.Favorite ? 1 : 0)}><button class="star{(r.Favorite ? " on" : "")}" type="button" disabled="disabled"
                        aria-pressed="{(r.Favorite ? "true" : "false")}"
                        aria-describedby="keep-note" data-id="{E(r.MatchId)}"
                        aria-label="Keep"
                        ><span aria-hidden="true">{(r.Favorite ? "★" : "☆")}</span></button></td>
                    <th scope="row"{Key(r.SortKey)}><a href="games/{E(Uri.EscapeDataString(r.MatchId))}.html">{E(r.Date)}</a></th>
                    <td><button class="copyid" type="button" data-id="{E(r.MatchId)}"
                        aria-label="Copy game ID"
                        ><span aria-hidden="true">⧉</span></button></td>
                    <td{Key(r.EventName)}>{E(r.EventName)}</td><td class="deck"{Key(r.Colors)}>{Colors(r)}</td>
                    <td{Key(r.Opponent)}>{E(r.Opponent)}</td>
                    <td class="{cls}"{Key(r.Result)}>{E(r.Result)}{Incomplete(r)}{Gaps(r)}</td>
                    <td{Key(r.Turns)}>{r.Turns}</td>
                    <td{Key(r.Length?.TotalSeconds)}>{Length(r)}</td></tr>
                    """);
            }
            body.Append("</tbody></table>");

            if (ordered.Any(r => r.Incomplete))
            {
                body.Append("""
                    <p class="note" id="incomplete-note"><span aria-hidden="true">*</span>
                    Incomplete — the log was rotated before the match finished.</p>
                    """);
            }

            // A separate mark and a separate footnote, because this is a separate
            // failure: the match above ran out of log, this one ran out of truth.
            if (ordered.Any(r => r.HasGaps))
            {
                body.Append("""
                    <p class="note" id="gaps-note"><span aria-hidden="true">†</span>
                    Missing data — the log left out part of the match, so the transcript
                    is not a complete account of it.</p>
                    """);
            }

            body.Append("""
                <p class="note" id="keep-note">Keeping a match protects it from pruning.
                It works while the report is served by <code>mtga-pbp watch</code>; opened
                from a file this page is read-only, so the Keep buttons are disabled.</p>
                <p id="status" class="vh" role="status"></p>
                """);
        }

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>MTGA Play-by-Play</title>
            <style>{{Css}}</style></head><body>
            <main>
            <h1>MTGA Play-by-Play</h1>
            <p class="sub">{{ordered.Count}} game{{(ordered.Count == 1 ? "" : "s")}} archived<span
                id="live"> <span aria-hidden="true">· </span>live updating</span></p>
            {{body}}
            </main>
            <footer class="build">{{E(BuildInfo.Line)}}</footer>
            <script>{{Script}}</script>
            </body></html>
            """;
    }

    /// <summary>
    /// The asterisk is a sighted-reader shorthand explained by the footnote below the
    /// table; screen readers get the word instead, because "star" next to "Lost 0-1"
    /// means nothing on its own.
    /// </summary>
    private static string Incomplete(MatchSummary r) => r.Incomplete
        ? """<span aria-hidden="true"> *</span><span class="vh"> (incomplete)</span>"""
        : "";

    /// <summary>
    /// A dagger rather than a second asterisk, so the two footnotes stay tellable
    /// apart at a glance; and the spoken form says what it means, for the same reason
    /// the asterisk above does.
    /// </summary>
    private static string Gaps(MatchSummary r) => r.HasGaps
        ? """<span aria-hidden="true"> †</span><span class="vh"> (missing data)</span>"""
        : "";

    /// <summary>
    /// How long the match ran. The column shows "12m 4s" because a table column has to
    /// stay narrow; speech gets "12 minutes 4 seconds", because the abbreviation is a
    /// run of letters and digits sitting immediately after a turn count, and a
    /// synthesiser running the two together produces a number that is in the table
    /// nowhere. Same split as the decklist's "4×".
    /// <para>
    /// An incomplete match leaves the cell empty rather than showing how much of it was
    /// captured. That figure is a real one, but it is not the match's length, and in a
    /// column headed "Length" it would be read as one.
    /// </para>
    /// </summary>
    private static string Length(MatchSummary r) => r.Length is not { } d
        ? ""
        : Twin(E(TurnClock.Format(d)), E(TurnClock.Spoken(d)));

    /// <summary>
    /// Which deck was played, said in the only way Arena leaves open: its colours.
    /// Letters for the column, spelled out for a synthesiser, which would otherwise read
    /// "WU" as a word and "G" as the letter alone next to an opponent's name.
    /// <para>
    /// An empty cell when the log carried no decklist. 103 of the 476 matches archived
    /// so far predate the deck being captured at all, and anything printed there —
    /// "colourless", a dash, a question mark — would be a claim about a deck nobody has
    /// a record of.
    /// </para>
    /// <para>
    /// Letters rather than coloured pips. The information has to survive CSS being off
    /// and has to reach a screen reader, which means text either way; and five mana
    /// colours cannot all clear 4.5:1 against both a white and a near-black backdrop
    /// without a per-scheme table that would be one more thing to get wrong.
    /// </para>
    /// </summary>
    /// <summary>How a column compares, or nothing when it does not sort at all.</summary>
    private const string Num = "num";
    private const string Text = "text";

    /// <summary>
    /// A column heading, which script may turn into a sort control if the column says
    /// how it compares.
    /// </summary>
    /// <remarks>
    /// The rule lives on the header rather than being guessed from the cells, because
    /// guessing is how a column of "10, 9, 8" comes out "10, 8, 9". A column with no
    /// rule never becomes a control: the ID column holds a copy button and has no order
    /// worth putting rows in.
    /// <para>
    /// Rendered as plain text, not as a button. The button is added only once script
    /// has run, the same rule the star, the pager and the deck filter follow — a
    /// control that does nothing without script is worse than text that never claimed
    /// to be one.
    /// </para>
    /// </remarks>
    private static string Col(string label, string? sorts = null) =>
        sorts is null
            ? $"""<th scope="col">{E(label)}</th>"""
            : $"""<th scope="col" data-sort="{sorts}">{E(label)}</th>""";

    /// <summary>
    /// What a cell sorts by, as an attribute to drop inside its opening tag.
    /// </summary>
    /// <remarks>
    /// Sorting cannot read the rendered text, because most cells on this page carry two
    /// versions of themselves: a visible one hidden from assistive technology and a
    /// spoken twin hidden from sight. A length cell's text content is "4m4 minutes",
    /// and a win rate's is "58%58 percent". So the key comes from the data the row was
    /// built from, before either version existed.
    /// <para>
    /// That also fixes the orderings text could never get right on its own: a date
    /// sorts by the timestamp the archive stores rather than by the words shown, and a
    /// length sorts by seconds rather than by "9m" coming after "10m".
    /// </para>
    /// <para>
    /// A null value renders an empty key rather than no key, and the comparator reads
    /// an empty one as missing rather than as zero — an unfinished match has no length,
    /// and calling that nought seconds would file it among the fastest games ever
    /// played. Empty rather than absent so that every cell of a sortable column carries
    /// one: the comparator falls back to rendered text when a key is missing, and
    /// rendered text here is "4m4 minutes".
    /// </para>
    /// </remarks>
    private static string Key(object? value) => value switch
    {
        null => " data-key=\"\"",
        string s => $" data-key=\"{E(s.ToLowerInvariant())}\"",
        IFormattable n => $" data-key=\"{n.ToString(null, CultureInfo.InvariantCulture)}\"",
        _ => $" data-key=\"{E(value.ToString() ?? "")}\""
    };

    private static string Colors(MatchSummary r) => r.Colors is not { Length: > 0 } c
        ? ""
        : Twin(E(c), E(DeckColors.Spoken(c)));

    /// <summary>
    /// The record, above the table it summarises.
    /// </summary>
    /// <remarks>
    /// Real tables with scoped headers rather than a chart or a grid of divs: the index
    /// works with JavaScript off and is read with a screen reader, and a record is
    /// tabular data in the plainest sense. Rendered by the build for the same reason the
    /// rows are.
    /// <para>
    /// A deck name is plain text here and becomes a filter button only if script runs.
    /// Rendering it as a button that does nothing without script would be the thing this
    /// file already refuses to do for the pager and the star.
    /// </para>
    /// </remarks>
    private static string Panel(IndexStats s)
    {
        // Nothing with a result yet is itself worth a sentence. Returning nothing here
        // would leave an archive of unfinished matches with no record and no reason
        // given for its absence, which is the failure the notes below exist to prevent.
        var sb = new StringBuilder();
        sb.Append("""
            <section id="stats" aria-labelledby="stats-heading">
            <h2 id="stats-heading">Record</h2>
            """);

        if (s.Any)
        {
            sb.Append($"""
                <table class="stats"><caption class="vh">Overall record</caption>
                <thead><tr><th scope="col">Played</th><th scope="col">Record</th>
                <th scope="col">Win rate</th><th scope="col">Best streak</th></tr></thead>
                <tbody><tr><td>{s.Overall.Played}</td><td>{Record(s.Overall)}</td>
                <td>{Rate(s.Overall)}</td><td>{s.LongestWinStreak}</td></tr></tbody></table>
                """);
        }
        else
        {
            sb.Append("""<p class="note">No match has a result yet.</p>""");
        }

        // Said out loud rather than folded into the totals. A panel that quietly leaves
        // out a quarter of the archive is a correctness bug wearing a feature's clothes.
        // Above the disclosure, not inside it: these caveat the record that stays on
        // screen, and a caveat that folds away while the number it qualifies does not
        // is the same bug in a new place.
        var notes = new List<string>();
        if (s.Unattributed > 0)
            notes.Add(s.Unattributed == 1
                ? "1 match is counted above but under no deck — its log carried no decklist."
                : $"{s.Unattributed} matches are counted above but under no deck — " +
                  "their logs carried no decklist.");
        if (s.Excluded > 0)
            notes.Add($"{s.Excluded} unfinished match{(s.Excluded == 1 ? " is" : "es are")} " +
                      "left out of every record — an unfinished match has no result.");
        foreach (var note in notes)
            sb.Append($"""<p class="note">{E(note)}</p>""");

        sb.Append(Breakdowns(s));
        sb.Append("</section>");
        return sb.ToString();
    }

    /// <summary>
    /// The two breakdowns, behind a disclosure, or nothing when there are none.
    /// </summary>
    /// <remarks>
    /// The whole panel used to sit open above the match list: 37 table rows over 529
    /// matches, and the deck table grows with every new deck played, so the block a
    /// reader scrolls past to reach the list they opened the page for keeps getting
    /// taller (#40).
    /// <para>
    /// A disclosure and not tabs, a modal or two columns. Two columns would halve the
    /// width of a nine-column match table that already scrolls sideways on a phone.
    /// The other two need script, and this page is meant to work from a file:// URL
    /// with none — so with script off they would have to fall back to showing
    /// everything, which is the layout being fixed. <c>details</c> is native: it
    /// collapses without script, and its keyboard and focus behaviour is the browser's
    /// rather than something to get right by hand. The deck list on a game page is the
    /// same control for the same reason.
    /// </para>
    /// <para>
    /// Only the breakdowns fold. The overall record is one row and it is the number
    /// people come for; hiding it would answer the complaint by making the page worse.
    /// The heading above stays a real <c>h2</c> rather than moving into the summary,
    /// because the page has two headings and heading navigation is how a screen-reader
    /// user skips to either of them.
    /// </para>
    /// </remarks>
    private static string Breakdowns(IndexStats s)
    {
        var format = Breakdown("By format", "Format", s.ByFormat, decks: false);
        var deck = Breakdown("By deck", "Deck", s.ByDeck, decks: true);
        if (format.Length == 0 && deck.Length == 0) return "";

        var sb = new StringBuilder();
        sb.Append($"""
            <details id="breakdowns"><summary>{E(BreakdownSummary(s))}</summary>
            """);
        sb.Append(format);

        // Before the table whose buttons it explains, and pointed at by none of them.
        // Nothing but position makes it reachable now, so position is the whole of the
        // contract: a reader working down the section meets it once and then meets the
        // buttons. It used to sit after both tables, where aria-describedby reached it
        // from anywhere and its own placement did not matter — removing the attribute
        // without moving the note would have left the explanation eleven thousand
        // characters past the first button it explains, which is worse than repeating
        // it. A_deck_filter_note_comes_before_the_buttons_it_explains holds this.
        if (s.ByDeck.Count > 0)
            sb.Append("""
                <p class="note" id="deck-filter-note">Selecting a deck filters the match
                list below to it. Selecting it again clears the filter.</p>
                """);

        sb.Append(deck);
        sb.Append(SessionTable(s));
        sb.Append("</details>");
        return sb.ToString();
    }

    /// <summary>
    /// What the coach has to say about the sitting in progress, above everything else.
    /// </summary>
    /// <remarks>
    /// A suggestion, never a verdict. It replaces a rule that read "bench the deck after
    /// three straight losses", which fired in 22 of the archive's 28 sittings and so was
    /// not detecting a failing deck at all — it was detecting that somebody had played
    /// for a while. What survives is the useful half: a note between two games that the
    /// last three went badly, with a way to say "yes, and I am still playing".
    /// <para>
    /// <c>role="status"</c> rather than <c>alert</c>. This arrives between games, when
    /// nothing is urgent, and an assertive region interrupts whatever a screen reader is
    /// currently saying — which on this page is usually the result of the match that just
    /// finished. Polite is the whole point.
    /// </para>
    /// <para>
    /// Dismissal is remembered in <c>sessionStorage</c> against the exact text, and not
    /// on the server. The page is rebuilt and reloaded every time a match lands, so a
    /// dismissal held anywhere else would be undone within the minute; and keying it on
    /// the text means a nudge for a different deck, or a longer streak, is a new message
    /// and says itself again.
    /// </para>
    /// </remarks>
    private static string Coach(Nudge? nudge)
    {
        if (nudge is null) return "";
        return $"""
            <aside id="coach" class="coach" role="status" data-nudge="{E(nudge.Text)}">
            <p>{E(nudge.Text)}</p>
            <button type="button" id="coach-dismiss">Dismiss</button>
            </aside>
            """;
    }

    /// <summary>
    /// How each sitting went, newest first.
    /// </summary>
    /// <remarks>
    /// The table below lists every match and never says how a night went, so the last row
    /// of an evening was the whole impression it left — and a session that finished on a
    /// loss read as a losing session whatever the record. This is the row that says
    /// otherwise.
    /// <para>
    /// The gap that ends a sitting is stated rather than left implicit. A threshold that
    /// silently decides what counts as "a night" is one nobody can argue with, and the
    /// page already explains what a turn duration covers for the same reason.
    /// </para>
    /// </remarks>
    private static string SessionTable(IndexStats s)
    {
        if (s.Sessions.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append($"""
            <p class="note" id="session-note">A session is a run of matches with no break
            longer than {(int)Sessions.Gap.TotalHours} hours.</p>
            <div class="scroller"><table class="stats" id="by-session">
            <caption>By session</caption>
            <thead><tr>{Col("Session", Text)}{Col("Games", Num)}
            {Col("Record", Num)}{Col("Win rate", Num)}{Col("Decks", Text)}</tr></thead><tbody>
            """);

        foreach (var r in s.Sessions)
        {
            // Twin, like the Length and Deck columns: "7-8" for the eye and the sentence
            // for a synthesiser, which reads the shorthand as "seven eight" otherwise.
            // Going through the shared helper rather than hand-rolling the two spans is
            // what keeps the glyph itself named, so a pointer resting on it says
            // something — the regression issue #63 was about.
            var rate = r.WinRate is { } w ? $"{w:P0}" : "";
            var decks = string.Join(", ", r.Decks);
            sb.Append($"""
                <tr><th scope="row"{Key(r.StartedAtMs)}>{E(r.Started)}</th>
                <td{Key(r.Games)}>{r.Games}</td>
                <td{Key(r.Won - r.Lost)}>{Twin($"{r.Won}-{r.Lost}", E(r.Spoken))}</td>
                <td{Key(r.WinRate)}>{rate}</td>
                <td{Key(decks)}>{E(decks)}</td></tr>
                """);
        }

        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    /// <summary>
    /// The disclosure's one visible line, which says what opening it gets you.
    /// </summary>
    /// <remarks>
    /// It counts, because a collapsed control that says only "More" gives a reader
    /// nothing to decide with — the same reason a game page's deck heading says how
    /// many cards rather than just "Your deck".
    /// </remarks>
    private static string BreakdownSummary(IndexStats s)
    {
        var parts = new List<string>();
        if (s.ByFormat.Count > 0) parts.Add(Count(s.ByFormat.Count, "format"));
        if (s.ByDeck.Count > 0) parts.Add(Count(s.ByDeck.Count, "deck"));
        return $"Break it down — {string.Join(" and ", parts)}";
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Breakdown(string title, string what, IReadOnlyList<StatRow> rows, bool decks)
    {
        if (rows.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append($"""
            <div class="scroller"><table class="stats" id="{(decks ? "by-deck" : "by-format")}"><caption>{E(title)}</caption>
            <thead><tr>{Col(what, Text)}{Col("Played", Num)}
            {Col("Record", Num)}{Col("Win rate", Num)}
            """);

        // Only decks carry these: a format's median turn count mixes archetypes together
        // and says nothing about any of them.
        if (decks)
            sb.Append($"""
                {Col("Turns won", Num)}{Col("Turns lost", Num)}
                {Col("On the play", Num)}
                """);

        sb.Append("</tr></thead><tbody>");

        foreach (var r in rows)
        {
            var name = decks && r.Slug is { } slug
                ? $"""<span class="deck-name" data-deck="{E(slug)}">{E(r.Name)}</span>"""
                : E(r.Name);

            sb.Append($"""
                <tr><th scope="row"{Key(r.Name)}>{name}</th><td{Key(r.Played)}>{r.Played}</td>
                <td{Key(RecordKey(r))}>{Record(r)}</td><td{Key(r.WinRate)}>{Rate(r)}</td>
                """);

            if (decks)
                sb.Append($"""
                    <td{Key(r.TurnsInWins)}>{r.TurnsInWins?.ToString(CultureInfo.InvariantCulture) ?? Missing("no wins yet")}</td>
                    <td{Key(r.TurnsInLosses)}>{r.TurnsInLosses?.ToString(CultureInfo.InvariantCulture) ?? Missing("no losses yet")}</td>
                    <td{Key(r.WithOpening == 0 ? null : (double?)r.OnThePlay / r.WithOpening)}>{Play(r)}</td>
                    """);

            sb.Append("</tr>");
        }

        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    /// <summary>
    /// Won-lost, as two complete forms rather than one form with the other threaded
    /// through it.
    /// </summary>
    /// <remarks>
    /// The first attempt put a spoken twin <em>between</em> the numbers, which failed
    /// twice over. It read as "95 and 118", naming neither number under a column called
    /// Record — every other twin on this page supplies a meaning, not a conjunction.
    /// And <c>.vh</c> is <c>position:absolute</c>, which makes the span a block box, so
    /// CSS strips the spaces padding it and the digits ran together against their own
    /// separator. Two whole strings side by side have neither problem, and it is the
    /// shape the rest of the page already uses.
    /// </remarks>
    /// <summary>
    /// What a record sorts by: the balance, wins less losses.
    /// </summary>
    /// <remarks>
    /// Wins alone will not do, which is what this column was keyed on first. It reads
    /// as won-lost, and 10-0 against 10-10 are not the same record at all — but they
    /// carry the same wins, so they compared equal and a column labelled "Record" sat
    /// unsorted against itself.
    /// <para>
    /// The balance and not the win rate, because the rate already has its own column
    /// beside this one and a second copy of it would tell a reader nothing new. The
    /// balance says a different thing: 1-0 and 100-0 share a rate and are a world
    /// apart, and the reverse — 6-4 and 60-40 — is exactly the pair a rate flattens.
    /// </para>
    /// <para>
    /// A draw moves it by nothing, because a draw is neither a win nor a loss. Two
    /// records alike but for their draws do tie here, and the sort is stable, so they
    /// keep the order they had; the column that tells them apart is Played, one to the
    /// left.
    /// </para>
    /// </remarks>
    private static int RecordKey(StatRow r) => r.Won - r.Lost;

    private static string Record(StatRow r)
    {
        var seen = r.Drawn == 0 ? $"{r.Won}-{r.Lost}" : $"{r.Won}-{r.Lost}-{r.Drawn}";
        var said = r.Drawn == 0
            ? $"{r.Won} won, {r.Lost} lost"
            : $"{r.Won} won, {r.Lost} lost, {r.Drawn} drawn";

        return Twin(E(seen), E(said));
    }

    /// <summary>
    /// The win rate, rounded — but never rounded past a match that says otherwise. A
    /// 199–1 record reaches 99.5% and would print "100%" on the same row as the loss
    /// it does not include.
    /// </summary>
    private static string Rate(StatRow r)
    {
        if (r.WinRate is not { } rate) return Missing("no matches counted");
        if (rate < 1 && rate * 100 >= 99.5) return Twin("&gt;99%", "over 99 percent");
        if (rate > 0 && rate * 100 < 0.5) return Twin("&lt;1%", "under 1 percent");
        return $"{(rate * 100).ToString("0", CultureInfo.InvariantCulture)}%";
    }

    /// <summary>
    /// The on-the-play split over its own denominator, because the log did not record an
    /// opening for the older half of the archive and those are not losses of the die roll.
    /// </summary>
    private static string Play(StatRow r) =>
        r.WithOpening == 0
            ? Missing("not recorded")
            : $"{r.OnThePlay} of {r.WithOpening}";

    /// <summary>
    /// A visible mark and what it actually means, because the mark alone means two
    /// different things in one row — no wins yet in one column, a log that never
    /// recorded an opening in the next — and a lone dash is punctuation a synthesiser
    /// either skips or reads as "em dash". This page renders no bare dashes elsewhere;
    /// the deck-colour column refuses to print one precisely because it would be a
    /// claim about something nobody has a record of.
    /// </summary>
    private static string Missing(string why) => Twin("—", why);

    /// <summary>
    /// An abbreviation that fills a whole cell: what the column shows, and what a
    /// synthesiser says instead.
    /// </summary>
    /// <remarks>
    /// The glyph carries the name rather than being hidden behind one. Hiding it was
    /// the obvious construction and it had a measured cost: a cell whose entire content
    /// is <c>aria-hidden</c> glyph plus clipped words has nothing under the pointer to
    /// announce, so mouse users heard silence on the deck-colour, length and record
    /// columns while keyboard users heard them correctly (#61). Listening test across
    /// five techniques: this is the only one a screen reader reads both ways.
    /// <para>
    /// <c>role="img"</c> is what makes <c>aria-label</c> apply at all — the same label
    /// on a bare span is discarded, which is what #46 discovered the expensive way. It
    /// was accepted knowing it costs the word "graphic" ahead of every value; hearing
    /// it afterwards showed that price is smaller than the one agreed to. Keyboard
    /// navigation says "Graphic White", but pointer reading announces the label alone —
    /// "white" — so the extra word falls only on the path that already worked, and the
    /// path this was built for gets the bare value.
    /// </para>
    /// <para>
    /// The clipped span stays, purely so find-in-page can still match the spoken form.
    /// It is <c>aria-hidden</c> now — the name lives on the glyph, and without this it
    /// would be announced a second time. <c>aria-hidden</c> has no effect on rendering
    /// or on the browser's find, so nothing is lost by it.
    /// </para>
    /// <para>
    /// A mark appended to a cell that already has text — the incomplete asterisk, the
    /// missing-data dagger — is deliberately not built this way. Those cells have real
    /// text under the pointer already, and they were not among the columns reported
    /// silent.
    /// </para>
    /// </remarks>
    private static string Twin(string seen, string said) =>
        $"""<span role="img" aria-label="{said}">{seen}</span>""" +
        $"""<span class="vh" aria-hidden="true">{said}</span>""";

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    // Contrast is measured against the two backdrops `color-scheme: light dark` can
    // produce: #fff/#000 in light, and a #121212–#1e1e1e canvas with white text in
    // dark. Dimming with `opacity` is kept where the result still clears 4.5:1 — over
    // a 21:1 base it does — but the win colour and both star colours did not, so they
    // are stated per scheme instead. `prefers-color-scheme` rather than `light-dark()`
    // so the fix is not conditional on a recent engine.
    private const string Css = """
        :root{color-scheme:light dark}
        body{font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;
             max-width:64rem;margin:0 auto;padding:2rem 1rem}
        h1{font-size:1.5rem;margin:0 0 .2rem}
        .sub{opacity:.65;margin:.2rem 0 1rem}
        /* The game page carries this rule too, and both need the user-select: clipped
           text is still selectable, so without it a row pastes "11m 12s11 minutes 12
           seconds". Fixing one page and not the other reads as fixed. */
        .vh{position:absolute;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;
            clip:rect(0 0 0 0);clip-path:inset(50%);white-space:nowrap;border:0;
            -webkit-user-select:none;user-select:none}
        label{display:block;font-size:.85rem;margin-bottom:.25rem}
        #q{width:100%;font:inherit;padding:.55rem .7rem;margin-bottom:1rem;
           border:1px solid currentColor;border-radius:.4rem;background:transparent;color:inherit}
        table{width:100%;border-collapse:collapse}
        caption{text-align:left}
        th,td{text-align:left;padding:.45rem .6rem;border-bottom:1px solid rgba(128,128,128,.35)}

        /* A named glyph stretched over the cell it is the whole content of. Nothing
           moves: the negative margin gives back exactly the padding that replaces it,
           and the text stays where it was to the pixel. What changes is how much of the
           cell answers to the pointer, which is what mouse reading depends on — the
           name lives on this element, so anywhere the pointer lands that is NOT this
           element has nothing to say. Measured on the live page: the deck glyph covered
           7% of its cell and the middle of the cell missed it entirely; length covered
           15%. Both are now 42%, the record columns 99%, and the centre hits every
           time. Rows run 87px tall when a neighbouring column wraps, so the height is
           not fully recoverable this way and the remaining shortfall is vertical. */
        td>[role=img]{display:block;margin:-.45rem -.6rem;padding:.45rem .6rem}
        thead th{font-size:.8rem;text-transform:uppercase;letter-spacing:.04em;opacity:.6}
        tbody th{font-weight:400}
        tbody tr:hover{background:rgba(128,128,128,.12)}
        a{color:inherit}
        tbody a{display:inline-block;padding-block:.15rem}
        .win{color:#137333}
        .loss,.draw{opacity:.7}
        .empty{opacity:.7}
        .note{opacity:.7;font-size:.85rem;max-width:44rem}
        /* A left rule rather than a coloured fill: the page is read in both schemes and
           in forced colours, and currentColor is the one border that survives all three. */
        .coach{display:flex;gap:.75rem;align-items:baseline;flex-wrap:wrap;
               border-left:3px solid currentColor;padding:.5rem .8rem;margin:0 0 1rem}
        .coach p{margin:0}
        .coach button{font:inherit;padding:.35rem .7rem;min-height:1.75rem;
                      border:1px solid currentColor;border-radius:.3rem;
                      background:transparent;color:inherit;cursor:pointer}
        .coach button:hover{background:rgba(128,128,128,.15)}
        body.live #keep-note{display:none}
        .star{background:none;border:0;cursor:default;font-size:1rem;padding:0;color:#666666;
              display:inline-flex;align-items:center;justify-content:center;
              min-width:1.75rem;min-height:1.75rem;border-radius:.3rem}
        .star.on{color:#8a6100}
        .star:enabled{cursor:pointer}
        .star:enabled:hover{background:rgba(128,128,128,.2)}
        #stats{margin:0 0 1.5rem}
        #stats h2{font-size:1.1rem;margin:0 0 .6rem}
        /* Sized and hit-targeted like the other controls on this page rather than left
           at the browser's bare marker, which is a few pixels tall on a touch screen. */
        #breakdowns>summary{cursor:pointer;padding:.4rem 0;min-height:1.75rem;
                            border-radius:.3rem;font-size:.95rem}
        #breakdowns[open]>summary{margin-bottom:.4rem}
        table.stats{width:auto;margin:0 0 1rem;font-size:.95rem}
        table.stats caption{font-weight:600;padding:.3rem 0;opacity:.8}
        table.stats th,table.stats td{padding:.3rem .8rem .3rem 0}
        /* Same trick, against this table's own padding rather than the match table's. */
        table.stats td>[role=img]{margin:-.3rem -.8rem -.3rem 0;padding:.3rem .8rem .3rem 0}
        /* A deck name is a span until script makes it a button, so it has to look like
           plain text until then rather than like a control that does nothing. */
        button.deck-name{background:none;border:0;font:inherit;color:inherit;
                         cursor:pointer;text-decoration:underline;
                         text-underline-offset:.2em;padding:.15rem .3rem;
                         margin:-.15rem -.3rem;min-height:1.75rem;border-radius:.3rem;
                         text-align:left}
        /* A background swap rather than an opacity shift, which is close to invisible
           against system-forced colours. The same feedback the star and copy buttons
           give, for the same reason. */
        button.deck-name:hover{background:rgba(128,128,128,.2)}
        button.deck-name.on{background:rgba(128,128,128,.25);text-decoration:none}
        /* A column heading is plain text until script makes it a sort control, so the
           button has to inherit the heading's own type rather than a browser default. */
        button.sort{background:none;border:0;font:inherit;color:inherit;cursor:pointer;
                    text-transform:inherit;letter-spacing:inherit;text-align:left;
                    padding:.15rem .3rem;margin:-.15rem -.3rem;min-height:1.75rem;
                    border-radius:.3rem}
        button.sort:hover{background:rgba(128,128,128,.2)}
        /* Weight and not colour alone, so the sorted column is still tellable apart
           without colour vision and with a forced palette. */
        th[aria-sort=ascending] button.sort,th[aria-sort=descending] button.sort{
            font-weight:700;opacity:1}
        /* Wide tables scroll inside themselves instead of pushing the whole page
           sideways at a phone width or at 200% zoom. */
        .scroller{overflow-x:auto}
        /* Sized like the star, and never disabled the way the star is: keeping a match
           needs the local server, copying an id needs nothing. */
        .copyid{background:none;border:0;cursor:pointer;font-size:1rem;padding:0;
                color:inherit;opacity:.6;display:inline-flex;align-items:center;
                justify-content:center;min-width:1.75rem;min-height:1.75rem;
                border-radius:.3rem}
        .copyid:hover{background:rgba(128,128,128,.2);opacity:1}
        :focus-visible{outline:2px solid currentColor;outline-offset:2px}
        #live{display:none;font-size:.8rem;opacity:.6}
        body.live #live{display:inline}
        code{font-family:ui-monospace,Menlo,Consolas,monospace}
        .deck{font-family:ui-monospace,Menlo,Consolas,monospace;letter-spacing:.08em;
              white-space:nowrap}
        .build{margin-top:2rem;padding-top:.8rem;font-size:.8rem;opacity:.55;
               border-top:1px solid rgba(128,128,128,.35)}
        @media (prefers-color-scheme:dark){
          .win{color:#4ade80}
          .star{color:#9a9a9a}
          .star.on{color:#f2c14a}
        }
        @media (forced-colors:active){
          .sub,.loss,.draw,.empty,.note,th,#live,.build{opacity:1}
          .star{color:ButtonText}
          .star.on{color:Highlight}
          caption{opacity:1}
          button.deck-name.on{background:Highlight;color:HighlightText}
          .copyid{color:ButtonText;opacity:1}
        }
        """;

    private const string Script = """
        (function () {
          var tbody = document.getElementById('data');
          var q = document.getElementById('q');
          var count = document.getElementById('count');
          var status = document.getElementById('status');
          if (!tbody || !q || !count) return;

          let rows = Array.prototype.slice.call(tbody.querySelectorAll('tr'));
          var counting;

          // Assigning a live region the string it already holds is a no-op, so a
          // second identical message would be silent. Clearing first, in its own
          // task, makes it announce again.
          function announce(message) {
            if (!status) return;
            status.textContent = '';
            setTimeout(function () { status.textContent = message; }, 60);
          }

          function apply() {
            var terms = q.value.toLowerCase().split(/\s+/).filter(Boolean);
            var shown = 0;
            rows.forEach(function (tr) {
              var hay = tr.getAttribute('data-search') || '';
              var match = terms.every(function (t) {
                // A deck token has to match whole. Substring matching would let
                // "deck:hare-apparent" also select "deck:hare-apparent-2", so the panel
                // would say a deck has played N while the table below showed more.
                if (t.indexOf('deck:') === 0) return (' ' + hay + ' ').indexOf(' ' + t + ' ') !== -1;
                return hay.indexOf(t) !== -1;
              });
              tr.hidden = !match;
              if (match) shown++;
            });

            // The counter is a live region, so writing it on every keystroke would
            // queue one announcement per character typed. Filtering stays immediate;
            // only the announcement waits for a pause. And writing the same string
            // back still replaces the text node, which a live region would announce,
            // so it is only written when it actually changed.
            var text = shown + ' of ' + rows.length + ' shown';
            clearTimeout(counting);
            counting = setTimeout(function () {
              if (count.textContent !== text) count.textContent = text;
            }, 300);
          }

          // Dismissal lives in sessionStorage against the exact text. The page is
          // rebuilt and reloaded whenever a match lands, so anything held in a variable
          // would be undone within the minute; and keying on the text means a nudge for
          // a different deck, or a longer streak, is a new message and speaks up again.
          var coach = document.getElementById('coach');
          if (coach) {
            var said = coach.getAttribute('data-nudge') || '';
            try {
              if (sessionStorage.getItem('coach-dismissed') === said) coach.remove();
            } catch (e) { /* private mode: the nudge simply stays */ }
            var off = document.getElementById('coach-dismiss');
            if (off) off.addEventListener('click', function () {
              try { sessionStorage.setItem('coach-dismissed', said); } catch (e) {}
              coach.remove();
            });
          }

          q.addEventListener('input', apply);
          apply();

          // The deck names in the stats panel become filters, but only now that there is
          // script to make them work. Rendered as a span and upgraded here, because a
          // button that does nothing without script is worse than a name that never
          // claimed to be one.
          function wireDeckNames() {
            document.querySelectorAll('#stats span.deck-name').forEach(function (name) {
              var button = document.createElement('button');
              button.type = 'button';
              button.className = 'deck-name';
              button.dataset.deck = name.dataset.deck;
              button.textContent = name.textContent;

              // No aria-label. This button is the content of a th[scope=row], so its
              // accessible name IS the row header, and a label of "Show only X matches"
              // would be announced ahead of every cell in the row instead of the deck's
              // name.
              //
              // And no aria-describedby either, though it had one. It pointed at the
              // note, on the reasoning that what the button does should be said once —
              // but a description is read out at every button that carries it, so
              // "Selecting a deck filters the match list below to it. Selecting it
              // again clears the filter." was announced on every deck row, sixteen
              // words ahead of the one word wanted. The note now sits before the table
              // instead, where it is met once on the way in. This is the same trade #48
              // got backwards on the Keep button: per-row repetition of something the
              // reader has already been given.

              // A toggle, so the state rides on aria-pressed and the name stays put —
              // the same rule the star two hundred lines up follows, and for the same
              // reason: a name that changes cannot be relied on to say what will happen.
              button.setAttribute('aria-pressed', 'false');

              button.addEventListener('click', function () {
                var token = 'deck:' + button.dataset.deck;
                var on = q.value.trim() !== token;

                // A second click on the deck already filtered to clears it, which is the
                // only way back without reaching for the field.
                q.value = on ? token : '';
                apply();

                document.querySelectorAll('#stats button.deck-name').forEach(function (b) {
                  b.setAttribute('aria-pressed', b === button && on ? 'true' : 'false');
                  b.classList.toggle('on', b === button && on);
                });

                // Said before focus moves: focusing the field makes it announce itself,
                // its label and its value, which would talk over this.
                announce(on ? 'Filtered to ' + button.textContent + '.' : 'Filter cleared.');
                q.focus();
              });
              name.replaceWith(button);
            });
          }
          wireDeckNames();

          // Sorting. One implementation for every table on the page rather than one per
          // table: the match list and the two breakdowns differ only in which columns
          // say how they compare, and that is said in the markup.
          var sorted = {};

          // What a cell sorts by. Never its rendered text if the renderer said
          // otherwise, because most cells here carry two versions of themselves — a
          // visible one hidden from assistive technology and a spoken twin hidden from
          // sight — so a length cell reads "4m4 minutes" and a rate reads "58%58
          // percent". Every sortable column supplies a key; the text is a fallback that
          // should never be reached.
          function keyOf(row, index, rule) {
            var cell = row.cells[index];
            if (!cell) return null;
            var raw = cell.hasAttribute('data-key')
              ? cell.getAttribute('data-key')
              : cell.textContent.trim().toLowerCase();
            // Empty is nothing, for words as much as for numbers: a match whose log
            // carried no decklist has no deck, and sorting it under the blank name it
            // does not have would put it above every deck that has one.
            if (raw === '') return null;
            if (rule !== 'num') return raw;
            var n = parseFloat(raw);
            return isNaN(n) ? null : n;
          }

          function sortRows(table, index, rule, asc) {
            var body = table.tBodies[0];
            if (!body) return;
            var order = Array.prototype.slice.call(body.rows);
            order.sort(function (a, b) {
              var x = keyOf(a, index, rule), y = keyOf(b, index, rule);
              // Nothing sorts last whichever way the column points. An unfinished match
              // has no length, and reading that as zero would file it among the fastest
              // games ever played.
              if (x === null || y === null) return x === y ? 0 : (x === null ? 1 : -1);
              if (x < y) return asc ? -1 : 1;
              if (x > y) return asc ? 1 : -1;
              return 0;
            });
            // Appending a row already in the table moves it whole, so every cell travels
            // with the row it belongs to and nothing is rebuilt — the stars and copy
            // buttons keep their state and their listeners.
            order.forEach(function (row) { body.appendChild(row); });
          }

          function applySort(table, state, quiet) {
            sortRows(table, state.index, state.rule, state.asc);
            Array.prototype.forEach.call(table.tHead.rows[0].cells, function (th, i) {
              if (!th.getAttribute('data-sort')) return;
              var on = i === state.index;
              // aria-sort is what says it out loud; the arrow is decoration for the eye.
              th.setAttribute('aria-sort',
                on ? (state.asc ? 'ascending' : 'descending') : 'none');
              var mark = th.querySelector('.mark');
              if (mark) mark.textContent = on ? (state.asc ? ' ↑' : ' ↓') : '';
            });
            if (table.id) sorted[table.id] = state;
            if (!quiet) {
              announce('Sorted by ' + state.label + ', ' +
                       (state.asc ? 'ascending' : 'descending') + '.');
            }
          }

          function wireSort(table) {
            var head = table.tHead && table.tHead.rows[0];
            var body = table.tBodies[0];
            // One row has no order to put it in, which is the overall record's whole
            // shape. Offering a control that cannot change anything is worse than none.
            if (!head || !body || body.rows.length < 2) return;

            Array.prototype.forEach.call(head.cells, function (th, index) {
              var rule = th.getAttribute('data-sort');
              if (!rule || th.querySelector('button.sort')) return;

              // Text until here, the same rule the star, the pager and the deck filter
              // follow: a control that does nothing without script is worse than text
              // that never claimed to be one.
              var label = th.textContent.trim();
              var button = document.createElement('button');
              button.type = 'button';
              button.className = 'sort';
              button.appendChild(document.createTextNode(label));
              var mark = document.createElement('span');
              mark.className = 'mark';
              mark.setAttribute('aria-hidden', 'true');
              button.appendChild(mark);
              th.textContent = '';
              th.appendChild(button);
              th.setAttribute('aria-sort', 'none');

              button.addEventListener('click', function () {
                applySort(table, {
                  index: index, rule: rule, label: label,
                  asc: th.getAttribute('aria-sort') !== 'ascending'
                });
              });
            });
          }

          function wireTables(quiet) {
            document.querySelectorAll('table').forEach(function (table) {
              wireSort(table);
              var state = table.id ? sorted[table.id] : null;
              if (state) applySort(table, state, quiet);
            });
          }
          wireTables(true);

          // navigator.clipboard needs a secure context and file:// does not qualify in
          // every browser, so fall back rather than fail silently. The twin of this
          // lives in the game page's script.
          function legacyCopy(text, button) {
            var area = document.createElement('textarea');
            area.value = text;
            area.setAttribute('readonly', '');
            area.style.position = 'fixed';
            area.style.top = '-1000px';
            document.body.appendChild(area);
            area.select();
            var ok = false;
            try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
            document.body.removeChild(area);
            button.focus();
            announce(ok ? 'Game ID copied.' : 'Copy failed.');
          }

          // Delegated, because there is one of these per row and an archive runs to
          // hundreds. Wired here rather than below the served-only line: copying needs
          // no server, and the report is meant to work opened straight off disk.
          tbody.addEventListener('click', function (e) {
            var button = e.target.closest ? e.target.closest('.copyid') : null;
            if (!button) return;
            var id = button.dataset.id || '';
            if (navigator.clipboard && navigator.clipboard.writeText) {
              navigator.clipboard.writeText(id).then(
                function () { announce('Game ID copied.'); },
                function () { legacyCopy(id, button); });
            } else {
              legacyCopy(id, button);
            }
          });

          // Everything below only works when the page is served by `mtga-pbp watch`.
          // Opened from disk it stays a plain static report, which is the point.
          if (location.protocol.indexOf('http') !== 0) return;
          document.body.classList.add('live');

          function setStar(b, on) {
            b.classList.toggle('on', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
            (b.firstElementChild || b).textContent = on ? '★' : '☆';
          }

          function wireStars() {
            tbody.querySelectorAll('.star').forEach(function (b) {
              // Served, so the control works: drop both the disabled state and the
              // note explaining why it did not, which would otherwise still be read.
              b.disabled = false;
              b.removeAttribute('aria-describedby');
              b.addEventListener('click', function () {
                var on = b.getAttribute('aria-pressed') !== 'true';
                fetch('/api/favorite/' + encodeURIComponent(b.dataset.id) + '?on=' + on,
                      { method: 'POST' })
                  .then(function (r) {
                    if (r.ok) setStar(b, on);
                    else announce('Could not change the keep state.');
                  }, function () { announce('Could not change the keep state.'); });
              });
            });
          }
          wireStars();

          // Re-read the freshly written index and swap in its rows, so the search box
          // and scroll position survive what would otherwise be a reload.
          function refresh() {
            var active = document.activeElement;
            var kind = active && active.classList
              ? (active.classList.contains('star') ? 'star'
                : active.classList.contains('copyid') ? 'copyid' : null)
              : null;
            var focused = kind ? active.dataset.id : null;

            // The disclosure's summary is destroyed by the same swap, and it is the one
            // control on this page that is not identified by a data-id. There is only
            // ever one of it, so where it was is all that needs remembering.
            var onSummary = !!(active && active.tagName === 'SUMMARY' &&
                               active.parentElement && active.parentElement.id === 'breakdowns');

            // A sort control in the panel is destroyed by that swap too. It has no id of
            // its own either, but which table and which column is enough to find it
            // again — and that is the same pair the sort itself is remembered by.
            var head = active && active.closest ? active.closest('thead th') : null;
            var onSort = head && active.classList && active.classList.contains('sort')
              ? { table: (head.closest('table') || {}).id,
                  index: Array.prototype.indexOf.call(head.parentElement.cells, head) }
              : null;

            fetch('/', { cache: 'no-store' })
              .then(function (r) { return r.text(); })
              .then(function (html) {
                var fresh = new DOMParser().parseFromString(html, 'text/html');
                var next = fresh.querySelector('#data');
                if (!next) return;
                tbody.innerHTML = next.innerHTML;

                // The record has to travel with the rows it describes. Swapping only
                // the table left the panel showing the state the page loaded with,
                // under a header that says the page is updating live — two sets of
                // numbers on one screen with nothing to say which was stale.
                var panel = document.getElementById('stats');
                var freshPanel = fresh.querySelector('#stats');
                if (panel && freshPanel) {
                  // The disclosure lives inside the panel, so its open state is part of
                  // what gets replaced. Without carrying it over, the breakdowns a
                  // reader had opened would fold up under them every time a match
                  // finished — which is exactly when they are watching this page.
                  var was = document.getElementById('breakdowns');
                  var open = was ? was.open : false;
                  panel.innerHTML = freshPanel.innerHTML;
                  var now = document.getElementById('breakdowns');
                  if (now) {
                    now.open = open;
                    // Put the reader back on the control they were on, for the same
                    // reason the rows below do it: a match finishing is not a reason to
                    // send somebody navigating by keyboard back to the top of the page.
                    if (onSummary) {
                      var summary = now.querySelector('summary');
                      if (summary) summary.focus();
                    }
                  }
                  wireDeckNames();
                }
                rows = Array.prototype.slice.call(tbody.querySelectorAll('tr'));
                wireStars();
                apply();
                // The rows arrive in the order the build wrote them, and the panel's
                // tables are new nodes entirely, so both need putting back the way the
                // reader had them. Quietly: they did not just ask for this.
                wireTables(true);
                if (onSort && onSort.table) {
                  var sortedTable = document.getElementById(onSort.table);
                  var cell = sortedTable && sortedTable.tHead
                    ? sortedTable.tHead.rows[0].cells[onSort.index] : null;
                  var control = cell ? cell.querySelector('button.sort') : null;
                  if (control) control.focus();
                }
                // Replacing the rows destroys the node that had focus, which would
                // drop a keyboard or screen-reader user back to the top of the page.
                if (focused) {
                  var again = tbody.querySelector(
                    '.' + kind + '[data-id="' + focused + '"]');
                  if (again) again.focus();
                }
                announce('Match list updated.');
              });
          }

          var es = new EventSource('/api/events');
          es.addEventListener('changed', refresh);
        })();
        """;
}
