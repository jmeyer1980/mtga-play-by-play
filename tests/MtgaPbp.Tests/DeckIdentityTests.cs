using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Which matches were played with the same deck, when Arena never logs a deck name.
/// </summary>
/// <remarks>
/// The acceptance criterion in issue #13 is that clustering "groups a list across small
/// edits and separates distinct archetypes", so both halves are asserted here — a rule
/// that merged everything would satisfy the first alone.
/// </remarks>
public class DeckIdentityTests
{
    private static IReadOnlyList<DeckEntry> Deck(params string[] names) =>
        names.Select(n => new DeckEntry(n, n is "Plains" or "Island" ? 20 : 4, true)).ToList();

    private static (string, IReadOnlyList<DeckEntry>, string?) M(
        string id, IReadOnlyList<DeckEntry> deck, string? commander = null) => (id, deck, commander);

    /// <summary>
    /// The failure the exact-fingerprint approach was rejected for: a list tweaked over
    /// an evening becoming four decks of five games each.
    /// </summary>
    [Test]
    public void A_list_edited_a_card_at_a_time_stays_one_deck()
    {
        var one = Deck("Plains", "Hare Apparent", "Ajani", "Hop to It", "Split Up", "Elspeth");
        var two = Deck("Plains", "Hare Apparent", "Ajani", "Hop to It", "Cooped Up", "Elspeth");
        var three = Deck("Plains", "Hare Apparent", "Ajani", "Hop to It", "Cooped Up", "Get Lost");

        var clusters = DeckIdentity.Cluster([M("a", one), M("b", two), M("c", three)]);

        Assert.That(clusters, Has.Count.EqualTo(1));
        Assert.That(clusters[0].MatchIds, Is.EquivalentTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Two_archetypes_that_only_share_their_lands_stay_apart()
    {
        var white = Deck("Plains", "Hare Apparent", "Ajani", "Hop to It", "Split Up");
        var blue = Deck("Island", "Zahid", "Witness Protection", "Cancel", "Fog Bank");

        var clusters = DeckIdentity.Cluster([M("a", white), M("b", blue)]);

        Assert.That(clusters, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Single linkage on purpose: each version resembles the one before it, and the
    /// first and last need not resemble each other at all.
    /// </summary>
    [Test]
    public void A_deck_reaches_its_own_earlier_self_through_the_versions_between()
    {
        var first = Deck("Plains", "A", "B", "C", "D", "E", "F", "G", "H", "I");
        var middle = Deck("Plains", "A", "B", "C", "D", "E", "F", "G", "J", "K");
        var last = Deck("Plains", "A", "B", "C", "D", "E", "J", "K", "L", "M");

        Assert.That(DeckIdentity.Similarity(
                first.Select(c => c.Name).ToHashSet(), last.Select(c => c.Name).ToHashSet()),
            Is.LessThan(DeckIdentity.SameDeck), "the ends alone would not have merged");

        var clusters = DeckIdentity.Cluster([M("a", first), M("b", middle), M("c", last)]);
        Assert.That(clusters, Has.Count.EqualTo(1), "but the middle links them");
    }

    // ---------- naming ----------

    [Test]
    public void A_deck_with_a_commander_is_called_by_it()
    {
        var deck = Deck("Swamp", "Gix's Command", "Scorpion", "Vizier");
        var clusters = DeckIdentity.Cluster([M("a", deck, "Gix, Yawgmoth Praetor")]);

        Assert.That(clusters.Single().Label, Is.EqualTo("Gix, Yawgmoth Praetor"));
        Assert.That(clusters.Single().Slug, Is.EqualTo("gix-yawgmoth-praetor"));
    }

    /// <summary>
    /// Without a commander, the card the deck runs most of — never the land it runs
    /// twenty of, which would name every deck after a basic.
    /// </summary>
    [Test]
    public void A_deck_without_a_commander_is_called_after_its_most_copied_card()
    {
        IReadOnlyList<DeckEntry> deck =
        [
            new("Plains", 20, true),
            new("Hare Apparent", 12, true),
            new("Ajani, Caller of the Pride", 2, true)
        ];

        Assert.That(DeckIdentity.Cluster([M("a", deck)]).Single().Label,
            Is.EqualTo("Hare Apparent"));
    }

    /// <summary>
    /// Pooled across the cluster, not read off one list. Computed per list it would
    /// change as the deck was edited, and a label that moves when a new match lands
    /// makes the panel look broken.
    /// </summary>
    [Test]
    public void The_name_is_pooled_across_the_cluster_rather_than_taken_from_one_list()
    {
        IReadOnlyList<DeckEntry> heavy =
        [
            new("Plains", 20, true), new("Hare Apparent", 12, true),
            new("Hop to It", 4, true), new("Ajani", 4, true), new("Split Up", 2, true)
        ];
        IReadOnlyList<DeckEntry> light =
        [
            new("Plains", 20, true), new("Hare Apparent", 2, true),
            new("Hop to It", 12, true), new("Ajani", 4, true), new("Split Up", 2, true)
        ];

        // Alone, each list names itself after a different card.
        Assert.That(DeckIdentity.Cluster([M("a", heavy)]).Single().Label, Is.EqualTo("Hare Apparent"));
        Assert.That(DeckIdentity.Cluster([M("b", light)]).Single().Label, Is.EqualTo("Hop to It"));

        // Together they are one deck with one name, whichever order they arrive in.
        var forward = DeckIdentity.Cluster([M("a", heavy), M("b", light)]).Single().Label;
        var backward = DeckIdentity.Cluster([M("b", light), M("a", heavy)]).Single().Label;
        Assert.That(forward, Is.EqualTo(backward));
        Assert.That(forward, Is.EqualTo("Hop to It"), "16 copies pooled against 14 — the pool decides, not either list");
    }

    [Test]
    public void A_match_whose_log_carried_no_decklist_is_not_a_deck()
    {
        var clusters = DeckIdentity.Cluster([M("a", []), M("b", Deck("Plains", "Hare Apparent"))]);

        Assert.That(clusters, Has.Count.EqualTo(1));
        Assert.That(clusters.Single().MatchIds, Is.EquivalentTo(new[] { "b" }));
    }

    [Test]
    public void Two_decks_that_would_answer_to_one_name_are_told_apart()
    {
        var white = Deck("Plains", "Hare Apparent", "Ajani", "Hop to It", "Split Up");
        var other = Deck("Island", "Hare Apparent", "Zahid", "Cancel", "Fog Bank");

        var labels = DeckIdentity.Cluster([M("a", white), M("b", other)])
            .Select(c => c.Label).ToList();

        Assert.That(labels, Has.Count.EqualTo(2));
        Assert.That(labels.Distinct().Count(), Is.EqualTo(2), "a name is a name for one deck");
    }
}
