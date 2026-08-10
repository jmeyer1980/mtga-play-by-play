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
        events, new Dictionary<string, int>(), new HashSet<string>());

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
                SourceName = "Lightning Bolt", TargetInstanceId = 99,
                TargetName = "Llanowar Elves", Amount = 3
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
    public void Narrate_drops_events_it_cannot_phrase_rather_than_emitting_blanks()
    {
        var lines = Narrator.Narrate(
            T(E(EventKind.ZoneMove) with { SourceName = null }), Density.Beats);
        Assert.That(lines.Any(l => string.IsNullOrWhiteSpace(l.Text)), Is.False);
    }
}
