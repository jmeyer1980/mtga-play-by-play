using System.Xml.Linq;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The card under the pointer: every card name in a narrated line that the database
/// has a face for shows that face on hover — issue #201, phase 2 of the peeks (#99).
/// </summary>
/// <remarks>
/// The feature is pixels only, and every test here is one of the invariants that
/// says so. A wrapper span carries no role, no title and no ARIA, so the page's
/// accessibility tree is what it was; the face rides once in an inert island rather
/// than once per mention; the clipboard, the markdown export and find-in-page see a
/// name exactly as they did; and a page with nothing to show is byte-identical to a
/// page that never heard of the feature. The two listening tests behind the peeks
/// (#1, #61) stay valid on that basis, and it is the basis this file guards.
/// </remarks>
public class CardTooltipTests
{
    private static readonly CardFace Hare = new(
        "Hare Apparent", "{1}{W}", "Creature — Rabbit Noble",
        ["A deck can have any number of cards named Hare Apparent."], "2", "2");

    private static readonly CardFace Plains = new("Plains", "", "Basic Land — Plains", [], null, null);

    private static readonly CardFace Sheoldred = new(
        "Sheoldred, the Apocalypse", "{2}{B}{B}", "Legendary Creature — Phyrexian Praetor",
        ["Deathtouch"], "4", "5");

    private static readonly CardFace Sheol = new(
        "Sheoldred", "{4}{B}{B}", "Legendary Creature — Phyrexian Praetor", ["Flying"], "5", "5");

    private static readonly CardFace Talent = new(
        "Caretaker's Talent", "{2}{W}", "Enchantment — Class",
        ["When this Class enters, draw a card."], null, null);

    private static IReadOnlyDictionary<string, CardFace> Faces(params CardFace[] faces) =>
        faces.ToDictionary(f => f.Name, StringComparer.Ordinal);

    /// <summary>
    /// A turn that says a card's name every way the narrator can: plain, possessive,
    /// with a statline, with a label letter, on a board line, and as a prefix of a
    /// longer name on the same page.
    /// </summary>
    private static Transcript Named() => RendererTests.Sample() with
    {
        Events =
        [
            new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1 },
            new GameEvent { Seq = 1, Kind = EventKind.LandPlayed, Turn = 1, ActorSeat = 1,
                            SourceName = "Plains" },
            new GameEvent { Seq = 2, Kind = EventKind.SpellCast, Turn = 1, ActorSeat = 1,
                            SourceName = "Hare Apparent" },
            new GameEvent { Seq = 3, Kind = EventKind.Triggered, Turn = 1, ActorSeat = 1,
                            SourceName = "Hare Apparent's ability" },
            new GameEvent { Seq = 4, Kind = EventKind.Attack, Turn = 1, ActorSeat = 1,
                            SourceName = "Hare Apparent 5/5" },
            new GameEvent { Seq = 5, Kind = EventKind.SpellCast, Turn = 1, ActorSeat = 2,
                            SourceName = "Sheoldred, the Apocalypse" },
            new GameEvent { Seq = 6, Kind = EventKind.SpellCast, Turn = 1, ActorSeat = 2,
                            SourceName = "Caretaker's Talent" },
            new GameEvent { Seq = 7, Kind = EventKind.BoardSnapshot, Turn = 1, ActorSeat = 1,
                            Detail = "Rabbit 1/1 · Hare Apparent A 3/3" },
            new GameEvent { Seq = 8, Kind = EventKind.GameEnd, Detail = "You win the match" },
        ]
    };

    private static XElement Beats(XElement page) =>
        page.Descendants("section").Single(s => s.Attribute("id")?.Value == "beats");

    private static List<XElement> Spans(XElement root) =>
        root.Descendants("span").Where(e => e.Attribute("class")?.Value == "card").ToList();

    [Test]
    public void A_narrated_name_with_a_face_is_wrapped_for_the_pointer()
    {
        var html = GamePageRenderer.Render(Named(), faces: Faces(Hare));

        Assert.That(html, Does.Contain("""<span class="card">Hare Apparent</span>"""));
        Assert.That(html, Does.Contain("""<template id="card-faces">"""));
        Assert.That(html, Does.Contain("tip.id = 'card-tip'"));
    }

    /// <summary>
    /// A page can say the same name thirty times; its rules ride once. The island is
    /// a template, whose content is a document fragment — not rendered, not in the
    /// accessibility tree, not text — and it holds exactly the faces the lines used.
    /// </summary>
    [Test]
    public void Each_face_rides_once_however_often_the_name_is_said()
    {
        var page = Markup.Parse(GamePageRenderer.Render(Named(), faces: Faces(Hare, Plains, Talent)));

        var island = page.Descendants("template").Single(e => e.Attribute("id")?.Value == "card-faces");
        var keys = island.Elements("div").Select(d => d.Attribute("data-card")?.Value).ToList();
        Assert.That(keys, Is.EqualTo(new[] { "Caretaker's Talent", "Hare Apparent", "Plains" }));

        // Both densities carry the turn, so every mention is there at least twice.
        Assert.That(Spans(page).Count(s => s.Value == "Hare Apparent"), Is.GreaterThanOrEqualTo(8));
    }

    [Test]
    public void The_longest_name_wins()
    {
        var html = GamePageRenderer.Render(Named(), faces: Faces(Sheoldred, Sheol));

        Assert.That(html, Does.Contain("""<span class="card">Sheoldred, the Apocalypse</span>"""));
        Assert.That(html, Does.Not.Contain("""<span class="card">Sheoldred</span>, the Apocalypse"""));
    }

    /// <summary>
    /// Labels append to the name, so the name is a clean prefix and the rest stays
    /// outside the span — and the statline still gets its spoken twin.
    /// </summary>
    [Test]
    public void A_statline_a_letter_and_a_possessive_stay_outside_the_span()
    {
        var html = GamePageRenderer.Render(Named(), faces: Faces(Hare));

        const string span = """<span class="card">Hare Apparent</span>""";
        Assert.That(html, Does.Contain(span + "&#39;s ability triggers"));
        Assert.That(html, Does.Contain(span + " <span aria-hidden=\"true\">5/5</span>"));
        Assert.That(html, Does.Contain("5 power 5 toughness"));
        Assert.That(html, Does.Contain(span + " A <span aria-hidden=\"true\">3/3</span>"),
            "the board line's label letter stays out of the span");
        Assert.That(html, Does.Not.Contain("<span class=\"card\">Rabbit"), "no face, no span");
    }

    /// <summary>
    /// The span's text is the key the script looks the face up by, so it has to come
    /// back out of the browser as the name the island was keyed with: encoded on the
    /// way in, and the island's own key encoded the same way.
    /// </summary>
    [Test]
    public void Punctuation_in_a_name_is_encoded_in_the_span_and_in_the_island()
    {
        var html = GamePageRenderer.Render(Named(), faces: Faces(Talent));

        Assert.That(html, Does.Contain("""<span class="card">Caretaker&#39;s Talent</span>"""));
        Assert.That(html, Does.Contain("""<div class="face" data-card="Caretaker&#39;s Talent">"""));

        var page = Markup.Parse(html);
        var island = page.Descendants("template").Single();
        Assert.That(Spans(page).Select(s => s.Value).Distinct(),
            Is.EqualTo(island.Elements("div").Select(d => d.Attribute("data-card")!.Value)));
    }

    /// <summary>
    /// The load-bearing invariant. A span with no role adds no node to the
    /// accessibility tree, so what a synthesiser is handed for the transcript — and
    /// what the copy button puts on the clipboard — is the same text either way.
    /// </summary>
    [Test]
    public void A_span_adds_nothing_to_what_is_read_or_copied()
    {
        var plain = Markup.Parse(GamePageRenderer.Render(Named()));
        var marked = Markup.Parse(GamePageRenderer.Render(Named(), faces: Faces(Hare, Plains, Sheoldred, Talent)));

        Assert.That(Spans(marked), Is.Not.Empty, "the fixture must exercise the feature");

        foreach (var id in new[] { "beats", "verbose" })
        {
            XElement Section(XElement page) =>
                page.Descendants("section").Single(s => s.Attribute("id")?.Value == id);
            Assert.That(Markup.Spoken(Section(marked)), Is.EqualTo(Markup.Spoken(Section(plain))), $"{id}: spoken");
            Assert.That(Markup.Clipboard(Section(marked)), Is.EqualTo(Markup.Clipboard(Section(plain))), $"{id}: clipboard");
        }

        var main = marked.Descendants("main").Single();
        Assert.That(Markup.Spoken(main), Is.EqualTo(Markup.Spoken(plain.Descendants("main").Single())),
            "the island sits outside main, and main reads as it did");
    }

    /// <summary>
    /// Stated as a census rather than assumed: no title, no ARIA and no spoken twin
    /// reaches the transcript that was not there before, and the wrapper itself
    /// carries nothing but its class and the name.
    /// </summary>
    [Test]
    public void The_transcript_carries_no_title_or_aria_the_plain_page_did_not()
    {
        var plain = Beats(Markup.Parse(GamePageRenderer.Render(Named())));
        var marked = Beats(Markup.Parse(GamePageRenderer.Render(Named(), faces: Faces(Hare, Plains, Sheoldred, Talent))));

        static int Census(XElement section) => section.DescendantsAndSelf().Attributes()
            .Count(a => a.Name.LocalName == "title" || a.Name.LocalName.StartsWith("aria-", StringComparison.Ordinal));
        static int Twins(XElement section) => section.Descendants()
            .Count(e => e.Attribute("class")?.Value == "vh");

        Assert.That(Census(marked), Is.EqualTo(Census(plain)));
        Assert.That(Twins(marked), Is.EqualTo(Twins(plain)));
        foreach (var span in Spans(marked))
            Assert.That(span.Attributes().Select(a => a.Name.LocalName), Is.EqualTo(new[] { "class" }));
    }

    /// <summary>
    /// Browser find matches within one text node. A span sliced through a name would
    /// break Ctrl+F on that name, which is the architecture #99 was warning about —
    /// and the same single text node is what the script reads the name back from.
    /// </summary>
    [Test]
    public void A_name_stays_one_text_node_for_find_in_page()
    {
        var page = Markup.Parse(GamePageRenderer.Render(Named(), faces: Faces(Hare, Sheoldred, Talent)));
        var known = new[] { "Hare Apparent", "Sheoldred, the Apocalypse", "Caretaker's Talent" };

        foreach (var span in Spans(page))
        {
            Assert.That(span.Nodes().Count(), Is.EqualTo(1));
            Assert.That(span.Nodes().Single(), Is.InstanceOf<XText>());
            Assert.That(known, Does.Contain(span.Value));
        }
    }

    [Test]
    public void The_tooltip_face_carries_no_link_and_the_peek_keeps_its_own()
    {
        var t = Named() with { Deck = [new DeckEntry("Hare Apparent", 26, true)] };
        var page = Markup.Parse(GamePageRenderer.Render(t, faces: Faces(Hare)));

        var island = page.Descendants("template").Single();
        Assert.That(island.Descendants("a"), Is.Empty);

        var deck = page.Descendants("details").Single(d => d.Attribute("id")?.Value == "deck");
        Assert.That(deck.Descendants("a").Any(a => a.Attribute("href")!.Value.Contains("scryfall")), Is.True);
    }

    /// <summary>
    /// WCAG 1.4.13, read off the script the way the rest of this suite reads scripts:
    /// dismissible (Escape), hoverable (the only path that hides on leaving is deferred,
    /// and arriving on the box cancels it) and persistent (that deferred hide is the
    /// one timer in the script, so nothing closes the box on its own). The behaviour
    /// itself was driven with a real pointer in a browser on the pull request.
    /// </summary>
    [Test]
    public void The_tooltip_is_pointer_only_dismissible_hoverable_and_persistent()
    {
        var html = GamePageRenderer.Render(Named(), faces: Faces(Hare));
        // The tooltip's script is the last one on the page, after the island.
        var script = html[html.LastIndexOf("<script>", StringComparison.Ordinal)..];

        Assert.That(script, Does.Contain("matchMedia('(hover: hover) and (pointer: fine)')"));
        Assert.That(html, Does.Contain("@media (hover:hover) and (pointer:fine)"));
        Assert.That(script, Does.Contain("tip.setAttribute('aria-hidden', 'true')"));
        Assert.That(html, Does.Contain(".controls,.back,.pager,#card-tip{display:none}"));

        // Dismissible.
        Assert.That(script, Does.Contain("if (e.key === 'Escape' && !tip.hidden) hide();"));

        // Hoverable: leaving the name only schedules the hide, and entering the box
        // cancels the schedule.
        Assert.That(script, Does.Contain("closing = setTimeout(hide, 150);"));
        Assert.That(script, Does.Contain("else if (tip.contains(e.target)) clearTimeout(closing);"));
        Assert.That(script, Does.Contain("if (to && (tip.contains(to) || cardOf(to))) return;"));

        // Persistent: that deferred hide is the only timer there is.
        Assert.That(script.Split("setTimeout(").Length - 1, Is.EqualTo(1));
        Assert.That(script, Does.Not.Contain("setInterval("));
    }

    /// <summary>
    /// No face, no feature: a name the dictionary cannot answer for is exactly the run
    /// of characters it was, and a page with nothing to show — a CI runner, a golden
    /// fixture, a machine without Arena — carries no span, no island and no script.
    /// </summary>
    [Test]
    public void Without_a_matching_name_the_page_is_byte_identical()
    {
        var t = Named();
        var plain = GamePageRenderer.Render(t);

        Assert.That(plain, Does.Not.Contain("card-faces"));
        Assert.That(plain, Does.Not.Contain("tip.id = 'card-tip'"), "no script without an island");
        Assert.That(GamePageRenderer.Render(t, faces: null), Is.EqualTo(plain));
        Assert.That(GamePageRenderer.Render(t, faces: Faces()), Is.EqualTo(plain));

        var stranger = new CardFace("Some Other Card", "{1}", "Artifact", [], null, null);
        Assert.That(GamePageRenderer.Render(t, faces: Faces(stranger)), Is.EqualTo(plain),
            "a face no line names changes nothing");
    }

    /// The box sits above and to the right of the name (#227): off the cursor, off the
    /// line the name is on, and off the lines below it — the ones read and hovered
    /// next, which a box below the name hid and blocked. Below only when the room above
    /// is the smaller side and too small for the box; and on whichever side, the box is
    /// capped to the room there and scrolls, so it never slides over the name's line the
    /// way the old viewport clamp did. The geometry was driven with a real pointer in a
    /// browser on the pull request; this pins the rule the script encodes.
    /// </summary>
    [Test]
    public void The_box_sits_above_and_right_of_the_name_and_never_on_its_line()
    {
        var html = GamePageRenderer.Render(Named(), faces: Faces(Hare));
        var script = html[html.LastIndexOf("<script>", StringComparison.Ordinal)..];

        // To the right of the name's end, clamped to the window.
        Assert.That(script, Does.Contain("var left = Math.max(gap, Math.min(r.right + gap, vw - w - gap));"));
        // Room measured on both sides of the line, above preferred.
        Assert.That(script, Does.Contain("var above = r.top - 2 * gap;"));
        Assert.That(script, Does.Contain("var below = vh - r.bottom - 2 * gap;"));
        Assert.That(script, Does.Contain("var up = h <= above || above >= below;"));
        // Capped to the chosen side, so it scrolls rather than grows over the line.
        Assert.That(script, Does.Contain("tip.style.maxHeight = room + 'px';"));
        Assert.That(script, Does.Contain("var top = up ? r.top - gap - h : r.bottom + gap;"));
        // The clamp that slid the box over the name is gone.
        Assert.That(script, Does.Not.Contain("Math.min(top, vh - h - gap)"));
    }

    /// <summary>
    /// A placeholder and the card back are not cards, and a face keyed by one — which
    /// the database could never produce, but a caller could — is ignored.
    /// </summary>
    [Test]
    public void A_placeholder_is_never_wrapped()
    {
        var t = RendererTests.Sample() with
        {
            Events =
            [
                new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1 },
                new GameEvent { Seq = 1, Kind = EventKind.SpellCast, Turn = 1, ActorSeat = 2,
                                SourceName = CardNames.Unknown },
                new GameEvent { Seq = 2, Kind = EventKind.SpellCast, Turn = 1, ActorSeat = 2,
                                SourceName = CardNames.FaceDown },
                new GameEvent { Seq = 3, Kind = EventKind.GameEnd, Detail = "You win the match" },
            ]
        };
        var plain = GamePageRenderer.Render(t);
        var faces = Faces(
            new CardFace(CardNames.Unknown, "", "", [], null, null),
            new CardFace(CardNames.FaceDown, "", "", [], null, null));

        Assert.That(GamePageRenderer.Render(t, faces: faces), Is.EqualTo(plain));
    }

    /// <summary>The markdown export narrates from the same lines and takes no faces.</summary>
    [Test]
    public void The_markdown_export_is_untouched()
    {
        var md = MarkdownRenderer.Render(Named());
        Assert.That(md, Does.Contain("Hare Apparent"));
        Assert.That(md, Does.Not.Contain("<span"));
    }
}
