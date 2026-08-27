using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The decklist peek: each entry can open into a text facsimile of the card, built
/// from Arena's own database — issue #99.
/// </summary>
/// <remarks>
/// The load-bearing property is the last test: with no face to show, the markup is
/// byte-identical to what it was before the feature existed. That is what keeps the
/// golden files honest and a CI runner with no card database rendering the same page
/// it always did.
/// </remarks>
public class CardPeekTests
{
    [TestCase("o1oW", "{1}{W}")]
    [TestCase("oXoB", "{X}{B}")]
    [TestCase("o0", "{0}")]
    [TestCase("o1o(R/G)", "{1}{R/G}")]
    [TestCase("oXo2oBoR", "{X}{2}{B}{R}")]
    [TestCase("", "")]
    [TestCase(null, "")]
    public void Mana_text_decodes_to_curly_notation(string? raw, string expected) =>
        Assert.That(CardFace.DecodeMana(raw), Is.EqualTo(expected));

    private static readonly CardFace Hare = new(
        "Hare Apparent", "{1}{W}", "Creature — Rabbit Noble",
        ["A deck can have any number of cards named Hare Apparent.",
         "When this creature enters, create a number of 1/1 white Rabbit creature " +
         "tokens equal to the number of other creatures you control named Hare Apparent."],
        "2", "2");

    private static IReadOnlyDictionary<string, CardFace> Faces(params CardFace[] faces) =>
        faces.ToDictionary(f => f.Name, StringComparer.Ordinal);

    [Test]
    public void A_deck_entry_with_a_face_opens_into_the_card()
    {
        var t = RendererTests.Sample(deck: [new DeckEntry("Hare Apparent", 26, true)]);
        var html = GamePageRenderer.Render(t, faces: Faces(Hare));

        Assert.That(html, Does.Contain("class=\"peek\""));
        Assert.That(html, Does.Contain("{1}{W}"));
        Assert.That(html, Does.Contain("Creature — Rabbit Noble"));
        Assert.That(html, Does.Contain("A deck can have any number"));
        Assert.That(html, Does.Contain(">2/2<"));
    }

    /// <summary>
    /// The summary line is the decklist entry it always was — count, spoken twin,
    /// name — so closed peeks read (and copy) like the plain list did.
    /// </summary>
    [Test]
    public void The_closed_peek_still_reads_as_the_decklist_entry()
    {
        var t = RendererTests.Sample(deck: [new DeckEntry("Hare Apparent", 26, false)]);
        var html = GamePageRenderer.Render(t, faces: Faces(Hare));

        Assert.That(html, Does.Contain("26×"));
        Assert.That(html, Does.Contain("26 copies of"));
        Assert.That(html, Does.Contain("not seen"));
    }

    [Test]
    public void The_open_face_links_to_scryfall_by_exact_name()
    {
        var t = RendererTests.Sample(deck: [new DeckEntry("Hare Apparent", 26, true)]);
        var html = GamePageRenderer.Render(t, faces: Faces(Hare));

        Assert.That(html,
            Does.Contain("https://scryfall.com/search?q=%21%22Hare%20Apparent%22"));
    }

    [Test]
    public void The_commander_gets_a_face_too()
    {
        var elspeth = new CardFace(
            "Elspeth, Storm Slayer", "{3}{W}{W}", "Legendary Planeswalker — Elspeth",
            ["If one or more tokens would be created under your control, twice that " +
             "many of those tokens are created instead."], null, null);
        var t = RendererTests.Sample(
            deck: [new DeckEntry("Plains", 33, true)],
            commanders: ["Elspeth, Storm Slayer"]);
        var html = GamePageRenderer.Render(t, faces: Faces(elspeth));

        Assert.That(html, Does.Contain("class=\"commander\""));
        Assert.That(html, Does.Contain("{3}{W}{W}"));
        Assert.That(html, Does.Contain("twice that many"));
    }

    /// <summary>
    /// A planeswalker's face has no statline, and the face must not invent one —
    /// the same honesty rule statlines follow everywhere else.
    /// </summary>
    [Test]
    public void A_face_without_power_and_toughness_shows_no_statline()
    {
        var talent = new CardFace(
            "Caretaker's Talent", "{2}{W}", "Enchantment — Class",
            ["When this Class enters, draw a card."], null, null);
        var t = RendererTests.Sample(deck: [new DeckEntry("Caretaker's Talent", 1, true)]);
        var html = GamePageRenderer.Render(t, faces: Faces(talent));

        Assert.That(html, Does.Not.Contain("class=\"face-pt\""));
    }

    [Test]
    public void Face_text_is_escaped()
    {
        var spiky = new CardFace(
            "AT&T <Test>", "{1}", "Artifact — <Gadget>",
            ["Tap: AT&T <does> something."], null, null);
        var t = RendererTests.Sample(deck: [new DeckEntry("AT&T <Test>", 1, true)]);
        var html = GamePageRenderer.Render(t, faces: Faces(spiky));

        Assert.That(html, Does.Not.Contain("<Gadget>"));
        Assert.That(html, Does.Not.Contain("<does>"));
        Assert.That(html, Does.Contain("&lt;Gadget&gt;"));
    }

    /// <summary>
    /// No face, no feature: an entry the dictionary cannot answer for renders exactly
    /// the line it always did, and a page with no dictionary at all — a CI runner, a
    /// golden fixture, a machine without Arena — is byte-identical to before.
    /// </summary>
    [Test]
    public void Without_a_face_the_markup_is_byte_identical_to_before()
    {
        var t = RendererTests.Sample(
            deck: [new DeckEntry("Hare Apparent", 26, true)],
            commanders: ["Elspeth, Storm Slayer"]);

        var plain = GamePageRenderer.Render(t);
        Assert.That(GamePageRenderer.Render(t, faces: null), Is.EqualTo(plain));
        Assert.That(GamePageRenderer.Render(t, faces: Faces()), Is.EqualTo(plain));

        var stranger = new CardFace("Some Other Card", "{1}", "Artifact", [], null, null);
        Assert.That(GamePageRenderer.Render(t, faces: Faces(stranger)), Is.EqualTo(plain),
            "a face for a card not in this deck changes nothing");
    }
}
