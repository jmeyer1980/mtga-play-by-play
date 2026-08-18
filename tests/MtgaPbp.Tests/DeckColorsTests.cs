using MtgaPbp.Core;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The colour identity of the registered deck, which is the only thing Arena gives us
/// to tell one archived deck from another — it never sends a deck name.
/// </summary>
public class DeckColorsTests
{
    private const string Land = "5";
    private const string Creature = "2";
    private const string Artifact = "1";

    /// <summary>
    /// Colour identities are stored the way the sampled database stores them, which is
    /// not WUBRG order: Kaito is "3,2" and Anzrag "5,4". Reproducing that here is the
    /// point — a fake that pre-sorted would let an unsorted implementation pass.
    /// </summary>
    private sealed class Cards : ICardDb
    {
        private readonly Dictionary<int, (string Types, string? Identity)> _cards;

        public Cards(params (int GrpId, string Types, string? Identity)[] cards) =>
            _cards = cards.ToDictionary(c => c.GrpId, c => (c.Types, c.Identity));

        public CardInfo? CardForGrpId(int grpId) =>
            _cards.TryGetValue(grpId, out var c)
                ? new CardInfo(grpId, $"Card {grpId}", c.Types, null, null, false)
                { ColorIdentity = c.Identity }
                : null;

        public string? NameForLocId(int locId) => null;
        public string? EnumName(string type, int value) => null;
        public string? AbilityText(int abilityGrpId) => null;
    }

    [Test]
    public void Colours_come_out_in_WUBRG_order_whatever_order_the_database_stored_them()
    {
        var cards = new Cards(
            (1, Creature, "3,2"),
            (2, Creature, "5,4"),
            (3, Creature, "1"));

        Assert.That(DeckColors.Of([1, 2, 3], [], cards), Is.EqualTo("WUBRG"));
    }

    [Test]
    public void A_repeated_card_does_not_repeat_its_colour()
    {
        var cards = new Cards((1, Creature, "2"));
        Assert.That(DeckColors.Of([1, 1, 1, 1], [], cards), Is.EqualTo("U"));
    }

    /// <summary>
    /// Brawl defines a deck's colours as its commander's identity and constrains the
    /// library to match, so the commander is exact where the union is a reconstruction.
    /// The deck here is deliberately wider than the commander to prove which one wins.
    /// </summary>
    [Test]
    public void A_commander_decides_the_colours_on_its_own()
    {
        var cards = new Cards(
            (1, Creature, "3,2"),
            (2, Creature, "4"),
            (3, Creature, "5"));

        Assert.That(DeckColors.Of([2, 3], [1], cards), Is.EqualTo("UB"));
    }

    [Test]
    public void Two_partner_commanders_are_both_counted()
    {
        var cards = new Cards(
            (1, Creature, "1"),
            (2, Creature, "3"));

        Assert.That(DeckColors.Of([], [1, 2], cards), Is.EqualTo("WB"));
    }

    /// <summary>
    /// A Golgari deck splashing a Plains for a utility land is not a three-colour deck,
    /// and a dual land is not a second colour either.
    /// </summary>
    [Test]
    public void Lands_are_left_out_of_the_union()
    {
        var cards = new Cards(
            (1, Creature, "3"),
            (2, Creature, "5"),
            (3, Land, "1"),
            (4, Land, "2,4"));

        Assert.That(DeckColors.Of([1, 2, 3, 4], [], cards), Is.EqualTo("BG"));
    }

    /// <summary>
    /// A creature land is a land: it is in the mana base, and the colour it taps for
    /// says nothing about the spells the deck casts.
    /// </summary>
    [Test]
    public void A_card_that_is_a_land_among_other_types_is_still_a_land()
    {
        var cards = new Cards(
            (1, Creature, "4"),
            (2, $"{Creature},{Land}", "1"));

        Assert.That(DeckColors.Of([1, 2], [], cards), Is.EqualTo("R"));
    }

    /// <summary>
    /// One off-colour card in sixty still widens the string. The registered deck is a
    /// fact; a splash threshold would be a judgment the log does not support.
    /// </summary>
    [Test]
    public void A_single_off_colour_card_still_counts()
    {
        var cards = new Cards(
            (1, Creature, "1"),
            (2, Creature, "4"));

        var deck = Enumerable.Repeat(1, 59).Append(2).ToList();
        Assert.That(DeckColors.Of(deck, [], cards), Is.EqualTo("WR"));
    }

    [Test]
    public void A_deck_of_no_colours_is_colourless()
    {
        var cards = new Cards(
            (1, Artifact, ""),
            (2, Creature, ""));

        Assert.That(DeckColors.Of([1, 2], [], cards), Is.EqualTo("C"));
    }

    /// <summary>
    /// 103 of the 476 matches archived so far predate the deck being captured. Those
    /// have to stay tellable apart from a colourless deck: one is a fact, the other is
    /// the absence of one.
    /// </summary>
    [Test]
    public void A_match_with_no_deck_reports_nothing_rather_than_colourless()
    {
        Assert.That(DeckColors.Of([], [], new Cards()), Is.Null);
    }

    [Test]
    public void A_deck_the_card_database_cannot_resolve_reports_nothing()
    {
        Assert.That(DeckColors.Of([7, 8, 9], [], new Cards()), Is.Null);
    }

    /// <summary>
    /// A card database that predates colour capture answers null, not "". Reading that
    /// as colourless would put a claim on every row rendered from an old fixture.
    /// </summary>
    [Test]
    public void A_card_with_no_recorded_colour_contributes_nothing_and_proves_nothing()
    {
        var cards = new Cards((1, Creature, null));
        Assert.That(DeckColors.Of([1], [], cards), Is.Null);
    }

    /// <summary>
    /// Excluding the lands can leave nothing behind. "No colours found" would then read
    /// as colourless, which is a claim about a deck nobody looked at the spells of.
    /// </summary>
    [Test]
    public void A_deck_of_nothing_but_lands_reports_nothing()
    {
        var cards = new Cards((1, Land, "1"), (2, Land, "5"));
        Assert.That(DeckColors.Of([1, 2], [], cards), Is.Null);
    }

    [TestCase("W", ExpectedResult = "white")]
    [TestCase("WU", ExpectedResult = "white and blue")]
    [TestCase("WUB", ExpectedResult = "white, blue and black")]
    [TestCase("WUBRG", ExpectedResult = "white, blue, black, red and green")]
    [TestCase("C", ExpectedResult = "colourless")]
    public string Colours_are_spelled_out_for_a_synthesiser(string letters) =>
        DeckColors.Spoken(letters);
}
