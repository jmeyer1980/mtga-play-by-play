using System.Linq;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class NarratorTests
{
    private static Transcript T(params GameEvent[] events) => new(
        "m1", 0, 0, "Ladder",
        new PlayerInfo(1, "ME", "PlayerOne", "SteamWindows"),
        new PlayerInfo(2, "THEM", "PlayerTwo", "iPhone"),
        WinningTeamId: 1, GamesWon: 2, GamesLost: 0, Incomplete: false,
        events, new Dictionary<string, int>(), new HashSet<string>(),
        new Dictionary<string, int>(), [], []);

    private static GameEvent E(EventKind kind, int seq = 0) =>
        new() { Seq = seq, Kind = kind, Turn = 1, ActiveSeat = 1 };


    // ---------- Issue 51 follow-up: a granted ability that runs to a paragraph ----------

    private const string LongRule =
        "“{T}: Add {U}. Spend this mana only to cast a spell from anywhere other than your hand.”";

    private static string? Granted(string detail, Density density) =>
        Narrator.Narrate(T(E(EventKind.AbilityGained) with
        {
            CauseName = "Mm'menon, the Right Hand",
            TargetName = "Chromatic Lantern",
            Detail = detail
        }), density).Select(l => l.Text).FirstOrDefault(t => t.Contains("gives"));

    /// <summary>
    /// Verbose is the view that exists to hold everything the log said, so a granted
    /// ability keeps its whole rules text there however long it runs.
    /// </summary>
    [Test]
    public void Verbose_keeps_the_whole_of_a_granted_ability() =>
        Assert.That(Granted(LongRule, Density.Verbose), Does.Contain(
            "Spend this mana only to cast a spell from anywhere other than your hand"));

    /// <summary>
    /// The readable view does not. Measured over the archive: 438 grant lines carry a
    /// quoted rule, 99 of them run past 110 characters, and one ran to 212. The head of
    /// the rule is the part that says what the ability is, so the cut keeps it and lands
    /// on a word boundary rather than mid-word.
    /// </summary>
    [Test]
    public void Beats_keeps_the_head_of_a_long_granted_ability_and_says_it_stopped()
    {
        var line = Granted(LongRule, Density.Beats)!;

        Assert.That(line, Does.StartWith("Mm'menon, the Right Hand gives Chromatic Lantern “{T}: Add {U}."));
        Assert.That(line, Does.EndWith("…”"), "the reader is told the rule goes on");
        Assert.That(line, Does.Not.Contain("anywhere other than your hand"));
        Assert.That(line, Does.Not.Contain(" …"), "the cut lands on a word, not on a space");
    }

    /// <summary>
    /// More than half of them are short enough to read in place — the median quoted rule
    /// is 31 characters — and shortening those would cost information for nothing.
    /// </summary>
    [Test]
    public void Beats_leaves_a_short_granted_ability_whole() =>
        Assert.That(Granted("“{T}: Add one mana of any color.”", Density.Beats),
            Does.EndWith("“{T}: Add one mana of any color.”"));

    /// <summary>
    /// A keyword is not a rules paragraph and never was. It carries no sentence
    /// punctuation, which is how AbilityText.Clause tells the two apart in the first
    /// place, and it is already the shortest form of itself.
    /// </summary>
    [Test]
    public void Beats_leaves_a_granted_keyword_alone() =>
        Assert.That(Granted("first strike", Density.Beats), Does.EndWith("first strike"));


    /// <summary>
    /// Detail is not always one quoted rule. EventExtractor joins a permanent's granted
    /// clauses with AbilityText.Join, so it can be a keyword and a rule — "haste and
    /// “When this permanent…”" — or several rules in a row. The archive holds 16 lines
    /// with two or more quoted clauses and 58 where a keyword list ends in one.
    /// <para>
    /// The first cut at this treated Detail as a single quoted rule and stripped its
    /// outer characters, which left four lines in the archive with an opening quote, a
    /// closing quote and then a stray second closing quote after the ellipsis. It also
    /// skipped every list that began with a keyword, so 20 long lines stayed long.
    /// </para>
    /// </summary>
    [TestCase("haste and “When this permanent is put into a graveyard from the battlefield, draw a card.”",
        Description = "a keyword and a rule")]
    [TestCase("“You may look at the top card of your library any time.” and “Whenever this creature attacks, surveil 2.”",
        Description = "two rules")]
    [TestCase("“Each player discards a card.”, “Target player sacrifices a creature.” and “Draw a card.”",
        Description = "three rules")]
    public void Beats_leaves_a_shortened_clause_list_properly_quoted(string detail)
    {
        var line = Granted(detail, Density.Beats)!;

        Assert.That(line.Count(c => c == '“'), Is.EqualTo(line.Count(c => c == '”')),
            $"quotes are unbalanced: {line}");
        Assert.That(line, Does.Contain("…"), "a shortened list says that it stopped");
        Assert.That(line, Does.Not.Contain("and…"), "no dangling conjunction before the ellipsis");
        Assert.That(line, Does.Not.Contain(",…"), "no dangling comma before the ellipsis");
    }

    /// <summary>
    /// A list short enough to read is left whole, quotes and conjunction and all.
    /// </summary>
    [Test]
    public void Beats_leaves_a_short_clause_list_whole() =>
        Assert.That(Granted("flying and “Draw a card.”", Density.Beats),
            Does.EndWith("flying and “Draw a card.”"));

    [Test]
    public void Beats_omits_phase_changes_mana_and_unknown()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.PhaseChange, 0),
            E(EventKind.ManaPaid, 1),
            E(EventKind.Unknown, 2),
            E(EventKind.LandPlayed, 3) with { SourceName = "Plains", ActorSeat = 1 }
        ), Density.Beats);

        Assert.That(lines.Any(l => l.Text.Contains("Plains")), Is.True);
        Assert.That(lines.Any(l => l.Text.Contains("phase", StringComparison.OrdinalIgnoreCase)),
            Is.False);
    }

    [Test]
    public void Verbose_includes_what_beats_omits()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.ManaPaid, 0) with { SourceName = "Island" },
            E(EventKind.Unknown, 1) with { RawType = "AnnotationType_Whatever" }
        ), Density.Verbose);

        Assert.That(lines, Is.Not.Empty);
        Assert.That(lines.Any(l => l.Text.Contains("AnnotationType_Whatever")), Is.True);
    }

    [Test]
    public void Turn_start_produces_a_turn_header_naming_the_active_player()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.TurnStart) with { Turn = 4, ActorSeat = 2 }), Density.Beats);

        var header = lines.Single(l => l.IsTurnHeader);
        Assert.That(header.Text, Does.Contain("Turn 4"));
        Assert.That(header.Text, Does.Contain("Opponent"));
    }

    [Test]
    public void Damage_to_a_player_reads_as_a_sentence()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Damage) with { SourceName = "Monastery Swiftspear", TargetSeat = 1, Amount = 2 }
        ), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Monastery Swiftspear deals 2 damage to You"));
    }

    [Test]
    public void Damage_to_a_permanent_names_the_permanent()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Damage) with
            {
                SourceName = "Lightning Bolt",
                TargetInstanceId = 99,
                TargetName = "Llanowar Elves",
                Amount = 3
            }), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Lightning Bolt deals 3 damage to Llanowar Elves"));
    }

    [Test]
    public void Life_change_shows_direction_and_owner()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.LifeChanged) with { TargetSeat = 2, Amount = -3 }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("Opponent loses 3 life"));

        lines = Narrator.Narrate(T(
            E(EventKind.LifeChanged) with { TargetSeat = 1, Amount = 4 }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("You gain 4 life"));
    }

    [Test]
    public void Spell_cast_and_land_played_read_naturally()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.SpellCast, 0) with { SourceName = "Counterspell", ActorSeat = 1 },
            E(EventKind.LandPlayed, 1) with { SourceName = "Island", ActorSeat = 2 }
        ), Density.Beats);

        Assert.That(lines[0].Text, Is.EqualTo("You cast Counterspell"));
        Assert.That(lines[1].Text, Is.EqualTo("Opponent plays Island"));
    }

    [Test]
    public void Game_end_states_the_match_outcome()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.GameEnd) with { Detail = "You win the match" }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("You win the match"));
    }

    [Test]
    public void Attack_names_the_creature_and_who_it_is_swinging_at()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Attack) with
            { SourceName = "Gloryheath Lynx", ActorSeat = 2, TargetSeat = 1 }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Opponent attacks with Gloryheath Lynx"));
    }

    [Test]
    public void Attack_on_a_planeswalker_names_the_permanent_attacked()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Attack) with
            { SourceName = "Gloryheath Lynx", ActorSeat = 2, TargetName = "Teferi" }), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Opponent attacks Teferi with Gloryheath Lynx"));
    }

    [Test]
    public void Block_reads_as_blocker_blocks_attacker()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Block) with
            { SourceName = "Toy", ActorSeat = 1, TargetName = "Gloryheath Lynx" }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Toy blocks Gloryheath Lynx"));
    }

    [Test]
    public void Beats_drop_events_whose_subject_is_only_a_bare_instance_id()
    {
        var ev = E(EventKind.StateBasedAction) with { SourceName = "#332" };

        Assert.That(Narrator.Narrate(T(ev), Density.Beats), Is.Empty);
        Assert.That(Narrator.Narrate(T(ev), Density.Verbose), Is.Not.Empty,
            "verbose keeps them so the gap stays visible");
    }

    /// <summary>
    /// A dead commander really is put into the graveyard, and then a second
    /// state-based action carries it home. Phrasing that trip in the graveyard's
    /// words rendered "is put into the graveyard ×2" for one Elspeth (#18) — two
    /// true zone changes folded into one impossible line.
    /// </summary>
    [Test]
    public void A_state_based_action_bound_for_the_command_zone_says_so()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.StateBasedAction, 0) with
            { SourceName = "Elspeth, Storm Slayer", FromZone = "ZoneType_Battlefield", ToZone = "ZoneType_Graveyard" },
            E(EventKind.StateBasedAction, 1) with
            { SourceName = "Elspeth, Storm Slayer", FromZone = "ZoneType_Graveyard", ToZone = "ZoneType_Command" }), Density.Beats);

        Assert.That(lines.Select(l => l.Text), Is.EqualTo(new[]
        {
            "Elspeth, Storm Slayer is put into the graveyard",
            "Elspeth, Storm Slayer returns to the command zone",
        }));
    }

    /// <summary>
    /// Every other state-based action keeps today's words — including one whose
    /// destination the log never recorded, because the graveyard is where every SBA
    /// this was measured against sends its card, except the commander's ride home.
    /// </summary>
    [Test]
    public void A_state_based_action_with_no_recorded_destination_keeps_the_graveyard_wording()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.StateBasedAction) with { SourceName = "Hare Apparent" }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Hare Apparent is put into the graveyard"));
    }

    [Test]
    public void Turn_header_shows_both_life_totals_you_first()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.TurnStart) with
            { Turn = 6, ActorSeat = 2, LifeSeat1 = 18, LifeSeat2 = 12 }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Turn 6 — Opponent  (You 18 · Opponent 12)"));
    }

    [Test]
    public void Turn_header_omits_the_score_before_any_life_is_known()
    {
        var lines = Narrator.Narrate(T(E(EventKind.TurnStart) with { Turn = 1, ActorSeat = 1 }),
            Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("Turn 1 — You"));
    }

    [Test]
    public void Board_snapshot_is_flagged_so_renderers_can_set_it_apart()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.BoardSnapshot) with
            { ActorSeat = 2, Detail = "Ajani's Pridemate 4/4 (1 dmg), Rabbit 1/1 (tapped)" }),
            Density.Beats);

        var line = lines.Single();
        Assert.That(line.IsBoard, Is.True);
        Assert.That(line.Text,
            Is.EqualTo("Opponent controls: Ajani's Pridemate 4/4 (1 dmg), Rabbit 1/1 (tapped)"));
    }

    [Test]
    public void Board_snapshot_for_you_reads_in_second_person()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.BoardSnapshot) with { ActorSeat = 1, Detail = "Knight 2/2" }),
            Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("You control: Knight 2/2"));
    }

    /// <summary>
    /// A caveat about the whole line is said once, before the colon, so that it plainly
    /// covers everything after it — rather than once per creature (#203).
    /// </summary>
    [Test]
    public void Board_snapshot_says_a_caveat_on_the_whole_line_once_before_the_colon()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.BoardSnapshot) with
            {
                ActorSeat = 2,
                Detail = "Forest 2/2 (tapped), Squirrel 1/1 (tapped)",
                Caveat = "last reported before the gap"
            }),
            Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo(
            "Opponent controls (last reported before the gap): Forest 2/2 (tapped), Squirrel 1/1 (tapped)"));
    }

    /// <summary>
    /// A blank caveat is no caveat, so the line must not print empty parentheses. The
    /// extractor never sets one — its only assignment is a constant or null — so this
    /// guards the narrator against a future caller rather than recording an observed case.
    /// </summary>
    [Test]
    public void Board_snapshot_with_a_blank_caveat_reads_as_if_there_were_none()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.BoardSnapshot) with { ActorSeat = 1, Detail = "Knight 2/2", Caveat = " " }),
            Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("You control: Knight 2/2"));
    }

    [Test]
    public void Effects_name_what_caused_them_when_the_log_says_so()
    {
        (EventKind Kind, string Expected)[] cases =
        [
            (EventKind.Destroyed, "Split Up destroys Hare Apparent"),
            (EventKind.Exiled,    "Split Up exiles Hare Apparent"),
            (EventKind.Returned,  "Split Up returns Hare Apparent"),
            (EventKind.Milled,    "Split Up mills Hare Apparent"),
            (EventKind.Countered, "Split Up counters Hare Apparent"),
        ];

        foreach (var (kind, expected) in cases)
        {
            var lines = Narrator.Narrate(T(
                E(kind) with { SourceName = "Hare Apparent", CauseName = "Split Up" }),
                Density.Beats);
            Assert.That(lines.Single().Text, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Effects_fall_back_to_the_passive_form_without_a_cause()
    {
        (EventKind Kind, string Expected)[] cases =
        [
            (EventKind.Destroyed, "Hare Apparent is destroyed"),
            (EventKind.Exiled,    "Hare Apparent is exiled"),
            (EventKind.Returned,  "Hare Apparent returns"),
            (EventKind.Countered, "Hare Apparent is countered"),
        ];

        foreach (var (kind, expected) in cases)
        {
            var lines = Narrator.Narrate(
                T(E(kind) with { SourceName = "Hare Apparent" }), Density.Beats);
            Assert.That(lines.Single().Text, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Counters_are_named_when_the_card_database_knows_the_kind()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.CounterChanged) with
            { TargetName = "Ajani's Pridemate", Amount = 1, Detail = "+1/+1" }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("Ajani's Pridemate gets 1 +1/+1 counter"));

        lines = Narrator.Narrate(T(
            E(EventKind.CounterChanged) with
            { TargetName = "Kaito", Amount = 3, Detail = "Loyalty" }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("Kaito gets 3 Loyalty counters"));
    }

    [Test]
    public void Counters_fall_back_to_the_bare_word_when_the_kind_is_unknown()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.CounterChanged) with { TargetName = "Thing", Amount = 1 }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("Thing gets 1 counter"));
    }

    [Test]
    public void Scry_says_where_the_cards_went()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Scry) with
            { ActorSeat = 1, Amount = 1, Detail = "Overlord of the Mistmoors to the bottom" }),
            Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("You scry 1, putting Overlord of the Mistmoors to the bottom"));
    }

    [Test]
    public void Scry_without_detail_stays_vague_rather_than_inventing_it()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.Scry) with { ActorSeat = 2 }), Density.Beats);
        Assert.That(lines.Single().Text, Is.EqualTo("Opponent scries"));
    }

    /// <summary>
    /// Passive on purpose: the same event covers an aura landing on a creature and a
    /// player equipping a sword, and only one of those has an actor the log names.
    /// </summary>
    [Test]
    public void An_attachment_says_what_it_went_onto()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Attached) with
            {
                SourceInstanceId = 500,
                SourceName = "Buster Sword",
                TargetInstanceId = 600,
                TargetName = "Veteran Ice Climber 4/5"
            }), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Buster Sword is attached to Veteran Ice Climber 4/5"));
    }

    /// <summary>
    /// Arena's own wording, taken from the card: "When this Class becomes level 2, …".
    /// </summary>
    [Test]
    public void A_class_level_reads_the_way_the_card_says_it()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.LevelUp) with
            { SourceInstanceId = 700, SourceName = "Caretaker's Talent", Amount = 3 }),
            Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Caretaker's Talent becomes level 3"));
    }

    /// <summary>
    /// Level zero is not a level a Class can be at, so an event carrying one is a
    /// reading that went wrong rather than something to announce.
    /// </summary>
    [Test]
    public void A_class_with_no_level_produces_no_line()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.LevelUp) with { SourceName = "Caretaker's Talent" }), Density.Beats);
        Assert.That(lines, Is.Empty);
    }

    /// <summary>
    /// Issue #5's missing line, in the cause-first shape the destroy lines use. This is
    /// what connects "Enter the Avatar State resolves" to the first-strike damage two
    /// lines later.
    /// </summary>
    [Test]
    public void A_granted_ability_names_granter_creature_and_ability()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.AbilityGained) with
            {
                CauseInstanceId = 431,
                CauseName = "Enter the Avatar State",
                TargetInstanceId = 405,
                TargetName = "Llanowar Elves 2/2",
                Detail = "flying, first strike, lifelink and hexproof"
            }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo(
            "Enter the Avatar State gives Llanowar Elves 2/2 " +
            "flying, first strike, lifelink and hexproof"));
    }

    /// <summary>
    /// A grant whose granter the log never named still happened to somebody. The line
    /// falls back to the creature's own act of gaining rather than inventing a cause.
    /// </summary>
    [Test]
    public void A_grant_with_no_named_granter_still_reads()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.AbilityGained) with
            {
                TargetInstanceId = 405,
                TargetName = "Toy 11/11",
                Detail = "lifelink"
            }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Toy 11/11 gains lifelink"));
    }

    /// <summary>
    /// Issue #22's missing line, in the same cause-first shape. "Temporary" is the whole
    /// of what the log supports about the duration: the annotation's code is in no table
    /// Arena ships, and the two codes in the archive do not mean the same length, so
    /// "until end of turn" would be a length nobody measured.
    /// </summary>
    [Test]
    public void A_copy_names_the_permanent_the_card_and_who_did_it()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Copied) with
            {
                SourceInstanceId = 755,
                SourceName = "Lembas",
                TargetName = "Iron Man, Futurist Paragon",
                CauseInstanceId = 712,
                CauseName = "Shuri, Wakandan Inventor",
                Detail = EventExtractor.TemporaryCopy
            }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo(
            "Shuri, Wakandan Inventor makes Lembas a temporary copy of " +
            "Iron Man, Futurist Paragon"));
    }

    [Test]
    public void A_permanent_that_copies_something_itself_needs_no_cause()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Copied) with
            {
                SourceInstanceId = 440,
                SourceName = "Oko, the Ringleader",
                TargetName = "Hare Apparent",
                Detail = EventExtractor.TemporaryCopy
            }), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Oko, the Ringleader becomes a temporary copy of Hare Apparent"));
    }

    /// <summary>
    /// A clone that arrived copying something never changed — "becomes" would send the
    /// reader back looking for a moment that did not happen.
    /// </summary>
    [Test]
    public void A_clone_enters_as_a_copy_rather_than_becoming_one()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Copied) with
            {
                SourceInstanceId = 474,
                SourceName = "Spark Double",
                TargetName = "Volo, Guide to Monsters",
                Detail = EventExtractor.PermanentCopy
            }), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Spark Double enters as a copy of Volo, Guide to Monsters"));
    }

    /// <summary>
    /// Issue #7's missing line: the grant's other end. "Loses", the verb counters and
    /// life already use, and no cause — a wear-off has no actor, and the grant line
    /// already said who put the ability there.
    /// </summary>
    [Test]
    public void An_expired_ability_reads_as_losing_it()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.AbilityExpired) with
            {
                TargetInstanceId = 405,
                TargetName = "Battlesong Berserker 4/4",
                Detail = "menace"
            }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Battlesong Berserker 4/4 loses menace"));
    }

    /// <summary>
    /// Cause first and active, the same shape the destroy and exile lines already use.
    /// Without it three triggers in a row read identically and a reader cannot tell what
    /// the player did to set any of them off.
    /// </summary>
    [Test]
    public void A_trigger_with_a_known_cause_puts_the_cause_first()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Triggered) with
            {
                SourceName = "Caretaker's Talent's ability",
                CauseName = "Hare Apparent"
            }), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Hare Apparent triggers Caretaker's Talent's ability"));
    }

    /// <summary>
    /// Two thirds of triggered abilities have no triggering object at all — nothing
    /// caused them but the turn advancing — so the causeless line has to keep working.
    /// </summary>
    [Test]
    public void A_trigger_with_no_known_cause_keeps_the_plain_line()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Triggered) with { SourceName = "Deal Gone Bad" }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Deal Gone Bad triggers"));
    }

    /// <summary>
    /// Issue #3's proposed wording, verbatim: the player and the permanent, active
    /// voice, because activating is something a player chose to do — unlike a trigger,
    /// which the game did to them.
    /// </summary>
    [Test]
    public void An_activation_names_the_player_and_the_permanent()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Activated) with { SourceName = "Lander", ActorSeat = 2 }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Opponent activates Lander"));
    }

    [Test]
    public void Your_own_activation_conjugates_for_you()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Activated) with { SourceName = "Abandoned Air Temple", ActorSeat = 1 }),
            Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("You activate Abandoned Air Temple"));
    }

    /// <summary>
    /// Naming the cause is what stops repeated triggers folding into one another when
    /// they were in fact set off by different permanents — the collapse is by rendered
    /// text, so a line that says more collapses less.
    /// </summary>
    [Test]
    public void Triggers_with_different_causes_no_longer_collapse_together()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Triggered, 0) with
            { SourceName = "Caretaker's Talent's ability", CauseName = "Hare Apparent" },
            E(EventKind.Triggered, 1) with
            { SourceName = "Caretaker's Talent's ability", CauseName = "Toy" }
        ), Density.Beats);

        Assert.That(lines.Select(l => l.Text), Is.EqualTo(new[]
        {
            "Hare Apparent triggers Caretaker's Talent's ability",
            "Toy triggers Caretaker's Talent's ability"
        }));
    }

    [Test]
    public void Repeated_lines_collapse_with_a_count()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Triggered, 0) with { SourceName = "Ghostly Dancers's ability" },
            E(EventKind.Triggered, 1) with { SourceName = "Ghostly Dancers's ability" },
            E(EventKind.Triggered, 2) with { SourceName = "Ghostly Dancers's ability" },
            E(EventKind.LandPlayed, 3) with { SourceName = "Plains", ActorSeat = 1 }
        ), Density.Beats);

        Assert.That(lines.Select(l => l.Text), Is.EqualTo(new[]
        {
            "Ghostly Dancers's ability triggers ×3",
            "You play Plains"
        }));
    }

    [Test]
    public void Turn_headers_are_never_collapsed_into_each_other()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.TurnStart, 0) with { Turn = 3, ActorSeat = 1 },
            E(EventKind.TurnStart, 1) with { Turn = 3, ActorSeat = 1 }
        ), Density.Beats);

        Assert.That(lines, Has.Count.EqualTo(2), "two turn headers must stay two lines");
    }

    [Test]
    public void Narrate_drops_events_it_cannot_phrase_rather_than_emitting_blanks()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.ZoneMove) with { SourceName = null }), Density.Beats);
        Assert.That(lines.Any(l => string.IsNullOrWhiteSpace(l.Text)), Is.False);
    }

    /// <summary>
    /// A return says where the card went, because "returns" alone covers two outcomes
    /// that are close to opposites.
    /// </summary>
    /// <remarks>
    /// This read "to hand" unconditionally. Across the archive a Return goes to hand 61
    /// times and to the battlefield 47, so nearly half of them named a zone the card did
    /// not go to — a flicker effect reported as a bounce, which reverses who came out
    /// ahead on the exchange.
    /// </remarks>
    [TestCase("ZoneType_Hand", "Split Up returns Hare Apparent to hand")]
    [TestCase("ZoneType_Battlefield", "Split Up returns Hare Apparent to the battlefield")]
    [TestCase("ZoneType_Library", "Split Up returns Hare Apparent to the library")]
    public void A_return_names_the_zone_the_card_went_to(string zone, string expected)
    {
        var lines = Narrator.Narrate(T(E(EventKind.Returned) with
        {
            SourceName = "Hare Apparent",
            CauseName = "Split Up",
            ToZone = zone
        }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo(expected));
    }

    [Test]
    public void A_return_with_no_recorded_zone_claims_none()
    {
        // Naming the commoner of two outcomes is guessing, and the guess would be wrong
        // more than four times in ten.
        var lines = Narrator.Narrate(
            T(E(EventKind.Returned) with { SourceName = "Hare Apparent" }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Hare Apparent returns"));
    }

    /// <summary>
    /// A statline change with no counter behind it — a pump, a shrink, or a doubling.
    /// </summary>
    /// <remarks>
    /// These were invisible until a Tifa Lockhart match made it obvious: her landfall
    /// ability doubles her power, and across one turn she went 1/2 to 24/4 with the
    /// transcript reporting none of it. The permanent is named at the size it was
    /// changed from, so one line carries both ends of the change.
    /// <para>
    /// No duration is claimed. The annotation carries two deltas and nothing else, and
    /// the same one covers a landfall pump that expires at end of turn and an aura that
    /// lasts while attached — "until end of turn" would be right about Tifa and wrong
    /// about Royal Treatment.
    /// </para>
    /// </remarks>
    [TestCase(1, 0, "+1/+0")]
    [TestCase(12, 0, "+12/+0")]
    [TestCase(-3, -3, "-3/-3")]
    [TestCase(2, 2, "+2/+2")]
    public void A_statline_change_says_the_size_it_started_from_and_the_delta(
        int power, int toughness, string expected)
    {
        var lines = Narrator.Narrate(T(E(EventKind.StatsModified) with
        {
            TargetName = "Tifa Lockhart 3/4",
            Amount = power,
            Detail = $"{power:+#;-#;+0}/{toughness:+#;-#;+0}"
        }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo($"Tifa Lockhart 3/4 gets {expected}"));
    }

    /// <summary>
    /// A temporary effect ending. Arena announces the pump and never announces its
    /// expiry — there is no PowerToughnessModDeleted — so the only evidence is the
    /// statline moving back while nothing in the message names the permanent.
    /// </summary>
    /// <remarks>
    /// Named at the size it is losing, so the line carries both ends: "Tifa Lockhart 2/2
    /// returns to 1/2". Naming it at the size it has now produced "Rabbit 2/2 returns to
    /// 2/2", which says nothing twice.
    /// </remarks>
    [Test]
    public void An_effect_wearing_off_names_the_size_it_is_losing()
    {
        var lines = Narrator.Narrate(T(E(EventKind.StatsExpired) with
        {
            TargetName = "Tifa Lockhart 2/2",
            Detail = "2/2 → 1/2"
        }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Tifa Lockhart 2/2 returns to 1/2"));
    }

    /// <summary>
    /// A Room's door opening, named by the half that opened.
    /// </summary>
    /// <remarks>
    /// Designations were dropped as a candidate because DesignationType is a bare int
    /// with no entry in the card database's enum table. That is still true, and it turned
    /// out not to matter: every one of the archive's 201 type-19 and type-20 designations
    /// lands on a card whose name holds both halves, so the half can be named from the
    /// name. 19 is the first door and 20 the second.
    /// <para>
    /// No cause is named. affectorId is populated on 54 of them and is not the unlocker —
    /// across the archive it points once at a Plains and once at Hare Apparent's ability,
    /// neither of which can open a door, so no shape of affector can be trusted.
    /// </para>
    /// </remarks>
    [Test]
    public void A_room_door_is_named_by_the_half_that_opened()
    {
        var lines = Narrator.Narrate(T(E(EventKind.DoorUnlocked) with
        {
            ActorSeat = 1,
            SourceName = "Porcelain Gallery"
        }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("You unlock Porcelain Gallery"));
        Assert.That(lines.Single().Text, Does.Not.Contain("//"),
            "naming the whole card would name the side that was already open too");
    }

    [Test]
    public void The_opponents_door_reads_in_the_third_person()
    {
        var lines = Narrator.Narrate(T(E(EventKind.DoorUnlocked) with
        {
            ActorSeat = 2,
            SourceName = "Dollmaker's Shop"
        }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Opponent unlocks Dollmaker's Shop"));
    }

    /// <summary>
    /// A statline that moves both ways at once is not an effect ending.
    /// </summary>
    /// <remarks>
    /// Porcelain Gallery makes every creature as big as the number of creatures you
    /// control, so a printed 2/5 Ghostly Dancers becomes 4/4 — toughness down, power up.
    /// The expiry rule only skipped changes where both numbers grew, so this reported
    /// "Ghostly Dancers returns to 4/4" about a creature that had just got bigger.
    /// A characteristic-defining ability setting the numbers is not a buff falling off.
    /// </remarks>
    [Test]
    public void A_statline_moving_both_ways_is_not_reported_as_an_expiry()
    {
        // The narrator renders whatever it is given; this pins the wording the extractor
        // produces so the shape of the claim stays a reduction.
        var lines = Narrator.Narrate(T(E(EventKind.StatsExpired) with
        {
            TargetName = "Hare Apparent 6/6",
            Detail = "6/6 → 2/2"
        }), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("Hare Apparent 6/6 returns to 2/2"));

        var parts = "6/6 → 2/2".Split('→');
        var before = parts[0].Trim().Split('/').Select(int.Parse).ToArray();
        var after = parts[1].Trim().Split('/').Select(int.Parse).ToArray();
        Assert.That(after[0], Is.LessThanOrEqualTo(before[0]));
        Assert.That(after[1], Is.LessThanOrEqualTo(before[1]));
    }

    // ---------- zone transfers with no verb of their own ----------

    private static string? Moved(string? from, string? to, string? category) =>
        Narrator.Narrate(T(E(EventKind.ZoneMove) with
        {
            SourceName = "Plains",
            FromZone = from,
            ToZone = to,
            Detail = category
        }), Density.Verbose).SingleOrDefault()?.Text;

    /// <summary>
    /// These read as where the card ended up, because the category does not say. "Put"
    /// covers a fetchland finding a Forest, a mulligan bottoming a card and a tutor
    /// putting one in hand — the destination is the only part that separates them.
    /// </summary>
    [TestCase("ZoneType_Library", "ZoneType_Battlefield", "Plains is put onto the battlefield")]
    [TestCase("ZoneType_Library", "ZoneType_Hand", "Plains is put into hand")]
    [TestCase("ZoneType_Hand", "ZoneType_Library", "Plains is put into the library")]
    [TestCase("ZoneType_Library", "ZoneType_Graveyard", "Plains is put into the graveyard")]
    [TestCase("ZoneType_Battlefield", "ZoneType_Exile", "Plains is exiled")]
    public void A_move_with_no_verb_of_its_own_says_where_the_card_went(
        string from, string to, string expected) =>
        Assert.That(Moved(from, to, "Put"), Is.EqualTo(expected));

    [Test]
    public void A_category_that_names_a_mechanic_survives_the_rephrasing()
    {
        // Warp exiles a creature that is coming back. Flattening it into a plain exile
        // would drop the only word that says so, and a mechanic printed in some future
        // set should surface rather than disappear.
        Assert.That(Moved("ZoneType_Battlefield", "ZoneType_Exile", "Warp"),
            Is.EqualTo("Plains is exiled (Warp)"));

        // "Put" and "nil" are the engine saying only that something moved, or nothing
        // at all. Neither belongs in a sentence that already names the destination.
        Assert.That(Moved("ZoneType_Stack", "ZoneType_Graveyard", "nil"),
            Is.EqualTo("Plains is put into the graveyard"));
    }

    [Test]
    public void A_move_that_begins_and_ends_in_one_zone_says_nothing()
    {
        // A shuffle or a reorder. The card did not go anywhere a reader can see.
        Assert.That(Moved("ZoneType_Library", "ZoneType_Library", "Put"), Is.Null);
    }

    [Test]
    public void A_move_through_a_zone_the_log_never_described_still_reports_something()
    {
        // Worse to read than the phrased form and still true, which beats staying silent
        // about a card that moved. The category is dropped here for the same reason it is
        // dropped everywhere else — these two paths used to be the one place it was not
        // consulted, which is how a single "moves (Put)" survived into the archive (#73).
        Assert.That(Moved("ZoneType_Library", null, "Put"), Is.EqualTo("Plains moves"));
        Assert.That(Moved(null, "ZoneType_Sideboard", "Put"),
            Is.EqualTo("Plains moves"), "and the same for a zone with no phrasing");
    }

    /// <summary>
    /// A category that names a mechanic is kept on every path, including the two that
    /// have no destination to name.
    /// </summary>
    [Test]
    public void A_mechanic_survives_a_move_through_a_zone_the_log_never_described() =>
        Assert.That(Moved("ZoneType_Battlefield", null, "Warp"),
            Is.EqualTo("Plains moves (Warp)"));

    /// <summary>
    /// Bookkeeping the engine keeps for itself. "Separate" is it splitting a revealed
    /// pile, and is the largest single parenthetical in the archive at 39 lines;
    /// "DestroyNoRegenerate" says a permission nobody exercised was withheld. Neither
    /// adds to a sentence that already names where the card went (#73).
    /// </summary>
    [TestCase("Separate")]
    [TestCase("DestroyNoRegenerate")]
    public void A_category_that_is_only_bookkeeping_does_not_reach_the_reader(string category)
    {
        Assert.That(Moved("ZoneType_Library", "ZoneType_Hand", category),
            Is.EqualTo("Plains is put into hand"));
        Assert.That(Moved("ZoneType_Library", null, category), Is.EqualTo("Plains moves"));
    }

    // ---------- Issue 41: hidden for a name the line never prints ----------

    /// <summary>
    /// A shockland's payment reaches the log as a life change attributed to the ability
    /// object rather than to the land, so its source is a placeholder. The readable view
    /// dropped it, while the turn headers — read from board state — went on reporting
    /// the true total. 37 of 529 rendered pages carried a life total that moved with
    /// nothing beneath it to say why.
    /// </summary>
    /// <remarks>
    /// The decisive fact is that <c>LifeChanged</c>'s wording is seat, verb and amount:
    /// the source it was being hidden for is one the line was never going to print.
    /// </remarks>
    [Test]
    public void A_life_change_from_an_unnamed_source_is_still_a_beat()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.LifeChanged, 0) with
            {
                SourceName = CardNames.Unknown,
                TargetSeat = 2,
                Amount = -2
            }
        ), Density.Beats);

        Assert.That(lines.Select(l => l.Text), Has.One.EqualTo("Opponent loses 2 life"));
        Assert.That(lines, Has.None.Matches<Line>(l => l.Text.Contains(CardNames.Unknown)),
            "and it says nothing about the source it could not name");
    }

    /// <summary>
    /// The audit the issue asked for found a second kind with the same defect: a counter
    /// line names the permanent and the counter, never the source. 16 lines across the
    /// archive.
    /// </summary>
    [Test]
    public void A_counter_change_from_an_unnamed_source_is_still_a_beat()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.CounterChanged, 0) with
            {
                SourceName = CardNames.Unknown,
                TargetName = "Unstoppable Slasher",
                Amount = -1,
                Detail = "Stun"
            }
        ), Density.Beats);

        Assert.That(lines, Has.One.Matches<Line>(l => l.Text.Contains("Unstoppable Slasher")));
        Assert.That(lines, Has.None.Matches<Line>(l => l.Text.Contains(CardNames.Unknown)));
    }

    /// <summary>
    /// The rule itself is not repealed. A line that really would say "Unknown card"
    /// still stays out of the readable view, which is the whole reason the rule exists.
    /// </summary>
    /// <remarks>
    /// <c>Damage</c> is the case that proves it is the LINE being tested and not a list
    /// of exempt kinds: it substitutes "Something" only for a <em>null</em> name, so a
    /// placeholder reaches its text and is found there.
    /// </remarks>
    [Test]
    public void A_line_that_would_name_an_unknown_card_is_still_kept_out_of_beats()
    {
        GameEvent[] events =
        [
            E(EventKind.Damage, 0) with
            {
                SourceName = CardNames.Unknown, TargetSeat = 2, Amount = 3
            }
        ];

        Assert.That(Narrator.Narrate(T(events), Density.Beats),
            Has.None.Matches<Line>(l => l.Text.Contains("damage")));

        // And verbose still keeps it, so the gap stays visible when debugging.
        Assert.That(Narrator.Narrate(T(events), Density.Verbose),
            Has.One.Matches<Line>(l => l.Text.Contains(CardNames.Unknown) &&
                                       l.Text.Contains("3 damage")));
    }

    /// <summary>
    /// Verbose said the life line before this change and must not say it twice after.
    /// </summary>
    [Test]
    public void The_verbose_view_still_says_it_exactly_once()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.LifeChanged, 0) with
            {
                SourceName = CardNames.Unknown,
                TargetSeat = 2,
                Amount = -2
            }
        ), Density.Verbose);

        Assert.That(lines.Count(l => l.Text == "Opponent loses 2 life"), Is.EqualTo(1));
    }

    /// <summary>
    /// A draw of a card nobody can name stays out of beats. It used to fall out of the
    /// unnamed-subject rule by accident; reading the finished line instead would have
    /// let it back in, because "Opponent draws a card" names nobody.
    /// </summary>
    /// <remarks>
    /// 3755 lines across the archive, one per opponent turn in 515 of 529 transcripts,
    /// each saying only that the draw step happened. A draw that names its card is a
    /// different thing and stays.
    /// </remarks>
    [Test]
    public void A_draw_nobody_can_name_stays_out_of_beats_but_a_named_one_stays_in()
    {
        var lines = Narrator.Narrate(T(
            E(EventKind.Drew, 0) with { SourceName = CardNames.Unknown, ActorSeat = 2 },
            E(EventKind.Drew, 1) with { SourceName = "Hop to It", ActorSeat = 1 }
        ), Density.Beats);

        Assert.That(lines, Has.None.Matches<Line>(l => l.Text.Contains("draws a card")));
        Assert.That(lines, Has.One.Matches<Line>(l => l.Text.Contains("Hop to It")));
    }

    // ---------- a run marker over a crowd ----------

    /// <summary>
    /// Reported from a real transcript: "Squirrel gets 1 +1/+1 counter ×24" read as one
    /// Squirrel standing up as a 25/25. The log has 45 objects taking exactly one counter
    /// apiece, and the attack lines directly below said "Squirrel 2/2 ×21" in the same
    /// breath. Collapsing is still right — twenty-four identical lines would be worse —
    /// but the marker has to say which of the two things happened.
    /// </summary>
    [Test]
    public void Counters_landing_on_different_permanents_say_one_each()
    {
        var lines = Narrator.Narrate(T(
            Enumerable.Range(0, 24).Select(i =>
                E(EventKind.CounterChanged, i) with
                {
                    TargetInstanceId = 100 + i,
                    TargetName = "Squirrel",
                    Amount = 1,
                    Detail = "+1/+1"
                }).ToArray()), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("24× Squirrel gets 1 +1/+1 counter"));
    }

    /// <summary>
    /// The case the plain marker was written for, and it must not change: one permanent
    /// taking counters over and over really does add up.
    /// </summary>
    [Test]
    public void Counters_landing_on_one_permanent_keep_the_plain_marker()
    {
        var lines = Narrator.Narrate(T(
            Enumerable.Range(0, 4).Select(i =>
                E(EventKind.CounterChanged, i) with
                {
                    TargetInstanceId = 100,
                    TargetName = "Mossborn Hydra",
                    Amount = 1,
                    Detail = "+1/+1"
                }).ToArray()), Density.Beats);

        Assert.That(lines.Single().Text,
            Is.EqualTo("Mossborn Hydra gets 1 +1/+1 counter ×4"));
    }

    /// <summary>
    /// Attacking does not accumulate, so five Rabbits are five Rabbits to any reader and
    /// a note saying so is clutter on a line nobody misread.
    /// </summary>
    [Test]
    public void A_run_of_attacks_by_different_creatures_is_left_alone()
    {
        var lines = Narrator.Narrate(T(
            Enumerable.Range(0, 5).Select(i =>
                E(EventKind.Attack, i) with
                {
                    SourceInstanceId = 200 + i,
                    SourceName = "Rabbit",
                    ActorSeat = 1
                }).ToArray()), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("You attack with Rabbit ×5"));
    }

    /// <summary>
    /// A statline change adds up the same way a counter does.
    /// </summary>
    /// <remarks>
    /// The detail is the modifier alone — the renderer supplies the verb. This test
    /// first passed "gets +2/+1" and so built "Soldier 1/1 gets gets +2/+1", which a
    /// prefix-only assertion was perfectly happy to accept. Asserting the whole line is
    /// what makes that impossible to miss again.
    /// </remarks>
    [Test]
    public void A_pump_on_different_creatures_counts_the_creatures()
    {
        var lines = Narrator.Narrate(T(
            Enumerable.Range(0, 3).Select(i =>
                E(EventKind.StatsModified, i) with
                {
                    TargetInstanceId = 300 + i,
                    TargetName = "Soldier 1/1",
                    Detail = "+2/+1"
                }).ToArray()), Density.Beats);

        Assert.That(lines.Single().Text, Is.EqualTo("3× Soldier 1/1 gets +2/+1"));
    }

    // ---------- Issue 179: the per-phase mana ledger ----------

    private static System.Collections.Generic.List<string> Ledger(bool on) =>
        Narrator.Narrate(T(
                E(EventKind.PhaseChange, 0) with { Detail = "1st Main" },
                E(EventKind.ManaPaid, 1) with
                { ActorSeat = 1, SourceName = "Plains", Detail = "W" },
                E(EventKind.ManaPaid, 2) with
                { ActorSeat = 1, SourceName = "Plains", Detail = "W" },
                E(EventKind.ManaPaid, 3) with
                { ActorSeat = 1, SourceName = "Nykthos, Shrine to Nyx", Detail = "C" },
                E(EventKind.ManaPaid, 4) with
                { ActorSeat = 2, SourceName = "Island", Detail = "U" },
                E(EventKind.TurnStart, 5) with { Turn = 2 }),
            Density.Beats, manaLedger: on)
            .Select(l => l.Text).ToList();

    /// <summary>
    /// The receipt closes the phase it covers, names the player who paid, and keeps the
    /// sources in the order they were tapped (#179).
    /// </summary>
    [Test]
    public void The_mana_ledger_reports_what_each_player_spent()
    {
        var lines = Ledger(on: true);

        Assert.That(lines, Has.Some.EqualTo(
            "You pay in 1st Main: {W}{W}{C} — Plains ×2, Nykthos, Shrine to Nyx"));
        Assert.That(lines, Has.Some.EqualTo(
            "Opponent pays in 1st Main: {U} — Island"));
    }

    /// <summary>Off unless asked for: the archive holds 57,000 payments.</summary>
    [Test]
    public void The_mana_ledger_is_absent_unless_asked_for()
    {
        Assert.That(Ledger(on: false), Has.None.Contains("pay in 1st Main"));
    }

    /// <summary>
    /// A payment whose colour code the extractor does not know must not silently shrink
    /// the total: three symbols for four mana reads as a count, and is wrong.
    /// </summary>
    [Test]
    public void The_mana_ledger_falls_back_to_a_count_when_a_colour_is_unknown()
    {
        var lines = Narrator.Narrate(T(
                E(EventKind.PhaseChange, 0) with { Detail = "1st Main" },
                E(EventKind.ManaPaid, 1) with
                { ActorSeat = 1, SourceName = "Plains", Detail = "W" },
                E(EventKind.ManaPaid, 2) with
                { ActorSeat = 1, SourceName = "Weird Land", Detail = null },
                E(EventKind.TurnStart, 3) with { Turn = 2 }),
            Density.Beats, manaLedger: true)
            .Select(l => l.Text).ToList();

        Assert.That(lines, Has.Some.EqualTo(
            "You pay in 1st Main: 2 mana — Plains, Weird Land"));
        Assert.That(lines, Has.None.Contains("{W}"),
            "naming only the colour that was known would under-report the total");
    }

    /// <summary>
    /// The receipt replaces the per-payment lines rather than joining them — counting
    /// the same tap twice on one page is the noise the ledger exists to remove.
    /// </summary>
    [Test]
    public void The_mana_ledger_replaces_the_per_payment_lines_in_verbose()
    {
        var events = new[]
        {
            E(EventKind.PhaseChange, 0) with { Detail = "1st Main" },
            E(EventKind.ManaPaid, 1) with
                { ActorSeat = 1, SourceName = "Plains", Detail = "W" },
            E(EventKind.TurnStart, 2) with { Turn = 2 }
        };

        var without = Narrator.Narrate(T(events), Density.Verbose)
            .Select(l => l.Text).ToList();
        var with = Narrator.Narrate(T(events), Density.Verbose, manaLedger: true)
            .Select(l => l.Text).ToList();

        Assert.That(without, Has.Some.Contains("for mana"));
        Assert.That(with, Has.None.Contains("for mana"));
        Assert.That(with, Has.Some.Contains("You pay in 1st Main"));
    }
}
