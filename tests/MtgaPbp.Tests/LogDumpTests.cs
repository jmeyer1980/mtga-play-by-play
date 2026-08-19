using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The diagnostic behind <c>mtga-pbp why</c>: one turn's raw annotations, ids resolved.
/// </summary>
/// <remarks>
/// Asserted against the real Bo3 fixture rather than a hand-built message, because the
/// thing it has to get right is exactly what a hand-built message would not exercise —
/// naming an object as it stood during the turn asked for, in a match where ids are
/// handed out twice.
/// </remarks>
public class LogDumpTests
{
    private static IReadOnlyList<string> Bo3Raw() =>
        GoldenFileTests.ReadFixture(GoldenFileTests.Bo3Fixture);

    private static ICardDb Cards() => FixtureCardDb.Load(GoldenFileTests.FixtureDir);

    [Test]
    public void A_turn_yields_its_annotations_with_ids_resolved_to_names()
    {
        var dump = LogDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 1).Annotations;

        Assert.That(dump, Is.Not.Empty);

        // Every annotation contributes a type line and a "by ... on [...]" line.
        Assert.That(dump.Count(l => l.StartsWith("    by ", StringComparison.Ordinal)),
            Is.GreaterThan(0));

        // An id on its own is what makes the raw log unreadable; the point of this is
        // that the name travels with it.
        var subjects = dump.Where(l => l.Contains(" on [", StringComparison.Ordinal)).ToList();
        Assert.That(subjects.Any(l => l.Contains('#')), Is.True, "ids are kept");
        Assert.That(subjects.Any(l => l.Contains("seat", StringComparison.Ordinal) ||
                                      l.Any(char.IsLetter)), Is.True, "names are resolved");
    }

    /// <summary>
    /// Each game's turn one is its own, in a match that has two of them.
    /// </summary>
    /// <remarks>
    /// Arena hands out instance ids again in each game, so a dump that ignored the game
    /// number would answer game one's questions out of game two's state — which is the
    /// exact bug that made a transcript claim a player cast a Plains.
    /// </remarks>
    [Test]
    public void Each_game_is_dumped_separately()
    {
        var one = LogDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 1).Annotations;
        var two = LogDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 2).Annotations;

        Assert.That(one, Is.Not.Empty);
        Assert.That(two, Is.Not.Empty);
        Assert.That(one, Is.Not.EqualTo(two));
    }

    [Test]
    public void A_turn_the_match_never_reached_yields_nothing_rather_than_throwing()
    {
        Assert.That(LogDump.ForTurn(Bo3Raw(), Cards(), turn: 999, game: 1).Annotations, Is.Empty);
        Assert.That(LogDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 9).Annotations, Is.Empty);
    }

    [Test]
    public void A_line_that_is_not_json_is_stepped_over()
    {
        // The scanner writes gap markers into the archive, and they are not JSON.
        IReadOnlyList<string> raw = ["not json at all", .. Bo3Raw()];
        Assert.That(LogDump.ForTurn(raw, Cards(), turn: 1, game: 1).Annotations, Is.Not.Empty);
    }

    // ---------- Issue 32: one walk, several turns ----------

    /// <summary>
    /// The whole point of reading the match once is that it answers exactly what
    /// reading it once per turn answered.
    /// </summary>
    /// <remarks>
    /// Asserted against the Bo3 fixture and across both games, because the thing that
    /// could break is the thing this fixture exists for: the tracker has to be in the
    /// right state when each turn arrives, and instance ids are handed out again in
    /// game two. A pass that collected turns without replaying between them would name
    /// game two's permanents out of game one's state and still look plausible.
    /// </remarks>
    [Test]
    public void One_walk_of_several_turns_says_what_one_walk_each_said()
    {
        (int Turn, int Game)[] wanted = [(1, 1), (2, 1), (1, 2)];

        var together = LogDump.ForTurns(Bo3Raw(), Cards(), wanted);

        Assert.That(together.Keys, Is.EquivalentTo(wanted));
        foreach (var (turn, game) in wanted)
        {
            var alone = LogDump.ForTurn(Bo3Raw(), Cards(), turn, game);
            Assert.That(together[(turn, game)].Annotations, Is.EqualTo(alone.Annotations),
                $"turn {turn} game {game} annotations");
            Assert.That(together[(turn, game)].Negotiations, Is.EqualTo(alone.Negotiations),
                $"turn {turn} game {game} prompts");
        }

        // And it is not vacuously equal on both sides.
        Assert.That(together[(1, 1)].Annotations, Is.Not.Empty);
        Assert.That(together[(1, 2)].Annotations, Is.Not.Empty);
    }

    [Test]
    public void Only_the_turns_asked_for_come_back()
    {
        var one = LogDump.ForTurns(Bo3Raw(), Cards(), [(1, 1)]);

        Assert.That(one.Keys, Is.EquivalentTo(new[] { (1, 1) }));
        Assert.That(LogDump.ForTurns(Bo3Raw(), Cards(), []), Is.Empty);
    }

    /// <summary>
    /// A turn the match never reached still gets a key, so the caller can print its
    /// heading and an empty section rather than crash on a lookup.
    /// </summary>
    [Test]
    public void A_turn_that_never_happened_comes_back_empty_rather_than_missing()
    {
        var dump = LogDump.ForTurns(Bo3Raw(), Cards(), [(1, 1), (999, 1)]);

        Assert.That(dump.ContainsKey((999, 1)), Is.True);
        Assert.That(dump[(999, 1)].Annotations, Is.Empty);
        Assert.That(dump[(999, 1)].Negotiations, Is.Empty);
        Assert.That(dump[(1, 1)].Annotations, Is.Not.Empty);
    }

    // ---------- Issue 20: what the game asked, from the same walk ----------

    /// <summary>
    /// Real traffic: the fixture's combat turn reports being asked to declare attackers,
    /// and names what was allowed to attack.
    /// </summary>
    /// <remarks>
    /// It reports the request twice, and that is the shape rather than a duplicate: the
    /// client sends it once before an attacker is picked and again after, and the two
    /// differ by the pick. Seeing the pair is what makes a cost that only appears on the
    /// second one legible, which is the whole of issue #20 on a turn where it bites.
    /// </remarks>
    [Test]
    public void A_turn_that_declared_attackers_reports_being_asked_to()
    {
        var asked = LogDump.ForTurn(Bo3Raw(), Cards(), turn: 6, game: 1).Negotiations;

        Assert.That(asked.Count(l => l.StartsWith("declare attackers", StringComparison.Ordinal)),
            Is.EqualTo(2), "asked before the pick and again after it");
        Assert.That(asked, Has.Exactly(2).Contains("allowed to attack: "));
        Assert.That(asked, Has.One.Contains("you had picked: "));

        // Real names, resolved as of this turn, not bare ids.
        Assert.That(asked, Has.None.Contains(CardNames.Unknown));
    }

    /// <summary>
    /// A turn the game asked nothing gets nothing, so <c>why</c> prints no heading over
    /// an empty section.
    /// </summary>
    /// <remarks>
    /// Turn one of game one is the fixture's own answer to this: it carries annotations
    /// but not one request message of any kind.
    /// </remarks>
    [Test]
    public void A_turn_that_was_asked_nothing_reports_nothing()
    {
        var dump = LogDump.ForTurn(Bo3Raw(), Cards(), turn: 1, game: 1);

        Assert.That(dump.Annotations, Is.Not.Empty);
        Assert.That(dump.Negotiations, Is.Empty);
    }

    /// <summary>
    /// The client re-sends a request every time the player reconsiders, and the repeats
    /// are interleaved rather than consecutive — declare, pay, dead end, cancel, declare
    /// again. A rule that only folded neighbours would have folded nothing at all on the
    /// turn that prompted issue #20, where 17 requests are 4 distinct ones.
    /// </summary>
    [Test]
    public void Identical_prompts_fold_and_count_even_when_something_came_between()
    {
        var asked = LogDump.ForTurn(Interleaved, Cards(), turn: 3, game: 1).Negotiations;

        Assert.That(asked.Where(l => l.StartsWith("declare attackers", StringComparison.Ordinal)),
            Is.EqualTo(new[] { "declare attackers  (asked 2 times)" }));
        Assert.That(asked, Has.One.EqualTo("groupReq"), "the one that came between");
    }

    /// <summary>
    /// A prompt that could be read out comes above one that could only be named. On
    /// almost every turn the names are the whole section and their position never
    /// arises; on the turns where it does, the answer should not be underneath them.
    /// </summary>
    [Test]
    public void A_prompt_that_could_be_read_comes_before_one_that_could_only_be_named()
    {
        var asked = LogDump.ForTurn(Interleaved, Cards(), turn: 3, game: 1).Negotiations;

        Assert.That(asked[0], Does.StartWith("declare attackers"));
        Assert.That(asked[^1], Is.EqualTo("groupReq"),
            "even though the log sent it in the middle");
    }

    /// <summary>
    /// One turn, three request messages: the same declaration twice with an unread
    /// request between them. Hand-built because the shape being tested is the order the
    /// log sends things in, which no archived match can be relied on to hold still.
    /// </summary>
    private static readonly string[] Interleaved =
    [
        """
        {"greToClientEvent":{"greToClientMessages":[{"gameStateMessage":
          {"gameInfo":{"gameNumber":1},"turnInfo":{"turnNumber":3}}}]}}
        """.ReplaceLineEndings(""),
        """
        {"greToClientEvent":{"greToClientMessages":[{"declareAttackersReq":
          {"qualifiedAttackers":[{"attackerInstanceId":7}],"canSubmitAttackers":true}}]}}
        """.ReplaceLineEndings(""),
        """
        {"greToClientEvent":{"greToClientMessages":[{"groupReq":{"groups":[]}}]}}
        """.ReplaceLineEndings(""),
        """
        {"greToClientEvent":{"greToClientMessages":[{"declareAttackersReq":
          {"qualifiedAttackers":[{"attackerInstanceId":7}],"canSubmitAttackers":true}}]}}
        """.ReplaceLineEndings(""),
    ];
}
