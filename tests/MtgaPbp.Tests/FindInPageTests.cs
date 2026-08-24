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
        var html = IndexRenderer.Render([Row()]);

        Assert.That(html, Does.Not.Contain("""<span class="vh" aria-hidden="true">"""),
            "a span hidden from sight, from selection and from assistive technology is "
            + "reachable only by find-in-page, which cannot show it");
    }

    /// <summary>
    /// The accessible name has to survive intact. It lives on the visible glyph via
    /// role="img" — a label on a bare span is discarded, which #46 found the expensive
    /// way — and a listening test across five techniques settled on this one (#61).
    /// </summary>
    [Test]
    public void The_spoken_form_still_rides_on_the_visible_text()
    {
        var html = IndexRenderer.Render([Row()]);

        Assert.That(html, Does.Contain("""<span role="img" aria-label="11 minutes 12 seconds">11m 12s</span>"""));
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
