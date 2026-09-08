using System.Xml.Linq;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// A card's other faces get a home in the card lists (#221): the Adventure half a
/// creature carries, the doors of a Room, the back of a double-faced card — listed
/// under the card they belong to, each with its own peek, so a name the transcript
/// says has a face on the page whichever way the reader arrives at it.
/// </summary>
/// <remarks>
/// The measurement behind this (#201 §2): across 1,516 archived pages, 176 named a
/// card that none of the four card lists carried, and nearly every one was an
/// Adventure cast under its own name while the list named the creature. The answer
/// is a list entry, not a tab stop on the inline name, and these tests hold the shape
/// of that entry: visible at rest, one nesting deep, never a duplicate, mirrored by
/// the markdown export and the copy script, and absent entirely when the database
/// knows no other face — which keeps every page that has none byte-identical.
/// </remarks>
public class OtherFaceTests
{
    // Plain faces first, then the linked ones derived from them: a face reached
    // through another face carries no links of its own, exactly as CardDb builds them.
    private static readonly CardFace GiantFace = new(
        "Bonecrusher Giant", "{2}{R}", "Creature — Giant",
        ["Whenever this creature becomes the target of a spell, it deals 2 damage to that spell's controller."],
        "4", "3");

    private static readonly CardFace StompFace = new(
        "Stomp", "{1}{R}", "Instant — Adventure",
        ["Stomp deals 2 damage to any target."], null, null);

    private static readonly CardFace Giant = GiantFace with { OtherFaces = [StompFace] };
    private static readonly CardFace Stomp = StompFace with { OtherFaces = [GiantFace] };

    private static readonly CardFace ShopFace = new(
        "Dollmaker's Shop", "{1}{W}", "Enchantment — Room",
        ["When you unlock this door, create a 1/1 white Toy artifact creature token."], null, null);

    private static readonly CardFace GalleryFace = new(
        "Porcelain Gallery", "{4}{W}{W}", "Enchantment — Room",
        ["Creatures you control get +1/+1 for each creature you control."], null, null);

    private static readonly CardFace RoomFace = new(
        "Dollmaker's Shop // Porcelain Gallery", "{1}{W}{4}{W}{W}", "Enchantment — Room",
        [], null, null);

    private static readonly CardFace Room = RoomFace with { OtherFaces = [ShopFace, GalleryFace] };
    private static readonly CardFace Shop = ShopFace with { OtherFaces = [GalleryFace] };

    private static readonly CardFace TibaltFace = new(
        "Tibalt, Cosmic Impostor", "{5}{B}{R}", "Legendary Planeswalker — Tibalt",
        ["As Tibalt, Cosmic Impostor enters, you get an emblem."], null, "5");

    private static readonly CardFace Valki = new CardFace(
        "Valki, God of Lies", "{1}{B}", "Legendary Creature — God",
        ["When Valki enters, each opponent reveals their hand."], "2", "1")
    { OtherFaces = [TibaltFace] };

    private static readonly CardFace Plains = new("Plains", "", "Basic Land — Plains", [], null, null);

    private static IReadOnlyDictionary<string, CardFace> Faces(params CardFace[] faces) =>
        faces.ToDictionary(f => f.Name, StringComparer.Ordinal);

    private static Transcript Deck(params DeckEntry[] entries) =>
        RendererTests.Sample(deck: entries);

    private static XElement Section(string html, string id) =>
        Markup.Parse(html).Descendants("details").Single(d => d.Attribute("id")?.Value == id);

    /// <summary>The list's own entries: the <c>ul.cards</c> directly inside the section.</summary>
    private static List<XElement> Entries(XElement section) =>
        section.Elements("ul").Single(u => u.Attribute("class")?.Value == "cards")
            .Elements("li").ToList();

    private static List<XElement> Nested(XElement entry) =>
        entry.Elements("ul").Where(u => u.Attribute("class")?.Value == "faces")
            .SelectMany(u => u.Elements("li")).ToList();

    private static string SummaryOf(XElement element) =>
        Markup.Spoken(element.Descendants("summary").First());

    // ---------- the deck ----------

    [Test]
    public void A_deck_entry_lists_its_other_face_under_it_with_a_peek()
    {
        var t = Deck(new DeckEntry("Bonecrusher Giant", 4, true));
        var html = GamePageRenderer.Render(t, faces: Faces(Giant));

        Assert.That(html, Does.Contain(
            """<ul class="faces" role="list"><li><details class="peek"><summary>Stomp</summary>"""));
        Assert.That(html, Does.Contain("Instant — Adventure"));
        Assert.That(html, Does.Contain("Stomp deals 2 damage to any target."));

        var entries = Entries(Section(html, "deck"));
        Assert.That(entries, Has.Count.EqualTo(1), "the deck still holds one distinct card");
        var nested = Nested(entries[0]);
        Assert.That(nested, Has.Count.EqualTo(1));
        Assert.That(SummaryOf(nested[0]), Is.EqualTo("Stomp"));
    }

    /// <summary>
    /// The entry itself is the line it always was: count, spoken twin, name, and the
    /// heading's count of distinct cards. The other face is beneath it, not in it.
    /// </summary>
    [Test]
    public void The_entry_line_and_the_deck_count_are_untouched()
    {
        var t = Deck(new DeckEntry("Bonecrusher Giant", 4, true));
        var html = GamePageRenderer.Render(t, faces: Faces(Giant));

        Assert.That(html, Does.Contain(TranscriptSummary.DeckHeading(t)));
        var entry = Entries(Section(html, "deck"))[0];
        var summary = entry.Elements("details").First().Element("summary")!;
        Assert.That(Markup.Clipboard(summary), Is.EqualTo("4× Bonecrusher Giant"));
        Assert.That(Markup.Spoken(summary), Is.EqualTo("4 copies of Bonecrusher Giant"));
    }

    /// <summary>
    /// The nested line is the name and nothing else — no count, no "not seen" mark:
    /// those are facts about the card, and the entry above carries them once.
    /// </summary>
    [Test]
    public void The_nested_face_reads_as_its_bare_name_to_everyone()
    {
        var t = Deck(new DeckEntry("Bonecrusher Giant", 1, false));
        var html = GamePageRenderer.Render(t, faces: Faces(Giant));

        var nested = Nested(Entries(Section(html, "deck"))[0]).Single();
        var summary = nested.Descendants("summary").First();
        Assert.That(Markup.Spoken(summary), Is.EqualTo("Stomp"));
        Assert.That(Markup.Clipboard(summary), Is.EqualTo("Stomp"));
        Assert.That(nested.Parent!.Attribute("role")?.Value, Is.EqualTo("list"));
    }

    [Test]
    public void A_room_lists_both_its_doors()
    {
        var t = Deck(new DeckEntry("Dollmaker's Shop // Porcelain Gallery", 1, true));
        var html = GamePageRenderer.Render(t, faces: Faces(Room));

        var nested = Nested(Entries(Section(html, "deck"))[0]);
        Assert.That(nested.Select(SummaryOf),
            Is.EqualTo(new[] { "Dollmaker's Shop", "Porcelain Gallery" }));
        Assert.That(html, Does.Contain("<summary>Dollmaker&#39;s Shop</summary>"), "encoded like every name");
    }

    // ---------- the export and the clipboard ----------

    [Test]
    public void The_markdown_export_nests_the_other_face_under_its_card()
    {
        var t = Deck(new DeckEntry("Bonecrusher Giant", 4, true));
        var nl = Environment.NewLine;

        var md = MarkdownRenderer.Render(t, faces: Faces(Giant));
        Assert.That(md, Does.Contain($"- 4× Bonecrusher Giant{nl}  - Stomp{nl}"));

        Assert.That(MarkdownRenderer.Render(t), Does.Not.Contain("Stomp"),
            "no faces, no nesting — the export is what it was");
    }

    [Test]
    public void The_markdown_export_nests_a_rooms_doors_in_order()
    {
        var t = Deck(new DeckEntry("Dollmaker's Shop // Porcelain Gallery", 1, true));
        var nl = Environment.NewLine;

        var md = MarkdownRenderer.Render(t, faces: Faces(Room));
        Assert.That(md, Does.Contain(
            $"- 1× Dollmaker's Shop // Porcelain Gallery{nl}  - Dollmaker's Shop{nl}  - Porcelain Gallery{nl}"));
    }

    /// <summary>
    /// The page, the export and the clipboard are one document, so the copy script
    /// walks the list the way the export writes it: each entry, then the faces under
    /// it two spaces in — and an entry's own text stops where its nested list starts,
    /// or the paste would read "4× Bonecrusher Giant Stomp".
    /// </summary>
    [Test]
    public void The_copy_script_walks_the_nested_faces_the_way_the_export_writes_them()
    {
        var t = Deck(new DeckEntry("Bonecrusher Giant", 4, true));
        var html = GamePageRenderer.Render(t, faces: Faces(Giant));

        Assert.That(html, Does.Contain("querySelectorAll('.vh, .face, .faces')"));
        Assert.That(html, Does.Contain("'.faces li'"));
        Assert.That(html, Does.Contain("'  - '"));
    }

    // ---------- seen from the opponent ----------

    [Test]
    public void An_opponent_card_lists_its_other_face_too()
    {
        var t = Deck(new DeckEntry("Plains", 30, true)) with { OpponentCards = ["Stomp"] };
        var html = GamePageRenderer.Render(t, faces: Faces(Stomp, Plains));
        var nl = Environment.NewLine;

        var nested = Nested(Entries(Section(html, "their-cards"))[0]);
        Assert.That(nested.Select(SummaryOf), Is.EqualTo(new[] { "Bonecrusher Giant" }));
        Assert.That(MarkdownRenderer.Render(t, faces: Faces(Stomp, Plains)),
            Does.Contain($"- Stomp{nl}  - Bonecrusher Giant{nl}"));
    }

    /// <summary>
    /// The opponent's list is built from what was seen, so it can carry both halves
    /// of one card. Each already has its own peek, and nesting each under the other
    /// would say the same thing twice.
    /// </summary>
    [Test]
    public void An_other_face_that_is_listed_itself_is_not_nested()
    {
        var t = Deck(new DeckEntry("Plains", 30, true))
            with
        { OpponentCards = ["Bonecrusher Giant", "Stomp"] };
        var html = GamePageRenderer.Render(t, faces: Faces(Giant, Stomp, Plains));

        Assert.That(html, Does.Not.Contain("class=\"faces\""));
        var entries = Entries(Section(html, "their-cards"));
        Assert.That(entries.Select(e => e.Descendants("details").Count()), Is.EqualTo(new[] { 1, 1 }),
            "each half keeps exactly its own peek");
        Assert.That(MarkdownRenderer.Render(t, faces: Faces(Giant, Stomp, Plains)),
            Does.Not.Contain("  - "));
    }

    /// <summary>
    /// A door and the whole Room can both be listed, and both know the other door.
    /// It is homed once, under the first entry that can claim it — the list is in name
    /// order, so which one that is never changes between builds.
    /// </summary>
    [Test]
    public void A_face_two_entries_could_claim_is_homed_once()
    {
        var t = Deck(new DeckEntry("Plains", 30, true))
            with
        { OpponentCards = ["Dollmaker's Shop", "Dollmaker's Shop // Porcelain Gallery"] };
        var faces = Faces(Shop, Room, Plains);
        var html = GamePageRenderer.Render(t, faces: faces);

        var entries = Entries(Section(html, "their-cards"));
        Assert.That(Nested(entries[0]).Select(SummaryOf), Is.EqualTo(new[] { "Porcelain Gallery" }));
        Assert.That(Nested(entries[1]), Is.Empty);
        Assert.That(html.Split("<summary>Porcelain Gallery</summary>"), Has.Length.EqualTo(2));

        var nl = Environment.NewLine;
        var md = MarkdownRenderer.Render(t, faces: faces);
        Assert.That(md, Does.Contain($"- Dollmaker's Shop{nl}  - Porcelain Gallery{nl}- Dollmaker's Shop // Porcelain Gallery{nl}"));
    }

    // ---------- the commander ----------

    /// <summary>
    /// The commander is a paragraph, not a list entry, so its other face has no list
    /// to sit in: it is a peek inside the commander's own peek. The line itself — the
    /// one the export and the clipboard carry — says what it always said.
    /// </summary>
    [Test]
    public void A_commanders_other_face_is_a_peek_inside_the_commanders_peek()
    {
        var t = RendererTests.Sample(
            deck: [new DeckEntry("Plains", 33, true)],
            commanders: ["Valki, God of Lies"]);
        var html = GamePageRenderer.Render(t, faces: Faces(Valki, Plains));

        // The peek whose own summary is the commander line — not the deck disclosure
        // around it, which contains that line too.
        var commander = Markup.Parse(html).Descendants("details")
            .Single(d => d.Element("summary")?.Descendants("span")
                .Any(s => s.Attribute("class")?.Value == "commander") == true);
        var inner = commander.Elements("details").ToList();
        Assert.That(inner, Has.Count.EqualTo(1));
        Assert.That(SummaryOf(inner[0]), Is.EqualTo("Tibalt, Cosmic Impostor"));
        Assert.That(html, Does.Contain("Legendary Planeswalker — Tibalt"));
        Assert.That(html, Does.Not.Contain("class=\"faces\""));

        var line = commander.Descendants("span").Single(s => s.Attribute("class")?.Value == "commander");
        Assert.That(Markup.Clipboard(line), Is.EqualTo("Commander: Valki, God of Lies"));
        var md = MarkdownRenderer.Render(t, faces: Faces(Valki, Plains));
        Assert.That(md, Does.Contain("Commander: Valki, God of Lies"));
        Assert.That(md, Does.Not.Contain("Tibalt"));
    }

    // ---------- what does not change ----------

    /// <summary>
    /// Other faces enter the tooltip island the way any face does — because a line
    /// named them — and not because a list entry knows them. The island is sized by
    /// what the transcript says, and that rule does not bend here.
    /// </summary>
    [Test]
    public void An_other_face_no_line_names_stays_out_of_the_tooltip_island()
    {
        var t = Deck(new DeckEntry("Bonecrusher Giant", 4, true)) with
        {
            Events =
            [
                new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1 },
                new GameEvent { Seq = 1, Kind = EventKind.SpellCast, Turn = 1, ActorSeat = 1,
                                SourceName = "Bonecrusher Giant" },
                new GameEvent { Seq = 2, Kind = EventKind.GameEnd, Detail = "You win the match" },
            ]
        };
        var html = GamePageRenderer.Render(t, faces: Faces(Giant));

        Assert.That(html, Does.Contain("""<div class="face" data-card="Bonecrusher Giant">"""));
        Assert.That(html, Does.Not.Contain("data-card=\"Stomp\""));
    }

    /// <summary>
    /// No other face, no nesting: a face without links renders the entry it always
    /// did, and a linked face for a card the page never lists changes nothing at all.
    /// </summary>
    [Test]
    public void Without_another_face_the_page_and_the_export_are_what_they_were()
    {
        var t = Deck(new DeckEntry("Bonecrusher Giant", 4, true));

        Assert.That(GamePageRenderer.Render(t, faces: Faces(GiantFace)),
            Does.Not.Contain("class=\"faces\""));
        Assert.That(MarkdownRenderer.Render(t, faces: Faces(GiantFace)),
            Is.EqualTo(MarkdownRenderer.Render(t)));

        var plain = GamePageRenderer.Render(t);
        Assert.That(GamePageRenderer.Render(t, faces: Faces(Stomp)), Is.EqualTo(plain),
            "a linked face for a card not on the page changes nothing");
        Assert.That(MarkdownRenderer.Render(t, faces: Faces(Stomp)),
            Is.EqualTo(MarkdownRenderer.Render(t)));
    }
}
