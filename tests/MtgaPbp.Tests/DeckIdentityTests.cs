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

    /// <summary>
    /// A nonbasic land never names a deck. The basics list alone was not enough: it
    /// held only until a deck was small enough that its best spell also ran four
    /// copies, and then the archive produced a deck called "Dimir Guildgate".
    /// </summary>
    [Test]
    public void A_nonbasic_land_does_not_get_to_name_a_deck()
    {
        IReadOnlyList<DeckEntry> deck =
        [
            new("Dimir Guildgate", 4, true, IsLand: true),
            new("Dreadwing Scavenger", 4, true),
            new("Swamp", 20, true, IsLand: true)
        ];

        Assert.That(DeckIdentity.Cluster([M("a", deck)]).Single().Label,
            Is.EqualTo("Dreadwing Scavenger"));
    }

    /// <summary>
    /// In Brawl the commander is the deck, so two of them are two decks however much
    /// of the library they have in common — which, in one colour built out of one
    /// collection, is routinely most of it.
    /// </summary>
    [Test]
    public void Two_commanders_are_two_decks_even_sharing_almost_every_card()
    {
        var shared = new[] { "Island", "A", "B", "C", "D", "E", "F", "G", "H" };
        var one = Deck([.. shared, "Solo"]);
        var two = Deck([.. shared, "Duet"]);

        Assert.That(DeckIdentity.Similarity(
                one.Select(c => c.Name).ToHashSet(), two.Select(c => c.Name).ToHashSet()),
            Is.GreaterThan(DeckIdentity.SameDeck), "these would merge on cards alone");

        var clusters = DeckIdentity.Cluster([M("a", one, "Sai, Master Thopterist"),
                                             M("b", two, "Iron Man, Futurist Paragon")]);

        Assert.That(clusters, Has.Count.EqualTo(2));
        Assert.That(clusters.Select(c => c.Label),
            Is.EquivalentTo(new[] { "Sai, Master Thopterist", "Iron Man, Futurist Paragon" }));
    }

    /// <summary>
    /// A deck keeps the same name and the same filter token whatever order the archive
    /// hands its matches over in. The renderer hands them over newest-first, so an
    /// order-sensitive name changes the moment a new match lands.
    /// </summary>
    [Test]
    public void A_shared_name_is_split_the_same_way_whichever_order_the_matches_arrive()
    {
        var white = Deck("Plains", "Hare Apparent", "Ajani", "Hop to It", "Split Up");
        var blue = Deck("Island", "Hare Apparent", "Zahid", "Cancel", "Fog Bank");

        var forward = DeckIdentity.Cluster([M("a", white), M("b", blue)]);
        var backward = DeckIdentity.Cluster([M("b", blue), M("a", white)]);

        // Same deck, same name, same slug, whichever way round.
        foreach (var id in new[] { "a", "b" })
        {
            var one = forward.Single(c => c.MatchIds.Contains(id));
            var other = backward.Single(c => c.MatchIds.Contains(id));
            Assert.That(other.Label, Is.EqualTo(one.Label), id);
            Assert.That(other.Slug, Is.EqualTo(one.Slug), id);
        }

        // And neither slug is a prefix of the other, because the table filters on them.
        var slugs = forward.Select(c => c.Slug).ToList();
        Assert.That(slugs[0], Does.Not.StartWith(slugs[1]));
        Assert.That(slugs[1], Does.Not.StartWith(slugs[0]));
    }
}
