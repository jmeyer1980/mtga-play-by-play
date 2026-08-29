using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The game page with nobody named on screen: a toggle that masks the heading and
/// the tab title, so a screenshot of a transcript can be shared without publishing
/// anyone's handle.
/// </summary>
/// <remarks>
/// The copy buttons already have a nameless form — see <see cref="SanitizedCopyTests"/>
/// — and the index grew a display toggle for the same reason (#103). What was left was
/// the page itself: its heading and its tab both say who played, and a screenshot
/// carries exactly what the page shows (#106). Names reach this page in two places
/// only, the <c>h1</c> and the <c>title</c>, both fed by the same
/// <see cref="TranscriptSummary.Title"/> — the audit behind the sanitized copy found
/// screen names in headings and never in a beat.
/// </remarks>
public class AnonymizedGamePageTests
{
    private const string Mine = "PlayerOne";
    private const string Theirs = "PlayerTwo";

    private static string Page() => GamePageRenderer.Render(RendererTests.Sample());

    /// <summary>
    /// Nothing changes until somebody asks: the build writes the real names into the
    /// heading and the tab, exactly as before.
    /// </summary>
    [Test]
    public void The_heading_names_both_players_until_asked_otherwise()
    {
        var parsed = Markup.Parse(Page());

        Assert.That(parsed.Descendants("h1").Single().Value,
            Is.EqualTo($"{Mine} vs {Theirs}"));
        Assert.That(parsed.Descendants("title").Single().Value,
            Is.EqualTo($"{Mine} vs {Theirs}"));
    }

    /// <summary>
    /// The control exists only once script has made it, the same rule the index
    /// toggle follows: with script off the page shows no toggle rather than one that
    /// does nothing.
    /// </summary>
    [Test]
    public void The_toggle_is_made_by_script_and_never_rendered_dead()
    {
        var page = Page();

        // Markup.Parse blanks the script, so what it sees is the page as it loads —
        // and the page as it loads carries no toggle.
        Assert.That(Markup.Parse(page).Descendants()
            .Any(e => e.Attribute("id")?.Value == "names-toggle"), Is.False);

        // A toggle, so the state rides on aria-pressed and the name stays put, and
        // it reports through the live region the copy buttons already use.
        Assert.That(page, Does.Contain("names.id = 'names-toggle'"));
        Assert.That(page, Does.Contain("'Hide player names'"));
        Assert.That(page, Does.Contain("names.setAttribute('aria-pressed'"));
        Assert.That(page, Does.Contain("'Player names hidden.'"));
        Assert.That(page, Does.Contain("'Player names shown.'"));
    }

    /// <summary>
    /// The masking words are the ones the sanitized copy already carries — the
    /// server-rendered <c>data-title</c> on the copy button — not a second literal
    /// kept in step by hand. The script never spells the anonymous title itself.
    /// </summary>
    [Test]
    public void Hiding_masks_with_the_copys_own_title()
    {
        var page = Page();

        Assert.That(page, Does.Contain("anon.dataset.title"));

        // The one place "You vs Opponent" appears is the attribute the server wrote;
        // as a script literal it appears nowhere.
        Assert.That(page, Does.Not.Contain("'You vs Opponent'"));
    }

    /// <summary>
    /// Both faces of the page mask and both restore: the heading a screenshot shows
    /// and the tab the browser shows.
    /// </summary>
    [Test]
    public void The_heading_and_the_tab_mask_and_restore_together()
    {
        var page = Page();

        Assert.That(page, Does.Contain("var realHeading = heading.textContent"));
        Assert.That(page, Does.Contain("var realTitle = document.title"));
        Assert.That(page, Does.Contain("heading.textContent = namesHidden ? anon.dataset.title : realHeading"));
        Assert.That(page, Does.Contain("document.title = namesHidden ? anon.dataset.title : realTitle"));
    }

    /// <summary>
    /// One stored choice covers the whole report: the game page reads and writes the
    /// key the index toggle owns, so hiding names on either page hides them on both,
    /// and the choice survives paging between matches — each page reads it on load.
    /// </summary>
    [Test]
    public void The_choice_is_shared_with_the_index()
    {
        var page = Page();

        Assert.That(page, Does.Contain("localStorage.getItem('hide-names')"));
        Assert.That(page, Does.Contain("localStorage.setItem('hide-names'"));

        // The index spells the same key, which is what makes it one choice rather
        // than two that happen to agree today.
        var index = IndexRenderer.Render([IndexRenderer.Summarize(RendererTests.Sample())]);
        Assert.That(index, Does.Contain("localStorage.getItem('hide-names')"));
    }
}
