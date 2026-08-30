using MtgaPbp.Core;

namespace MtgaPbp.Render;

public enum Density { Beats, Verbose }

/// <summary>One narrated line, or one of the headings the lines hang under.</summary>
/// <param name="IsTurnHeader">
/// True for every heading the narrator emits, not only turn headings — the opening and,
/// in a multi-game match, the game headings are all headings to the renderers.
/// </param>
/// <param name="Game">
/// Which game this line belongs to, or zero on a transcript that carries no game
/// records. Only used to keep <see cref="Narrator.Collapse"/> from folding a line at the
/// end of one game into the identical line at the start of the next.
/// </param>
/// <param name="Anchor">
/// The heading's id, less the prefix that tells the two densities apart. Empty on
/// everything that is not a heading.
/// </param>
/// <param name="Level">
/// The heading's rank, and meaningless on anything that is not a heading. A single-game
/// match keeps every heading at 2, directly under the match title. A multi-game match
/// puts its games at 2 and demotes their openings and turns to 3, so that a game is the
/// container its divider already looks like.
/// <para>
/// This used to be flat — every heading an <c>h2</c>, game headings included — on the
/// grounds that a run of same-rank headings still walks past "Game 2" on the way into
/// the second game's turn one, and that the page shrinks <c>h2</c> below the browser
/// default so an unstyled <c>h3</c> would come out larger than the game heading above
/// it. Walking past a heading is not the same as being able to tell what contains what:
/// rank is the only thing that says a turn belongs to a game, and flattening it left 56
/// equal-rank headings on a three-game page. The styling objection was real and is
/// answered by styling <c>h3</c> rather than by leaving the structure wrong — single-game
/// pages emit no <c>h3</c> at all, so the added rule cannot reach them.
/// </para>
/// </param>
/// <param name="Accrues">
/// The permanent a quantity in this line adds to, on the lines that carry such a
/// quantity — counters and statline changes. Null everywhere else.
/// <para>
/// It exists for one distinction. "Squirrel gets 1 +1/+1 counter ×24" reads as one
/// Squirrel standing up as a 25/25, and it was twenty-four Squirrels taking one apiece.
/// The trap is specific to lines whose number a reader would add up: "You attack with
/// Rabbit ×5" is five Rabbits to anyone, because attacking does not accumulate, and
/// marking that one would be noise. So this is set only where the misreading is
/// available.
/// </para>
/// </param>
public sealed record Line(
    int Turn, int Indent, string Text, bool IsTurnHeader, bool IsBoard = false,
    int Game = 0, string Anchor = "", int Level = 2, int? Accrues = null);

public static class Narrator
{
    private static readonly HashSet<EventKind> VerboseOnly =
        [EventKind.PhaseChange, EventKind.ManaPaid, EventKind.Unknown];

    /// <summary>
    /// The heading the opening sits under. Its own section rather than part of the
    /// turn-one header: the roll and the mulligans happen before turn one, not during
    /// it, so folding them into that header would file pre-game facts inside a turn and
    /// leave the header carrying three unrelated claims. It also survives a match that
    /// never reaches turn one — the archive has one, conceded during the mulligan —
    /// where a turn-one header has nothing to attach to.
    /// </summary>
    private const string OpeningHeading = "Opening";

    /// <summary>
    /// The anchor the opening takes in a single-game match. Turn zero's, which no turn
    /// can ever claim — Arena numbers turns from one — so it needs no scheme of its own.
    /// </summary>
    private const string OpeningAnchor = "t0";

    /// <summary>
    /// Hand sizes as words. The rest of a line is prose, and "mulligan to 6" reads like
    /// a stat where "mulligan to six" reads like the sentence a player would say. It is
    /// also one less number for a synthesiser to run together with the die roll's.
    /// Indexed by cards kept, so it covers nought through a full opening hand.
    /// </summary>
    private static readonly string[] CardCounts =
        ["zero", "one", "two", "three", "four", "five", "six", "seven"];

    /// <summary>
    /// The whole transcript as lines. A single-game match narrates exactly as it always
    /// has: one opening, then its turns. A match with more than one game gets a heading
    /// per game, each with its own opening and its own turn one, and the result of every
    /// game but the last stated where that game ends — the match-end line already says
    /// how the last one went.
    /// </summary>
    public static IReadOnlyList<Line> Narrate(Transcript t, Density density)
    {
        var lines = new List<Line>();

        // More than one game is what changes the shape of the page, so it is what the
        // extra structure keys off. A Bo3 that only ever reached game one is a
        // single-game transcript, and reads like one.
        var multi = t.Games.Count > 1;

        // Everything below a game heading is one rank deeper than the game, and a match
        // with no game headings has nothing to be deeper than.
        var under = multi ? 3 : 2;

        // Both densities get the opening. Nothing about it is detail you would want
        // hidden, and the two views are meant to be the same match at two zoom levels.
        if (!multi) AppendOpening(lines, t, t.Opening, game: 0, OpeningAnchor, under);

        // Only the turns worth remarking on, so a header carries a duration where that
        // is the interesting thing about the turn and stays quiet everywhere else.
        var longTurns = TurnClock.LongTurns(t);

        var game = 0;

        foreach (var e in FoldControlChanges(t.Events.OrderBy(x => x.Seq).ToList()))
        {
            if (multi && e.GameNumber != game)
            {
                // The game that just ended says how it ended, where it ended. Only a game
                // with another after it ever reaches here, which is exactly right: the
                // last game's ending is the match's ending, and the match-end event says
                // it a few lines later.
                if (t.Games.FirstOrDefault(g => g.Number == game)?.ResultLine is { } ending)
                    lines.Add(new Line(0, 1, ending, IsTurnHeader: false, Game: game));

                game = e.GameNumber;
                var record = t.Games.FirstOrDefault(g => g.Number == game);
                lines.Add(new Line(0, 0, $"Game {game}", IsTurnHeader: true, Game: game,
                                   Anchor: $"g{game}", Level: 2));
                AppendOpening(lines, t, record?.Opening, game, $"g{game}-open", under);
            }

            if (density == Density.Beats && VerboseOnly.Contains(e.Kind)) continue;
            if (density == Density.Beats && IsRoutineDraw(e)) continue;
            var text = Phrase(e, t, density);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // After the phrasing, not before it: what beats refuse to show is a line
            // that says "Unknown card", and only the line knows whether it says one.
            if (density == Density.Beats && ShowsAPlaceholder(e, text)) continue;

            // Appended here rather than inside Phrase, which is a switch over how each
            // kind of event reads and has no business knowing the clock.
            if (e.Kind == EventKind.TurnStart) text += Elapsed(e, longTurns);

            var header = e.Kind == EventKind.TurnStart;
            lines.Add(new Line(
                e.Turn,
                header ? 0 : 1,
                text,
                header,
                e.Kind == EventKind.BoardSnapshot,
                Game: e.GameNumber,
                Anchor: header ? (multi ? $"g{game}-t{e.Turn}" : $"t{e.Turn}") : "",
                Level: under,
                Accrues: Accumulates(e.Kind) ? e.TargetInstanceId ?? e.SourceInstanceId : null));
        }
        return Collapse(lines);
    }

    /// <summary>
    /// One effect stealing six permanents is one thing that happened, so it becomes one
    /// line.
    /// </summary>
    /// <remarks>
    /// Arena sends a separate <c>ControllerChanged</c> annotation per permanent, all in
    /// the same message and therefore all at once — there is no order among them to
    /// preserve, and printing them apart produced a stutter that reads as a fault:
    /// <c>gains control of Hare Apparent ×2 / of Rabbit / of Hare Apparent / of Rabbit
    /// ×2</c>, the same creature named twice in a run of four lines for what was one
    /// trigger.
    /// <para>
    /// <see cref="Collapse"/> cannot do this. It folds adjacent lines that are already
    /// identical, and these differ by the permanent each names — the folding has to
    /// happen while the seat is still known, which is before the phrasing rather than
    /// after it.
    /// </para>
    /// <para>
    /// Only a run going to the same player is folded. Two effects trading permanents in
    /// opposite directions is two things happening, and one sentence claiming both would
    /// name the wrong player for half of it.
    /// </para>
    /// </remarks>
    private static List<GameEvent> FoldControlChanges(IReadOnlyList<GameEvent> events)
    {
        var folded = new List<GameEvent>(events.Count);

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Kind != EventKind.ControlChanged || e.SourceName is null)
            {
                folded.Add(e);
                continue;
            }

            var names = new List<string>();
            var run = 0;
            while (i + run < events.Count
                   && events[i + run] is { Kind: EventKind.ControlChanged } next
                   && next.SourceName is not null
                   && next.ActorSeat == e.ActorSeat
                   && next.GameNumber == e.GameNumber
                   && next.Turn == e.Turn)
            {
                names.Add(next.SourceName);
                run++;
            }

            // Counted in the order they were first named, so the sentence lists the
            // board the way the board was described.
            var tally = new List<string>();
            foreach (var name in names)
            {
                if (tally.Any(x => x == name || x.StartsWith($"{name} ×", StringComparison.Ordinal)))
                    continue;
                var many = names.Count(x => x == name);
                tally.Add(many == 1 ? name : $"{name} ×{many}");
            }

            folded.Add(run == 1 ? e : e with { Detail = string.Join(", ", tally) });
            i += run - 1;
        }
        return folded;
    }

    /// <summary>
    /// The opening heading and its lines, or nothing at all when the log carried none of
    /// it — which is what keeps a game with no opening from growing an empty heading.
    /// </summary>
    private static void AppendOpening(
        List<Line> lines, Transcript t, Opening? opening, int game, string anchor, int level)
    {
        if (OpeningLines(t, opening) is not { Count: > 0 } texts) return;

        lines.Add(new Line(0, 0, OpeningHeading, IsTurnHeader: true, Game: game,
                           Anchor: anchor, Level: level));
        foreach (var text in texts)
            lines.Add(new Line(0, 1, text, IsTurnHeader: false, Game: game));
    }

    /// <summary>
    /// Folds runs of the identical line into one with a count. A single card can
    /// trigger nine times in a row or make four tokens back to back, and printing
    /// each is how a transcript turns into a wall. Turn headers are never folded.
    /// </summary>
    /// <remarks>
    /// A run never crosses a game boundary, even where the two games would supply
    /// identical text. Two games of the same deck really can end and begin on the same
    /// sentence, and "You play Plains ×2" spanning a game boundary would report one
    /// thing happening twice in a row when it was two things in two different games.
    /// The game headings between them already break every run in practice; the check is
    /// here so that stays true of the fold itself rather than of the layout around it.
    /// </remarks>

    /// <summary>
    /// Whether a line of this kind reports a quantity that a reader would add up if the
    /// line repeated.
    /// </summary>
    /// <remarks>
    /// Counters and statline changes do: three of them on one permanent is a bigger
    /// permanent, so a run marker over them invites the sum. Everything else does not.
    /// Attacking five times with the same-named creature is five creatures to any
    /// reader, and a note saying so would be clutter on a line nobody misread.
    /// </remarks>
    private static bool Accumulates(EventKind kind) =>
        kind is EventKind.CounterChanged or EventKind.StatsModified or EventKind.StatsExpired;

    private static List<Line> Collapse(List<Line> lines)
    {
        var result = new List<Line>(lines.Count);
        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i];
            var run = 1;
            while (!line.IsTurnHeader &&
                   i + run < lines.Count &&
                   lines[i + run].Text == line.Text &&
                   lines[i + run].IsTurnHeader == line.IsTurnHeader &&
                   lines[i + run].Game == line.Game)
                run++;

            if (run == 1)
            {
                result.Add(line);
                i += run;
                continue;
            }

            // Two or more distinct permanents means the run is a crowd, not a repetition.
            // Anything less — no subjects at all, or one subject named over and over —
            // keeps the plain marker, which is what a line about a player or a spell on
            // the stack has always had and still needs.
            //
            // Counted by index rather than with Skip: the outer loop advances by the run
            // length, so the runs partition the list and the whole scan is one pass, but
            // only if reaching a run's start is free. Skip(i) would make that cost depend
            // on i, which on a transcript of thousands of short runs is the difference
            // between linear and quadratic.
            var subjects = new HashSet<int>();
            for (var j = i; j < i + run; j++)
                if (lines[j].Accrues is { } id) subjects.Add(id);

            // Where the marker sits is what it means, so nothing has to be explained.
            // Trailing, it counts the event: "Iron Man's ability triggers ×2" happened
            // twice. Leading, it counts the subject: "24× Squirrel gets 1 +1/+1 counter"
            // is twenty-four Squirrels — the same shape a decklist already uses for
            // "4× Hare Apparent", so the idiom is one a reader of this app has met.
            //
            // Before this, both were the trailing form, and the crowd read as the
            // repetition: one Squirrel standing up as a 25/25 when every one of the
            // twenty-four was a 2/2, as the attack lines directly below it said.
            result.Add(line with
            {
                Text = subjects.Count > 1 ? $"{run}× {line.Text}" : $"{line.Text} ×{run}"
            });
            i += run;
        }
        return result;
    }

    /// <summary>
    /// True when the line the reader would see actually says "Unknown card" — a subject
    /// that resolved only to a bare instance id, typically a token that left play before
    /// the client ever described it. "#332 is put into the graveyard" is noise, so beats
    /// drop it; verbose keeps it so the gap stays visible when debugging.
    /// </summary>
    /// <remarks>
    /// Asks the finished line rather than the event's fields, because the two disagree.
    /// A shockland's payment arrives as <c>ModifiedLife</c> attributed to the ability
    /// object rather than the land, so its <c>SourceName</c> is a placeholder — but
    /// <c>LifeChanged</c>'s phrasing is seat, verb and amount, and never prints a source
    /// at all. The old rule read the field and dropped the line, so 37 of 529 rendered
    /// pages carried a life total that moved with nothing beneath it to say why, while
    /// the headers went on reporting the true total from board state. The page
    /// contradicted itself over a name it was never going to show (#41).
    /// <para>
    /// The kinds that really do print an unnamed source are unaffected, and they are
    /// unaffected without being listed: <c>Damage</c> and <c>TokenCreated</c> fall back
    /// to "Something" and "An effect" only when the name is <em>null</em>, so a
    /// placeholder reaches their text and is found there. A list of exempt kinds would
    /// have to be kept in step with the phrasing by hand, which is the failure this is.
    /// </para>
    /// </remarks>
    private static bool ShowsAPlaceholder(GameEvent e, string text) =>
        (Placeholder(e.SourceName) is { } source && text.Contains(source, StringComparison.Ordinal))
        || (Placeholder(e.TargetName) is { } target && text.Contains(target, StringComparison.Ordinal));

    private static string? Placeholder(string? name) =>
        name is not null && CardNames.IsPlaceholder(name) ? name : null;

    /// <summary>
    /// A draw of a card the log never named, which beats leave out.
    /// </summary>
    /// <remarks>
    /// This was already the behaviour and is only written down here. It used to fall
    /// out of the unnamed-subject rule by accident, and rewriting that rule to read the
    /// finished line would have let it back in: <c>Drew</c>'s wording for a card it
    /// cannot name is "Opponent draws a card", which names nobody and so passes. Across
    /// the archive that is 3755 lines, one per opponent turn in 515 of 529 transcripts,
    /// saying only that the draw step happened.
    /// <para>
    /// So it stays out, but as a decision rather than a side effect: a draw nobody can
    /// name is the draw step, and beats already leave routine structure to verbose.
    /// "You draw Hop to It" is unaffected — it names the card, which is the whole of
    /// what makes it worth a line.
    /// </para>
    /// </remarks>
    private static bool IsRoutineDraw(GameEvent e) =>
        e.Kind == EventKind.Drew && Placeholder(e.SourceName) is not null;

    /// <summary>
    /// What happened before turn one, in the order it happened: the die roll, then who
    /// is on the play, then the opening hands. Empty when the log carried none of it,
    /// which is what keeps a match with no opening from growing an empty heading.
    /// </summary>
    /// <remarks>
    /// A game after the first opens differently and has to say so. There is no die roll
    /// — the loser of the previous game chooses who begins — so the sentence names the
    /// chooser and what they chose, in the same shape the roll winner's does. Nothing
    /// says what they lost, because the line that ends the previous game, two lines
    /// above, has just said it.
    /// </remarks>
    private static List<string> OpeningLines(Transcript t, Opening? opening)
    {
        var lines = new List<string>();
        if (opening is not { } o) return lines;

        if (o.WinnerSeat is { } winner)
        {
            // High first, whichever seat rolled it: the sentence is about a roll being
            // won, and "wins the die roll 8 to 20" reads as a loss.
            var high = o.Rolls.Max(r => r.Value);
            var low = o.Rolls.Min(r => r.Value);
            var roll =
                $"{Who(winner, t)} {Verb(winner, "win", "wins", t)} the die roll {high} to {low}";

            if (o.FirstPlayerSeat is not { } first)
                // The roll is known but the game never opened a turn, so who would have
                // gone first is genuinely unknown. Saying only what was seen.
                lines.Add(roll);
            else if (first == winner)
                lines.Add($"{roll} and {Verb(winner, "play", "plays", t)} first");
            else
            {
                // Two lines rather than one long sentence, so each starts with the
                // player it is about — the reader has to come away with the right
                // player on the play, and that is the clause that must not be skimmed.
                lines.Add($"{roll} and {Verb(winner, "choose", "chooses", t)} to draw");
                lines.Add(OnThePlay(first, t));
            }
        }
        else if (o.ChoosingSeat is { } chooser)
        {
            var chose = $"{Who(chooser, t)} {Verb(chooser, "choose", "chooses", t)}";

            if (o.FirstPlayerSeat is not { } first)
                // Somebody chose, but the game never opened a turn, so what they chose
                // is genuinely unknown. Saying only what was seen.
                lines.Add($"{chose} who begins");
            else if (first == chooser)
                lines.Add($"{chose} to play first");
            else
            {
                // Split for the same reason the die roll's is: the reader has to come
                // away with the right player on the play.
                lines.Add($"{chose} to draw");
                lines.Add(OnThePlay(first, t));
            }
        }
        else if (o.FirstPlayerSeat is { } only)
        {
            lines.Add(OnThePlay(only, t));
        }

        lines.AddRange(HandLines(o, t));
        return lines;
    }

    private static string OnThePlay(int seat, Transcript t) =>
        $"{Who(seat, t)} {Verb(seat, "play", "plays", t)} first";

    /// <summary>
    /// What each player did with their opening hand, you first as everywhere else.
    /// </summary>
    /// <remarks>
    /// A seat missing from the map is a seat whose pre-game state was never read, and it
    /// gets no line: "keeps seven" is a claim, and the evidence for it is having watched
    /// the mulligan phase go by without a count appearing.
    /// <para>
    /// When neither player mulliganed the two lines become one. Standing silent instead
    /// would leave a reader unable to tell "both kept" from "the log did not say", which
    /// is the whole thing this section exists to stop; but 132 of the 152 archived
    /// matches have no mulligan at all, and spending two lines to report that nothing
    /// happened, in seven transcripts out of eight, is how a section becomes furniture.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> HandLines(Opening o, Transcript t)
    {
        var seats = new List<int>();
        if (t.You?.Seat is { } you && o.Mulligans.ContainsKey(you)) seats.Add(you);
        if (t.Opponent?.Seat is { } them && o.Mulligans.ContainsKey(them)) seats.Add(them);

        if (seats.Count == 0) yield break;

        if (seats.Count == 2 && seats.TrueForAll(s => o.Mulligans[s] == 0))
        {
            yield return $"Both players keep {CardCounts[Opening.StartingHandSize]}";
            yield break;
        }

        foreach (var seat in seats)
            yield return o.Mulligans[seat] == 0
                ? $"{Who(seat, t)} {Verb(seat, "keep", "keeps", t)} {CardCounts[o.Kept(seat)]}"
                : $"{Who(seat, t)} {Verb(seat, "mulligan", "mulligans", t)} " +
                  $"to {CardCounts[o.Kept(seat)]}";
    }

    /// <summary>Life totals entering the turn, always ordered you-first.</summary>
    private static string LifeScore(GameEvent e, Transcript t)
    {
        if (e.LifeSeat1 == 0 && e.LifeSeat2 == 0) return "";
        var yours = t.You?.Seat == 2 ? e.LifeSeat2 : e.LifeSeat1;
        var theirs = t.You?.Seat == 2 ? e.LifeSeat1 : e.LifeSeat2;
        return $"  (You {yours} · Opponent {theirs})";
    }

    private static string Who(int? seat, Transcript t) =>
        seat is null ? "Someone" : seat == t.You?.Seat ? "You" : "Opponent";

    private static string Verb(int? seat, string youForm, string theyForm, Transcript t) =>
        seat == t.You?.Seat ? youForm : theyForm;

    /// <summary>
    /// How long the turn ran, on the turns long enough for that to be the point. The
    /// sentence is about the turn and never about a player: the span covers whoever
    /// was deciding, whoever was responding, and the animations in between, and
    /// <see cref="TurnClock"/> cannot tell those apart. "Opponent took 1m 48s" would
    /// be an accusation the log does not support; "1 minute 48 seconds elapsed" is
    /// what was actually measured.
    /// </summary>
    private static string Elapsed(GameEvent e, IReadOnlyDictionary<int, TimeSpan> longTurns) =>
        longTurns.TryGetValue(e.Seq, out var d) ? $" · {TurnClock.Spoken(d)} elapsed" : "";

    /// <summary>
    /// A zone transfer whose category carries no verb of its own, said as where the card
    /// ended up.
    /// </summary>
    /// <remarks>
    /// These used to read "Forest moves (Put)" — the only place in the transcript where
    /// Arena's own vocabulary reached the reader, across 153 lines of the archive. The
    /// category is bookkeeping: "Put" covers a fetchland finding a Forest, a mulligan
    /// bottoming a card and a tutor putting one in hand, which have nothing in common
    /// except that the engine had no better word. The destination is the part worth
    /// reading, and it is in the same annotation.
    /// <para>
    /// A category that names a mechanic rather than the bare fact of moving is kept
    /// alongside it, so "Warp" is not flattened into a plain exile and a mechanic added
    /// in some future set surfaces rather than disappearing. A move that begins and ends
    /// in the same zone is a shuffle or a reorder and says nothing worth a line.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How much of a granted ability's rules text the line carries.
    /// </summary>
    /// <remarks>
    /// A granted keyword is a word or two — "first strike", "menace" — and is left
    /// alone, which needs no test of length because <see cref="AbilityText.Clause"/> has
    /// already separated the two: a keyword carries no sentence punctuation and a stated
    /// rule always does, so only a rule arrives in quotes.
    /// <para>
    /// A stated rule can run to a paragraph. Measured over the archive: 438 grant lines
    /// carry one, the median is 31 characters and reads perfectly well in place, but 99
    /// of the lines run past 110 characters and the longest rule is 212 — a transcript
    /// line that is mostly Oracle text. Verbose keeps all of it, because that is the view
    /// that exists to hold everything the log said. Beats keeps the head, which is the
    /// part that says what the ability <em>is</em>: "{T}: Add {U}" before the clause
    /// restricting where the mana may be spent.
    /// </para>
    /// <para>
    /// The limit is derived rather than picked. At 60 characters, 22% of grant lines are
    /// shortened and 11 over-long lines remain — and those 11 are long because the card
    /// names are long, which no amount of trimming the ability can fix. Tightening to 50
    /// shortens 145 lines instead of 95 to save six more, which is paying a third of the
    /// grants to fix six.
    /// </para>
    /// </remarks>
    private const int AbilityLimit = 60;
    private const char OpenQuote = '\u201c';
    private const char CloseQuote = '\u201d';

    /// <summary>
    /// When in the turn a spell was cast, for the casts where that is the play — or
    /// nothing at all, which is most of them.
    /// </summary>
    /// <remarks>
    /// The beats view reported <em>that</em> an instant resolved and never <em>when</em>,
    /// and for a card whose whole identity is its timing that loses the play (#148). A
    /// removal spell cast in upkeep, in response to a trigger, after blockers, or in the
    /// second main phase all rendered identically — and "cast a trick after blockers
    /// were declared" and "cast it before attacks" are different plays with different
    /// quality. A transcript that cannot tell them apart cannot be used to review them.
    /// <para>
    /// Only the casts that are not the ordinary case are marked. A spell cast on your
    /// own turn in a main phase is what casting normally means, and annotating every one
    /// of those would bury the handful that matter. So the test is whether the caster
    /// held the turn AND was in a main phase; anything else gets the step named. It keys
    /// off the turn and the phase rather than off card type, because "an instant" is not
    /// the question — a sorcery-speed spell cast in someone else's main phase would be a
    /// bug worth seeing, and flash makes card type a poor proxy anyway.
    /// </para>
    /// <para>
    /// Beats only. Verbose already prints the step transitions as their own lines, so
    /// this would be saying the same thing twice on the density that least needs help.
    /// </para>
    /// <para>
    /// It sits against the spell's name rather than at the end of the line, because a
    /// target carries its own parenthetical — "targeting Bristly Bill (2/2 → 0/0)" —
    /// and two of them running together read as one confused aside rather than as two
    /// facts. Against the name it is unambiguously about the cast.
    /// </para>
    /// </remarks>
    private static string CastTiming(GameEvent e, Density density) =>
        density == Density.Verbose || !OutsideYourOwnMainPhase(e) || StepOrPhase(e) is not { } when
            ? ""
            : $" ({when})";

    /// <summary>
    /// Whether a cast happened anywhere other than its caster's own main phase — the
    /// one place a spell is cast by default.
    /// </summary>
    private static bool OutsideYourOwnMainPhase(GameEvent e) =>
        e.ActorSeat != e.ActiveSeat || e.Phase is not (FirstMain or SecondMain);

    // Phase and Step numbering read from Arena's own card database rather than assumed
    // — the Enums table joined to Localizations_enUS gives, for Phase: 1 Beginning,
    // 2 1st Main, 3 Combat, 4 2nd Main, 5 Ending; and for Step: 1 Untap, 2 Upkeep,
    // 3 Draw, 4 Begin Combat, 5 Declare Attackers, 6 Declare Blockers, 7 Combat Damage,
    // 8 End Combat, 9 End, 10 Cleanup, 11 First Strike Damage. A main phase carries no
    // step, which is why step 0 falls back to naming the phase.
    private const int FirstMain = 2;
    private const int SecondMain = 4;

    /// <summary>
    /// The step a cast landed in, or the phase when the step says nothing.
    /// </summary>
    /// <remarks>
    /// Written here in English rather than read back from the card database. The wording
    /// the database carries is the label for a phase transition — "Combat · Declare
    /// Attackers" — and taking the half of it this wants would mean splitting a string
    /// built for a reader, which is how a rewording quietly becomes a behaviour change.
    /// Every other word the narrator emits is its own; these are too.
    /// </remarks>
    private static string? StepOrPhase(GameEvent e) => e.Step switch
    {
        1 => "untap",
        2 => "upkeep",
        3 => "draw step",
        4 => "beginning of combat",
        5 => "declare attackers",
        6 => "declare blockers",
        7 => "combat damage",
        8 => "end of combat",
        9 => "end step",
        10 => "cleanup",
        11 => "first-strike damage",
        _ => e.Phase switch
        {
            1 => "beginning phase",
            FirstMain => "first main phase",
            3 => "combat",
            SecondMain => "second main phase",
            5 => "ending phase",
            // Phase 0 as well as anything Arena adds later: better to say nothing than
            // to name a part of the turn by a number nobody recognises.
            _ => null
        }
    };

    private static string Ability(string detail, Density density)
    {
        if (density == Density.Verbose || detail.Length <= AbilityLimit) return detail;

        var space = detail.LastIndexOf(' ', Math.Min(AbilityLimit, detail.Length - 1));
        var head = (space > 0 ? detail[..space] : detail[..AbilityLimit]).TrimEnd();

        // Off whatever the cut left dangling, so the ellipsis follows a word rather than
        // a conjunction or a comma. Repeated because a list can end ", and".
        for (var trimming = true; trimming && head.Length > 0;)
        {
            var before = head;
            foreach (var tail in Dangling)
                if (head.EndsWith(tail, StringComparison.Ordinal))
                {
                    head = head[..^tail.Length].TrimEnd();
                    break;
                }
            trimming = head != before;
        }

        // A cut inside a quoted clause leaves it open. Closing it is not cosmetic: four
        // lines in the archive ended with a quote that never opened, because the first
        // version of this stripped Detail's outer characters on the assumption that it
        // was one quoted rule. It is not — EventExtractor joins a permanent's clauses,
        // so Detail can be a keyword and a rule, or several rules.
        var open = head.Count(c => c == OpenQuote) > head.Count(c => c == CloseQuote);
        return head + "\u2026" + (open ? CloseQuote.ToString() : "");
    }

    private static readonly string[] Dangling = [" and", " or", ",", ";", ":", "."];

    private static string? ZoneMove(GameEvent e)
    {
        var how = Mechanic(e.Detail) is { } named ? $" ({named})" : "";

        if (e.ToZone is null) return $"{e.SourceName} moves{how}";
        if (e.ToZone == e.FromZone) return null;

        var where = e.ToZone switch
        {
            "ZoneType_Battlefield" => "is put onto the battlefield",
            "ZoneType_Hand" => "is put into hand",
            "ZoneType_Library" => "is put into the library",
            "ZoneType_Graveyard" => "is put into the graveyard",
            "ZoneType_Exile" => "is exiled",
            "ZoneType_Stack" => "goes on the stack",
            _ => null
        };

        return where is null ? $"{e.SourceName} moves{how}" : $"{e.SourceName} {where}{how}";
    }

    /// <summary>
    /// The transfer category when it names a mechanic, or null when it is only the engine
    /// keeping its own books.
    /// </summary>
    /// <remarks>
    /// "Put" is the engine saying only that something moved and "nil" is it saying nothing
    /// at all; neither adds to a sentence that already names the destination. "Separate"
    /// is it splitting a revealed pile, and was the largest single parenthetical in the
    /// archive at 39 lines — "Island is put into hand (Separate)" tells a reader nothing
    /// the first four words did not. "DestroyNoRegenerate" reports that a permission
    /// nobody exercised was withheld.
    /// <para>
    /// Everything else is kept, which is the point: Warp, Conjure, Seek and Draft each
    /// name something a player did, and a mechanic printed in some future set should
    /// surface rather than disappear. ManifestLike is left in for exactly that reason
    /// rather than because it has been judged — it is the case the rule exists to protect
    /// (#73).
    /// </para>
    /// <para>
    /// Asked on every path, including the two that have no destination to name. Those two
    /// used to interpolate the category unconditionally, which is how one "moves (Put)"
    /// survived into the archive after the reading was supposedly retired, and how a
    /// category the log omitted would have rendered as an empty pair of brackets.
    /// </para>
    /// </remarks>
    private static string? Mechanic(string? detail) =>
        detail is null or "" or "Put" or "nil" or "Separate" or "DestroyNoRegenerate"
            ? null
            : detail;

    /// <summary>
    /// Where a return went, when the log said.
    /// </summary>
    /// <remarks>
    /// This used to read "to hand" unconditionally. Across the archive a Return goes to
    /// hand 61 times and to the battlefield 47, so nearly half of them named a zone the
    /// card did not go to — a flicker effect read as a bounce, which is close to the
    /// opposite. A return with no destination recorded now says only that it returned,
    /// because naming the commoner of two outcomes is guessing.
    /// </remarks>
    private static string ReturnedTo(GameEvent e) => e.ToZone switch
    {
        "ZoneType_Hand" => " to hand",
        "ZoneType_Battlefield" => " to the battlefield",
        "ZoneType_Library" => " to the library",
        "ZoneType_Graveyard" => " to the graveyard",
        "ZoneType_Exile" => " to exile",
        _ => ""
    };

    /// <summary>
    /// Where a spell was cast from, when that is not where spells come from.
    /// </summary>
    /// <remarks>
    /// A flashback, an escape, an adventure and a foretell all rendered as an ordinary
    /// cast, so the page showed a card being cast that the reader had just watched go to
    /// the graveyard, with nothing in between to account for it. That reads as the
    /// parser repeating a line rather than as the play it was (#127).
    /// <para>
    /// The complaint is about a missing account, not about an impossibility. A card can
    /// legitimately be cast from hand more than once — anything that returns it there
    /// does it — and singleton limits how many copies a deck may hold, not how often one
    /// card may be cast. What made the transcript unreadable was casting a card whose
    /// last reported whereabouts were somewhere it could not be cast from.
    /// </para>
    /// <para>
    /// Counted over 1,238 archived matches: 15,632 casts come from hand, and the ones
    /// worth marking are 461 from exile across 206 matches, 107 from a graveyard across
    /// 63, and 41 from a library across 21.
    /// </para>
    /// <para>
    /// The command zone is deliberately not among them, though at 1,488 casts across 715
    /// matches it is much the largest non-hand source. It is where a commander lives and
    /// the only place one can be cast from, so the name of the card already says it —
    /// and a recast is explained by the "returns to the command zone" line the
    /// transcript already carries. Marking it would put a phrase on more than half the
    /// archive's matches to report the unsurprising.
    /// </para>
    /// </remarks>
    private static string CastFrom(GameEvent e) => e.FromZone switch
    {
        "ZoneType_Graveyard" => " from the graveyard",
        "ZoneType_Exile" => " from exile",
        "ZoneType_Library" => " from the library",
        _ => ""
    };

    private static string? Phrase(GameEvent e, Transcript t, Density density) => e.Kind switch
    {
        EventKind.TurnStart =>
            $"Turn {e.Turn} — {Who(e.ActorSeat ?? e.ActiveSeat, t)}{LifeScore(e, t)}",

        EventKind.BoardSnapshot when !string.IsNullOrWhiteSpace(e.Detail) =>
            $"{Who(e.ActorSeat, t)} control{(e.ActorSeat == t.You?.Seat ? "" : "s")}: {e.Detail}",

        EventKind.LandPlayed when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "play", "plays", t)} {e.SourceName}",

        EventKind.SpellCast when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "cast", "casts", t)} {e.SourceName}"
            + CastFrom(e)
            + CastTiming(e, density)
            + (e.TargetName is not null ? $", targeting {e.TargetName}" : ""),

        EventKind.Resolved when e.SourceName is not null => $"{e.SourceName} resolves",

        EventKind.Countered when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} counters {e.SourceName}"
                : $"{e.SourceName} is countered",

        EventKind.Drew when e.SourceName is not null && e.ActorSeat == t.You?.Seat =>
            $"You draw {e.SourceName}",
        EventKind.Drew => $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "draw", "draws", t)} a card",

        EventKind.Discarded when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "discard", "discards", t)} {e.SourceName}",

        // Naming what caused it is the difference between a list of things that
        // happened and a transcript you can follow.
        EventKind.Destroyed when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} destroys {e.SourceName}"
                : $"{e.SourceName} is destroyed",

        EventKind.Exiled when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} exiles {e.SourceName}"
                : $"{e.SourceName} is exiled",

        EventKind.Returned when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} returns {e.SourceName}{ReturnedTo(e)}"
                : $"{e.SourceName} returns{ReturnedTo(e)}",

        EventKind.Sacrificed when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "sacrifice", "sacrifices", t)} {e.SourceName}",

        EventKind.Milled when e.SourceName is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} mills {e.SourceName}"
                : $"{e.SourceName} is milled",

        EventKind.Surveilled when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "surveil", "surveils", t)} {e.SourceName}",
        // The graveyard is where every state-based action in the archive sends its
        // card — zero toughness, lethal damage, zero loyalty, the legend rule — except
        // one: SBA_Commander is the commander leaving the graveyard for the command
        // zone, and phrasing that trip in the graveyard's words buried Elspeth twice
        // on one line ("is put into the graveyard ×2", #18). An unrecorded destination
        // keeps the graveyard wording, because it is the right guess for every SBA
        // this has ever been measured against.
        EventKind.StateBasedAction when e.SourceName is not null =>
            e.ToZone == "ZoneType_Command"
                ? $"{e.SourceName} returns to the command zone"
                : $"{e.SourceName} is put into the graveyard",

        // Zero is not a small amount of damage, it is none — a 0/4 blocker deals no
        // damage at all under the rules, and "Gleaming Barrier deals 0 damage to Hare
        // Apparent" describes an event that did not happen. Guarded the same way
        // LifeChanged below already is.
        EventKind.Damage when e.Amount == 0 => null,
        EventKind.Damage when e.TargetSeat is not null =>
            $"{e.SourceName ?? "Something"} deals {e.Amount} damage to {Who(e.TargetSeat, t)}",
        EventKind.Damage when e.TargetName is not null =>
            $"{e.SourceName ?? "Something"} deals {e.Amount} damage to {e.TargetName}",

        EventKind.LifeChanged when e.Amount != 0 =>
            $"{Who(e.TargetSeat, t)} " +
            $"{Verb(e.TargetSeat, e.Amount > 0 ? "gain" : "lose", e.Amount > 0 ? "gains" : "loses", t)} " +
            $"{Math.Abs(e.Amount)} life",

        EventKind.TokenCreated when e.TargetName is not null =>
            $"{e.SourceName ?? "An effect"} creates {e.TargetName}",

        // The permanent is named at the size it was changed from, so "Tifa Lockhart 1/2
        // gets +1/+0" carries both ends of the change in one line.
        //
        // No duration is claimed. The annotation carries only the two deltas, and the
        // same one covers a landfall pump that expires at end of turn and an aura that
        // lasts as long as it stays attached — so saying "until end of turn" would be
        // right about Tifa and wrong about Royal Treatment.
        EventKind.StatsModified when e.TargetName is not null && e.Detail is not null =>
            $"{e.TargetName} gets {e.Detail}",

        // The permanent is named as it stands now, with where it came from in the
        // parenthesis, because the change has already happened by the time this is said.
        EventKind.StatsExpired when e.TargetName is not null && e.Detail is not null =>
            $"{e.TargetName} returns to {e.Detail.Split('→')[1].Trim()}",

        // Named by the half that opened, not by the whole card: "unlocks Porcelain
        // Gallery" is what happened, where "unlocks Dollmaker's Shop // Porcelain
        // Gallery" would name the side that was already open too.
        EventKind.DoorUnlocked when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "unlock", "unlocks", t)} {e.SourceName}",

        EventKind.CounterChanged when e.TargetName is not null && e.Amount != 0 =>
            $"{e.TargetName} {(e.Amount > 0 ? "gets" : "loses")} {Math.Abs(e.Amount)} " +
            $"{(e.Detail is null ? "" : e.Detail + " ")}counter" +
            $"{(Math.Abs(e.Amount) == 1 ? "" : "s")}",

        // Cause first and active, the same shape as "Split Up destroys Hare Apparent".
        // Without it a trigger line names the ability and stops, so a reader watching
        // three of them fire in a row has nothing to tell them apart or say what the
        // player did to set them off.
        EventKind.Triggered when e.SourceName is not null && e.CauseName is not null =>
            $"{e.CauseName} triggers {e.SourceName}",
        EventKind.Triggered when e.SourceName is not null => $"{e.SourceName} triggers",

        // The player and the permanent, not the ability: "Opponent activates Lander"
        // is the deliberate play "Lander's ability triggers" misreported. What the
        // activation did lands on the following lines, same as any resolution.
        EventKind.Activated when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "activate", "activates", t)} " +
            e.SourceName,

        // Passive on purpose. The same annotation covers an aura arriving on a creature
        // and a player equipping a sword, and "Opponent equips" would be a claim about
        // who acted that the log does not make for the first of those.
        EventKind.Attached when e.SourceName is not null && e.TargetName is not null =>
            $"{e.SourceName} is attached to {e.TargetName}",

        // Arena's own wording, from the card: "When this Class becomes level 2, …".
        EventKind.LevelUp when e.SourceName is not null && e.Amount > 0 =>
            $"{e.SourceName} becomes level {e.Amount}",

        // Cause first and active where the log names one, the same shape as "Split Up
        // destroys Hare Apparent". This is the line that connects "Enter the Avatar
        // State resolves" to the first-strike damage two lines later — without it the
        // grant is invisible and the damage step reads like the parser lost count.
        EventKind.AbilityGained when e.TargetName is not null && e.Detail is not null =>
            e.CauseName is not null
                ? $"{e.CauseName} gives {e.TargetName} {Ability(e.Detail, density)}"
                : $"{e.TargetName} gains {Ability(e.Detail, density)}",

        // "loses", the same verb counters and life use. No cause: a wear-off has no
        // actor, and the grant line already named who put the ability there.
        EventKind.AbilityExpired when e.TargetName is not null && e.Detail is not null =>
            $"{e.TargetName} loses {Ability(e.Detail, density)}",

        // "enters as" rather than "becomes" for the clones that arrived copying
        // something: nothing changed about them, they came that way, and "becomes"
        // would invite the reader to look back for the moment it happened.
        // Named for the seat that gains it, because that is the fact the rest of the
        // transcript then depends on: every later line about this permanent — the board
        // it appears on, who attacks with it — is about the player named here. Without
        // it those lines look like the parser losing track of a creature (#124).
        //
        // The cause is left to the line above rather than repeated. Arena announces the
        // effect's trigger or resolution immediately before, so "Loki, Lord of Misrule's
        // ability triggers" is already on the page, and naming it again turns one theft
        // into two sentences that both look like the whole story.
        EventKind.ControlChanged when (e.Detail ?? e.SourceName) is { } taken =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "gain", "gains", t)} control of {taken}",

        EventKind.Copied when e.SourceName is not null && e.TargetName is not null
                              && e.Detail == EventExtractor.PermanentCopy =>
            $"{e.SourceName} enters as a copy of {e.TargetName}",

        // "a temporary copy" says the one thing about the duration the log supports.
        // The annotation's Duration is a bare code with no table to resolve it against,
        // so "until end of turn" would be a length nobody measured — and the two codes
        // in the archive do not even mean the same length.
        EventKind.Copied when e.SourceName is not null && e.TargetName is not null
                              && e.Detail == EventExtractor.TemporaryCopy =>
            e.CauseName is not null
                ? $"{e.CauseName} makes {e.SourceName} a temporary copy of {e.TargetName}"
                : $"{e.SourceName} becomes a temporary copy of {e.TargetName}",

        EventKind.Attack when e.SourceName is not null && e.TargetName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "attack", "attacks", t)} " +
            $"{e.TargetName} with {e.SourceName}",
        EventKind.Attack when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "attack", "attacks", t)} with {e.SourceName}",

        EventKind.Block when e.SourceName is not null && e.TargetName is not null =>
            $"{e.SourceName} blocks {e.TargetName}",
        EventKind.Block when e.SourceName is not null => $"{e.SourceName} blocks",

        EventKind.Scry when e.Detail is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "scry", "scries", t)} {e.Amount}, " +
            $"putting {e.Detail}",
        EventKind.Scry => $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "scry", "scries", t)}",
        EventKind.Revealed when e.SourceName is not null => $"{e.SourceName} is revealed",

        // Named subject, like every other line. Without one this read as a continuation
        // of whatever came before it — in a list where every other line begins with a
        // player, "taps Plains for mana" attaches itself to the previous line's actor,
        // and 4,528 verbose lines across 206 pages did exactly that.
        EventKind.ManaPaid when e.SourceName is not null =>
            $"{Who(e.ActorSeat, t)} {Verb(e.ActorSeat, "tap", "taps", t)} " +
            $"{e.SourceName} for mana",
        EventKind.PhaseChange when !string.IsNullOrWhiteSpace(e.Detail) => $"— {e.Detail} —",
        EventKind.PhaseChange => null,
        EventKind.Unknown => $"[unhandled: {e.RawType}]",

        // Said in both densities. A reader skimming the beats is exactly who needs to
        // know that this turn is not a complete account of itself, and it is the one
        // line on the page that explains a board changing with nothing to explain it.
        EventKind.LogGap => e.Detail,

        EventKind.GameEnd => e.Detail,
        EventKind.ZoneMove when e.SourceName is not null => ZoneMove(e),

        _ => null
    };
}
