using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Ctrl+F used to match text that exists only inside a clipped span. Measured on a
/// rendered index of 794 matches: 1,719 clipped spans, of which "minutes" appeared 712
/// times and was visible zero times. Driving the browser showed the match is worse than
/// invisible — `window.find` returns true and scrolls the page nearly three thousand
/// pixels to a place with nothing highlighted, because #30 made the twins unselectable
/// (#34).
/// </summary>
public class FindInPageTests
{
    private static MatchSummary Row(TimeSpan? length = null, string? colors = null) =>
        new("m1", "2026-08-24 06:00", 1, "Ladder", "Rival", "Won 1-0", 9, false,
            ["Bilbo Baggins, Burglar"], Length: length ?? TimeSpan.FromSeconds(672),
            Colors: colors);

    /// <summary>
    /// The clipped twin's only job was find-in-page: it is <c>aria-hidden</c>, so no
    /// screen reader ever read it, and <c>user-select: none</c>, so nobody could select
    /// it. Removing it therefore cannot change what anything announces — which is what
    /// makes this fixable without a listening test.
    /// </summary>
    [Test]
    public void The_page_carries_no_clipped_text_that_only_find_in_page_can_reach()
    {
        var clipped = Markup.Parse(IndexRenderer.Render([Row()])).Descendants()
            .Where(e => e.Attribute("class")?.Value == "vh"
                     && e.Attribute("aria-hidden")?.Value == "true")
            .ToList();

        Assert.That(clipped, Is.Empty,
            "an element hidden from sight, from selection and from assistive technology "
            + "is reachable only by find-in-page, which cannot show it");
    }

    /// <summary>
    /// The accessible name has to survive intact. It lives on the visible glyph via
    /// role="img" — a label on a bare span is discarded, which #46 found the expensive
    /// way — and a listening test across five techniques settled on this one (#61).
    /// </summary>
    [Test]
    public void The_spoken_form_still_rides_on_the_visible_text()
    {
        var glyph = Markup.Parse(IndexRenderer.Render([Row()])).Descendants("span")
            .Single(e => e.Attribute("aria-label")?.Value == "11 minutes 12 seconds");

        // role="img" is what makes the label apply at all — the same label on a bare
        // span is discarded, which #46 found the expensive way.
        Assert.That(glyph.Attribute("role")?.Value, Is.EqualTo("img"));

        // Read through the cell, not the glyph: Markup.Spoken substitutes a label for a
        // child it finds, which is what a synthesiser does when it reaches the cell.
        // Asked of the labelled element itself it reads straight through to the text
        // underneath, which is the one thing this must not conclude.
        var cell = glyph.Parent!;
        Assert.That(Markup.Clipboard(cell), Is.EqualTo("11m 12s"), "the eye gets the short form");
        Assert.That(Markup.Spoken(cell), Is.EqualTo("11 minutes 12 seconds"), "the ear gets the words");
    }

    /// <summary>
    /// With the twin gone, the filter is the only way to search a duration — so it has
    /// to know both forms, the way it already knows both forms of the deck colours so
    /// that "wu" and "blue" find the same rows.
    /// </summary>
    [TestCase("11 minutes 12 seconds", Description = "the spoken form")]
    [TestCase("11m 12s", Description = "the form on screen")]
    public void A_match_is_searchable_by_how_long_it_ran(string term)
    {
        var html = IndexRenderer.Render([Row()]);
        var haystack = Markup.Parse(html).Descendants("tr")
            .Select(tr => tr.Attribute("data-search")?.Value)
            .Single(v => v is not null)!;

        Assert.That(haystack, Does.Contain(term.ToLowerInvariant()));
    }

    [Test]
    public void The_opponents_commander_is_shown_and_searchable()
    {
        var html = IndexRenderer.Render(
            [Row() with { OpponentCommanders = ["Kinnan, Bonder Prodigy"] }]);

        var cell = Markup.Parse(html).Descendants("td")
            .Single(e => e.Attribute("class")?.Value == "oppdeck");
        Assert.That(Markup.Clipboard(cell), Is.EqualTo("Kinnan, Bonder Prodigy"));

        var haystack = Markup.Parse(html).Descendants("tr")
            .Select(tr => tr.Attribute("data-search")?.Value)
            .Single(v => v is not null)!;
        Assert.That(haystack, Does.Contain("kinnan, bonder prodigy"));
    }

    [Test]
    public void The_against_table_carries_its_own_id()
    {
        // Breakdown used to derive the id from a two-way flag, so the third table
        // reused "by-format" — two elements with one id is invalid HTML and whichever
        // selector relied on it found only the first.
        var html = IndexRenderer.Render(
            [Row() with { OpponentCommanders = ["Kinnan, Bonder Prodigy"] }]);

        Assert.That(html.Split("id=\"against\"").Length - 1, Is.EqualTo(1));
        Assert.That(html.Split("id=\"by-format\"").Length - 1, Is.EqualTo(1));
    }

    [Test]
    public void A_match_with_no_commander_recorded_shows_an_empty_deck_cell()
    {
        var html = IndexRenderer.Render([Row()]);
        var cell = Markup.Parse(html).Descendants("td")
            .Single(e => e.Attribute("class")?.Value == "oppdeck");
        Assert.That(Markup.Clipboard(cell), Is.Empty,
            "absence means none recorded, and the cell must not claim otherwise");
    }

    /// <summary>A match whose length the log never recorded contributes no length terms.</summary>
    [Test]
    public void A_match_with_no_recorded_length_is_not_searchable_by_one()
    {
        var html = IndexRenderer.Render([Row() with { Length = null }]);
        var haystack = Markup.Parse(html).Descendants("tr")
            .Select(tr => tr.Attribute("data-search")?.Value)
            .Single(v => v is not null)!;

        Assert.That(haystack, Does.Not.Contain("minutes"));
    }
}
