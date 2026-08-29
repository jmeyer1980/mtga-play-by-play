using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Copying the record off the index, with the option of saying whose it is.
/// </summary>
/// <remarks>
/// Everything else this report exports takes names out, because an opponent never
/// agreed to be published. The player's own handle is the opposite case: on a record
/// shared on purpose it is the point — attribution, not a leak (#108). So the panel
/// gets two copies, nameless by default, and the named one exists only as a second
/// button that says what it does — the same reasoning the game page gives for its
/// pair of copy buttons.
/// </remarks>
public class RecordCopyTests
{
    private const string Mine = "PlayerOne";
    private const string Theirs = "PlayerTwo";

    private static string Page() =>
        IndexRenderer.Render([IndexRenderer.Summarize(RendererTests.Sample())]);

    /// <summary>
    /// The summary now carries whose match it was, because the export has to say so
    /// and the index build never learned it before — the opponent travelled and the
    /// player did not.
    /// </summary>
    [Test]
    public void The_summary_carries_your_own_name()
    {
        Assert.That(IndexRenderer.Summarize(RendererTests.Sample()).You, Is.EqualTo(Mine));
        Assert.That(IndexRenderer.Summarize(RendererTests.Sample() with { You = null }).You,
            Is.Null);
    }

    /// <summary>
    /// The buttons are made by script, because a copy does nothing without it and
    /// the panel ships no control that does nothing — what the build renders is an
    /// anchor carrying the heading the server composed, one fact the script never
    /// has to spell for itself. Two buttons rather than a toggle on one, so what
    /// each does is said every time.
    /// </summary>
    [Test]
    public void The_panel_offers_both_copies_and_the_named_one_knows_the_name()
    {
        var page = Page();

        // As it loads — script blanked — the panel holds the anchor and no button.
        var parsed = Markup.Parse(page);
        var anchor = parsed.Descendants("div")
            .Single(d => d.Attribute("class")?.Value == "statcopy");
        Assert.That(anchor.Attribute("data-title")?.Value, Is.EqualTo($"{Mine}'s record"));
        Assert.That(anchor.Descendants("button"), Is.Empty);

        // What script makes of it.
        Assert.That(page, Does.Contain("make('copy-stats', 'Copy record')"));
        Assert.That(page, Does.Contain("make('copy-stats-named', 'Copy record with my name')"));
    }

    /// <summary>
    /// An archive whose logs never named the player gets no named copy — a control
    /// that cannot do what it says is worse than one that is not there, the same
    /// rule the pager follows at the ends of the archive. The anchor carries no
    /// heading, and the script makes that button only from one.
    /// </summary>
    [Test]
    public void A_log_that_never_named_you_gets_no_named_copy()
    {
        var page = IndexRenderer.Render(
            [IndexRenderer.Summarize(RendererTests.Sample() with { You = null })]);

        var anchor = Markup.Parse(page).Descendants("div")
            .Single(d => d.Attribute("class")?.Value == "statcopy");
        Assert.That(anchor.Attribute("data-title"), Is.Null);

        Assert.That(page, Does.Contain("if (holder.dataset.title)"));
    }

    /// <summary>
    /// The newest name wins, because an account renames forward: the record belongs
    /// to whoever the player is now, not to whoever they were when the archive began.
    /// </summary>
    [Test]
    public void The_newest_name_wins()
    {
        var older = IndexRenderer.Summarize(RendererTests.Sample());
        var newer = older with { MatchId = "newer", SortKey = older.SortKey + 1, You = "NewName" };

        var anchor = Markup.Parse(IndexRenderer.Render([older, newer])).Descendants("div")
            .Single(d => d.Attribute("class")?.Value == "statcopy");

        Assert.That(anchor.Attribute("data-title")?.Value, Is.EqualTo("NewName's record"));
    }

    /// <summary>
    /// The invariant the whole feature rests on: the panel being copied names no
    /// opponent anywhere, so a copy of it cannot leak one under any setting.
    /// </summary>
    [Test]
    public void The_record_panel_names_no_opponent()
    {
        var stats = Markup.Parse(Page()).Descendants("section")
            .Single(s => s.Attribute("id")?.Value == "stats");

        Assert.That(stats.Value, Does.Not.Contain(Theirs));

        // And the copy walks that panel only, so the invariant above is the whole
        // of the guarantee.
        Assert.That(Page(), Does.Contain("stats.querySelectorAll('table.stats, p.note')"));
    }

    /// <summary>
    /// The copy mirrors what the page shows: captions hidden from sight stay out of
    /// it, a note that only explains a control stays out of it, and the build stamp
    /// every report ends with goes in.
    /// </summary>
    [Test]
    public void The_copy_mirrors_the_page()
    {
        var page = Page();

        Assert.That(page, Does.Contain("classList.contains('vh')"));
        Assert.That(page, Does.Contain("'deck-filter-note'"));
        Assert.That(page, Does.Contain("footer.build"));
    }

    /// <summary>
    /// It reports the way every copy on this page reports, through the shared
    /// fallback that is told what to say.
    /// </summary>
    [Test]
    public void It_reports_like_every_other_copy()
    {
        var page = Page();

        Assert.That(page, Does.Contain("'Record copied.'"));
        Assert.That(page, Does.Contain("'Record copied with your name.'"));
        Assert.That(page, Does.Contain("closest('.copyrec')"));
    }

    /// <summary>
    /// The panel swap a live refresh performs destroys these buttons like it
    /// destroys the sort controls, and the reader is put back the same way — by the
    /// id the button keeps across rebuilds.
    /// </summary>
    [Test]
    public void A_refresh_puts_focus_back_on_the_copy()
    {
        var page = Page();
        var refresh = page[page.IndexOf("function refresh()", StringComparison.Ordinal)..page.IndexOf("new EventSource", StringComparison.Ordinal)];

        Assert.That(refresh, Does.Contain("copyrec"));
    }
}
