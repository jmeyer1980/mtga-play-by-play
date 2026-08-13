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
        // about a card that moved.
        Assert.That(Moved("ZoneType_Library", null, "Put"), Is.EqualTo("Plains moves (Put)"));
        Assert.That(Moved(null, "ZoneType_Sideboard", "Put"),
            Is.EqualTo("Plains moves (Put)"), "and the same for a zone with no phrasing");
    }
}
