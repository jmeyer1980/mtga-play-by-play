using MtgaPbp.Core;
using MtgaPbp.Render;

namespace MtgaPbp.Cli;

/// <summary>
/// What <c>why</c> does with the turns it was asked for.
/// </summary>
public enum WhyOutcome
{
    /// <summary>Show the turns the plan carries.</summary>
    Render,

    /// <summary>Show the match's turn list instead. This is the discoverability path.</summary>
    ListTurns,

    /// <summary>Show nothing; the complaint says what could not be read or found.</summary>
    Refuse
}

/// <summary>
/// The reading of <c>why</c>'s turn operands against the turns a match actually has.
/// </summary>
/// <param name="Outcome">What to do about it.</param>
/// <param name="Turns">Ascending and distinct, and empty unless the outcome is to render.</param>
/// <param name="Complaint">
/// What could not be read or could not be found, or null when everything could. It is
/// set independently of the outcome: an operand that could not be read at all still
/// yields a turn list, and the reader is still told why they got one.
/// </param>
/// <param name="ExitCode">0 for anything shown, 2 for an operand, 4 for a missing turn.</param>
public readonly record struct TurnPlan(
    WhyOutcome Outcome, IReadOnlyList<int> Turns, string? Complaint, int ExitCode);

/// <summary>
/// Shows turns' rendered lines beside the raw annotations that produced them, and the
/// prompts the game put to the player while they were happening.
/// </summary>
/// <remarks>
/// Every output bug found so far came from reading a transcript, thinking "that line
/// looks wrong", and then hand-writing a script to walk the gzipped archive and print
/// what the log actually said. That loop is the most productive tool in the project and
/// the slowest part of it is the script. This is that script, kept.
/// <para>
/// It resolves instance ids to card names, because the raw log is a wall of integers and
/// the whole question is usually "which permanent is 405". Ids are shown as well as
/// names: a name that looks wrong is the bug, and the id is what you search the archive
/// for next.
/// </para>
/// <para>
/// A refused action leaves no annotations at all — nothing happened — so the middle
/// section reads the requests instead, which is where a cost that could not be paid is
/// recorded. See <see cref="Negotiations"/>.
/// </para>
/// <para>
/// Reading the operands is kept apart from reading the archive, in
/// <see cref="ParseTurns"/> and <see cref="Plan"/>, because it is the half that has to
/// be tested and the other half cannot be: <c>Run</c> needs Arena's own card database,
/// which is 237 MB and not on CI.
/// </para>
/// </remarks>
public static class Why
{
    /// <summary>
    /// The widest range that will be read as one. Nothing is played to five hundred
    /// turns; the cap is here so that <c>1-2000000000</c> is refused as an operand
    /// rather than materialised into a set first and rejected turn by turn after.
    /// </summary>
    private const int WidestRange = 500;

    /// <summary>The most items named in one complaint before it starts counting instead.</summary>
    private const int MostNamed = 8;

    /// <summary>
    /// What separates one turn from the next inside a single operand. A comma because
    /// PowerShell leaves one there, and a space because a shell that was told to quote
    /// the lot — <c>why &lt;id&gt; "13 14"</c> — hands over one argument, and refusing
    /// that would be pedantry rather than safety.
    /// </summary>
    private static readonly char[] Separators = [',', ' ', '\t'];

    public static int Run(Config cfg, string? matchId, params string[] turnArgs)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            Console.Error.WriteLine("""
                usage: mtga-pbp why <matchId> [turns]

                With no turns, lists the turns of the match. With one or more - 13, or
                13 14, or 13-15, or 13,14 - shows each turn's transcript lines, then what
                the game asked you and what it would cost, then the raw annotations
                behind them, ids resolved, in ascending order.
                """);
            return 2;
        }

        var archive = new RawArchive(cfg.ArchiveDir);
        if (!archive.Contains(matchId))
        {
            Console.Error.WriteLine($"no archived match with id {matchId}");
            Console.Error.WriteLine($"searched {Path.GetFullPath(cfg.ArchiveDir)}");
            return 4;
        }

        // Named rather than thrown: `why` is a diagnostic, and a stack trace is the
        // least diagnostic thing it could print about a file it cannot read (#131).
        var read = archive.TryRead(matchId);
        if (read.Damage is { } why)
        {
            Console.Error.WriteLine($"the archived slice for {matchId} could not be read ({why})");
            Console.Error.WriteLine(
                "deleting it lets a later capture rewrite it, if the match is still in an Arena log");
            return 4;
        }

        var raw = read.Lines;
        using var cards = OpenCards(cfg);
        var transcript = new EventExtractor(cards).Extract(matchId, raw);
        var lines = Narrator.Narrate(transcript, Density.Verbose);

        var headers = lines.Where(l => l.IsTurnHeader && l.Turn > 0).ToList();
        var plan = Plan(turnArgs, headers.Select(l => l.Turn).ToHashSet());

        // Before anything it might scroll off the top of: the reason this run is not the
        // run that was asked for.
        if (plan.Complaint is not null) Console.Error.WriteLine($"why: {plan.Complaint}");
        if (plan.Outcome is WhyOutcome.Refuse) return plan.ExitCode;

        if (plan.Outcome is WhyOutcome.ListTurns)
        {
            Console.WriteLine(TranscriptSummary.Title(transcript));
            Console.WriteLine(TranscriptSummary.Subtitle(transcript));
            Console.WriteLine();
            foreach (var header in headers)
                Console.WriteLine($"  {header.Text}");
            Console.WriteLine();
            Console.WriteLine($"mtga-pbp why {matchId} <turns>");
            Console.WriteLine("  one (13), several (13 14, or 13,14) or a range (13-15)");
            return plan.ExitCode;
        }

        var games = transcript.Games.Select(g => g.Number).DefaultIfEmpty(1).ToList();
        var reached = headers.Select(l => (l.Turn, l.Game)).ToHashSet();

        // Turn numbers are per game, so a turn the match reached is not a turn every
        // game reached. Only the pairs a game actually played are kept: keeping the
        // rest is what would fill a range over a Bo3 with empty sections. A single-game
        // match skips the check, so its output is whatever it always was.
        var showing = (from turn in plan.Turns
                       from game in games
                       where games.Count == 1 || reached.Contains((turn, game))
                       select (Turn: turn, Game: game)).ToList();

        // Every section asked for, from one walk of the archived match. Asking a turn
        // at a time re-parsed the whole log once per turn, which a whole-match dump
        // paid for in full.
        var dump = LogDump.ForTurns(raw, cards, showing);

        foreach (var (turn, game) in showing)
        {
            var of = games.Count > 1 ? $" of game {game}" : "";
            Console.WriteLine($"=== turn {turn}{of}: what the transcript says ===");
            foreach (var l in lines.Where(l => l.Turn == turn && l.Game == game))
                Console.WriteLine($"  {(l.IsTurnHeader ? "" : "- ")}{l.Text}");

            // Between the two, and only when there is something to say. It answers a
            // different question from either neighbour — not what happened but what the
            // player was asked — and it is short, while the annotation dump below it ran
            // to 250 lines on the turn that prompted this. Below that wall it would
            // scroll away from exactly the reader who came looking for it.
            var asked = dump[(turn, game)].Negotiations;
            if (asked.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== turn {turn}{of}: what the game asked you ===");
                foreach (var l in asked) Console.WriteLine($"  {l}");
            }

            Console.WriteLine();
            Console.WriteLine($"=== turn {turn}{of}: what the log says ===");
            foreach (var l in dump[(turn, game)].Annotations)
                Console.WriteLine($"  {l}");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Reads the turn operands as a set of turn numbers, keeping whatever it could not
    /// read rather than dropping it.
    /// </summary>
    /// <remarks>
    /// Four forms, because four are what a reader types. <c>13</c> and <c>13 14</c> are
    /// the obvious ones and <c>13-15</c> is the range. The fourth is <c>13, 14</c>:
    /// PowerShell's array syntax reaches an exe as <c>13,</c> and <c>14</c>, which is why
    /// the natural thing to type used to render turn 13 and say nothing about 14. So a
    /// comma separates here too, and a trailing one is not an error.
    /// <para>
    /// A reversed range is read ascending. Somebody who types <c>15-13</c> has said what
    /// they want unambiguously, and the output is ordered by turn either way.
    /// </para>
    /// <para>
    /// Zero and below are unreadable rather than absent. Turn numbering starts at 1, so
    /// <c>0</c> is not a turn this match happened to stop short of — it is not a turn,
    /// and saying "this match has no turn 0, its turns run 1 to 22" would answer a
    /// question nobody asked.
    /// </para>
    /// </remarks>
    public static (IReadOnlyList<int> Turns, IReadOnlyList<string> Unreadable) ParseTurns(
        IEnumerable<string> operands)
    {
        var turns = new SortedSet<int>();
        var unreadable = new List<string>();

        foreach (var operand in operands)
            foreach (var piece in operand.Split(
                         Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var ends = piece.Split('-');

                if (ends.Length == 1 && int.TryParse(ends[0], out var one) && one > 0)
                {
                    turns.Add(one);
                    continue;
                }

                if (ends.Length == 2 &&
                    int.TryParse(ends[0], out var from) && int.TryParse(ends[1], out var to))
                {
                    if (from > to) (from, to) = (to, from);
                    if (from > 0 && to - from < WidestRange)
                    {
                        for (var turn = from; turn <= to; turn++) turns.Add(turn);
                        continue;
                    }
                }

                unreadable.Add(piece);
            }

        return (turns.ToList(), unreadable);
    }

    /// <summary>
    /// Decides what to do about the turns asked for, given the turns the match has.
    /// </summary>
    /// <remarks>
    /// The rule is that nothing renders until every operand has been read and every turn
    /// asked for exists. The alternative - complain, then render the part that was
    /// understood - was considered and rejected: a complaint printed above a long render
    /// scrolls away exactly like the dropped operand this replaces, so it would
    /// reproduce the bug one level up. Re-typing a <c>why</c> costs a second.
    /// <para>
    /// The one thing that does not refuse is an operand set that yielded no turns at all.
    /// <c>why &lt;id&gt; banana</c> printing the turn list is an accident of the old
    /// parse, but it is the accident people use to find out what turns exist, so it is
    /// kept - now with a line saying which word it could not read.
    /// </para>
    /// </remarks>
    public static TurnPlan Plan(IEnumerable<string> operands, IReadOnlyCollection<int> available)
    {
        var (turns, unreadable) = ParseTurns(operands);
        var cannotRead = unreadable.Count == 0
            ? null
            : $"cannot read {Named(unreadable, o => $"\"{o}\"")} as a turn - " +
              "turns are given as 13, or 13 14, or 13-15, or 13,14";

        if (turns.Count == 0) return new TurnPlan(WhyOutcome.ListTurns, [], cannotRead, 0);
        if (cannotRead is not null) return new TurnPlan(WhyOutcome.Refuse, [], cannotRead, 2);

        var absent = turns.Where(t => !available.Contains(t)).ToList();
        if (absent.Count == 0) return new TurnPlan(WhyOutcome.Render, turns, null, 0);

        var complaint = available.Count == 0
            ? "this match has no turns"
            : $"this match has no turn{(absent.Count > 1 ? "s" : "")} " +
              $"{Named(absent, t => t.ToString())} - its turns run " +
              $"{available.Min()} to {available.Max()}";

        return new TurnPlan(WhyOutcome.Refuse, [], complaint, 4);
    }

    /// <summary>Names what is wrong, up to the point where a list stops being readable.</summary>
    private static string Named<T>(IReadOnlyList<T> items, Func<T, string> show)
    {
        var named = string.Join(", ", items.Take(MostNamed).Select(show));
        return items.Count > MostNamed ? $"{named} and {items.Count - MostNamed} more" : named;
    }

    private static CardDb OpenCards(Config cfg)
    {
        var path = CardDb.FindDatabase(cfg.CardDbPath)
                   ?? throw new FileNotFoundException("Card database not found.");
        return new CardDb(path);
    }
}
