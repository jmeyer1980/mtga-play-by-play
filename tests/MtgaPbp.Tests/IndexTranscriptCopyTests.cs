using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Copying a sanitized transcript straight from the index, without opening the game.
/// </summary>
/// <remarks>
/// The game page's "Copy without names" already produces the shareable form, but
/// reaching it from the index meant opening the game first (#109). The transcript is
/// not in the index page, and <c>file://</c> blocks the fetch that would get it — so
/// the control follows the Keep stars exactly: rendered disabled with a note that
/// says why, woken by script only when the report is served, and honest about it the
/// whole way. What it copies is the markdown export the build already writes, with
/// the title swapped — the title being the one line of a transcript that names
/// either player.
/// </remarks>
public class IndexTranscriptCopyTests
{
    private const string Mine = "PlayerOne";
    private const string Theirs = "PlayerTwo";

    private static string Page() =>
        IndexRenderer.Render([IndexRenderer.Summarize(RendererTests.Sample())]);

    /// <summary>
    /// The control ships disabled, because from a file it cannot work, and a note
    /// says so — the same honesty the Keep buttons ship with. The name says what it
    /// does, because "copy" alone next to a copy-id button says nothing.
    /// </summary>
    [Test]
    public void Every_row_offers_the_copy_disabled_until_a_server_wakes_it()
    {
        var page = Page();
        var button = Markup.Parse(page).Descendants("button")
            .Single(b => b.Attribute("class")?.Value == "copymd");

        Assert.That(button.Attribute("disabled"), Is.Not.Null);
        Assert.That(button.Attribute("aria-describedby")?.Value, Is.EqualTo("copymd-note"));
        Assert.That(button.Attribute("data-id")?.Value, Is.EqualTo("abc-123"));
        Assert.That(button.Attribute("aria-label")?.Value,
            Is.EqualTo("Copy transcript without names"));

        // The note exists, names the server that makes the button work, and hides
        // once that server is the thing serving the page.
        var note = Markup.Parse(page).Descendants("p")
            .Single(p => p.Attribute("id")?.Value == "copymd-note");
        Assert.That(note.Value, Does.Contain("watch"));
        Assert.That(page, Does.Contain("body.live #copymd-note{display:none}"));
    }

    /// <summary>
    /// It sits in the cell the other copy action lives in, so a row's copy controls
    /// are one place to look rather than two.
    /// </summary>
    [Test]
    public void It_sits_beside_the_other_copy_button()
    {
        var cell = Markup.Parse(Page()).Descendants("td")
            .Single(d => d.Descendants("button")
                .Any(b => b.Attribute("class")?.Value == "copymd"));

        Assert.That(cell.Descendants("button")
            .Count(b => (b.Attribute("class")?.Value ?? "").StartsWith("copy", StringComparison.Ordinal)),
            Is.EqualTo(2));
    }

    /// <summary>
    /// What is copied is the export the build already writes, with its title line
    /// swapped for the words the sanitized copy uses — the renderer's own wording
    /// for not knowing, not a redaction style of this button's own.
    /// </summary>
    [Test]
    public void The_copy_is_the_markdown_export_with_the_title_swapped()
    {
        var page = Page();

        Assert.That(page,
            Does.Contain("fetch('text/' + encodeURIComponent(button.dataset.id) + '.md'"));
        Assert.That(page, Does.Contain("replace(/^# .*/, '# You vs Opponent')"));

        // The words the script writes are the ones the renderer itself reaches for —
        // one fact, kept in step by this assertion rather than by hand.
        Assert.That(TranscriptSummary.AnonymousTitle(RendererTests.Sample()),
            Is.EqualTo("You vs Opponent"));
    }

    /// <summary>
    /// The one-line rule the swap relies on, proven against the export itself: the
    /// title is the only line that names either player, so replacing it is the whole
    /// of sanitizing.
    /// </summary>
    [Test]
    public void The_swapped_export_names_neither_player()
    {
        var md = MarkdownRenderer.Render(RendererTests.Sample());
        Assert.That(md, Does.StartWith($"# {Mine} vs {Theirs}"));

        // The same replacement the script makes: the first line goes, the rest stays.
        var sanitized = "# You vs Opponent" + md[md.IndexOf('\n')..];

        Assert.That(sanitized, Does.Not.Contain(Mine));
        Assert.That(sanitized, Does.Not.Contain(Theirs));
    }

    /// <summary>
    /// Woken only when served, and woken again after every refresh — the fresh rows
    /// arrive disabled, because the build always writes the honest-from-a-file form.
    /// </summary>
    [Test]
    public void The_server_wakes_it_and_a_refresh_wakes_it_again()
    {
        var page = Page();

        Assert.That(page, Does.Contain("function wireCopies()"));

        // Below the served-only line, beside the stars it copies its manners from.
        Assert.That(page.IndexOf("function wireCopies()", StringComparison.Ordinal),
            Is.GreaterThan(page.IndexOf("location.protocol", StringComparison.Ordinal)));

        var refresh = page[page.IndexOf("function refresh()", StringComparison.Ordinal)..page.IndexOf("new EventSource", StringComparison.Ordinal)];
        Assert.That(refresh, Does.Contain("wireCopies();"));
    }

    /// <summary>
    /// A refresh puts the reader back on this control the way it does for the star
    /// and the id copy: the row swap destroys the node that had focus, and losing
    /// your place because a match finished is the failure that block exists to
    /// prevent — this button has to be one of the kinds it recognises.
    /// </summary>
    [Test]
    public void A_refresh_puts_focus_back_on_it()
    {
        var page = Page();
        var refresh = page[page.IndexOf("function refresh()", StringComparison.Ordinal)..page.IndexOf("new EventSource", StringComparison.Ordinal)];

        Assert.That(refresh, Does.Contain("active.classList.contains('copymd') ? 'copymd'"));
    }

    /// <summary>
    /// One fallback copier serves every copy on the page, told what to announce —
    /// the shape the game page already uses — so this button could not grow a
    /// second clipboard path, and the game-ID button still says what it did.
    /// </summary>
    [Test]
    public void It_reports_through_the_shared_copier()
    {
        var page = Page();

        Assert.That(page, Does.Contain("function legacyCopy(text, button, copied)"));
        Assert.That(page, Does.Contain("'Transcript copied without names.'"));
        Assert.That(page, Does.Contain("legacyCopy(id, button, 'Game ID copied.')"));
        Assert.That(page, Does.Contain("'Copy failed.'"));
    }
}
