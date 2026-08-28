using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// The index with nobody named: a toggle that masks the Opponent column so the
/// archive's table of contents can be shared without publishing anyone's handle.
/// </summary>
/// <remarks>
/// The transcript pages already take the names out of a copy — see
/// <see cref="SanitizedCopyTests"/> — but the index kept showing every opponent
/// played, so sharing it meant blacking the column out by hand (#103). The index is
/// shared as a screenshot or as visible text, and either carries exactly what the
/// page shows, which is why this is a display toggle on the page rather than a
/// rebuild option in the config.
/// </remarks>
public class AnonymizedIndexTests
{
    private const string Theirs = "PlayerTwo";

    private static string Page() =>
        IndexRenderer.Render([IndexRenderer.Summarize(RendererTests.Sample())]);

    /// <summary>
    /// Nothing changes until somebody asks: the build writes the real name, and the
    /// cell's class is the one handle the script needs to find it later.
    /// </summary>
    [Test]
    public void The_opponent_cell_names_the_opponent_until_asked_otherwise()
    {
        var cell = Markup.Parse(Page()).Descendants("td")
            .Single(d => d.Attribute("class")?.Value == "opp");

        Assert.That(cell.Value, Is.EqualTo(Theirs));
    }

    /// <summary>
    /// The control exists only once script has made it, the same rule the deck
    /// filters and sort buttons follow: with script off the page shows no toggle
    /// rather than one that does nothing.
    /// </summary>
    [Test]
    public void The_toggle_is_made_by_script_and_never_rendered_dead()
    {
        var page = Page();

        // Markup.Parse blanks the script, so what it sees is the page as it loads —
        // and the page as it loads carries no toggle.
        Assert.That(Markup.Parse(page).Descendants()
            .Any(e => e.Attribute("id")?.Value == "names-toggle"), Is.False);

        // A toggle, so the state rides on aria-pressed and the name stays put — the
        // same rule the star and the deck filters follow — and it reports through
        // the live region the filter and sorts already use.
        Assert.That(page, Does.Contain("button.id = 'names-toggle'"));
        Assert.That(page, Does.Contain("'Hide player names'"));
        Assert.That(page, Does.Contain("button.setAttribute('aria-pressed'"));
        Assert.That(page, Does.Contain("'Player names hidden.'"));
        Assert.That(page, Does.Contain("'Player names shown.'"));
    }

    /// <summary>
    /// The masking word is the build's own word for not knowing. A match whose log
    /// never named the opponent already renders "Opponent", so a hidden index reads
    /// exactly like an archive the log never learned the players of, rather than
    /// inventing a redaction style of its own.
    /// </summary>
    [Test]
    public void Hiding_masks_with_the_builds_own_word_for_not_knowing()
    {
        var nameless = RendererTests.Sample() with { Opponent = null };
        Assert.That(IndexRenderer.Summarize(nameless).Opponent, Is.EqualTo("Opponent"));

        // The script writes the same word — one fact, not two literals kept in step.
        Assert.That(Page(), Does.Contain("cell.textContent = 'Opponent'"));
    }

    /// <summary>
    /// Turning it off restores the real names without a reload, which is why the
    /// original rides on the cell while it is masked and leaves when it is not.
    /// </summary>
    [Test]
    public void Turning_it_off_restores_the_names_it_kept()
    {
        var page = Page();

        Assert.That(page, Does.Contain("cell.setAttribute('data-name', cell.textContent)"));
        Assert.That(page, Does.Contain("cell.textContent = cell.getAttribute('data-name')"));
        Assert.That(page, Does.Contain("cell.removeAttribute('data-name')"));
    }

    /// <summary>
    /// The choice outlives both ways this page replaces itself: a reopen, because it
    /// is remembered in localStorage, and a live refresh, because the fresh rows
    /// arrive naming everybody and the masking is done to them again.
    /// </summary>
    [Test]
    public void The_choice_survives_a_reopen_and_a_live_refresh()
    {
        var page = Page();

        Assert.That(page, Does.Contain("localStorage.getItem('hide-names')"));
        Assert.That(page, Does.Contain("localStorage.setItem('hide-names'"));

        // The refresh path itself reapplies, not just first load: everything between
        // the function and the event source that calls it is the swap the fresh rows
        // arrive through.
        var refresh = page[page.IndexOf("function refresh()", StringComparison.Ordinal)
            ..page.IndexOf("new EventSource", StringComparison.Ordinal)];
        Assert.That(refresh, Does.Contain("applyNames();"));
    }
}
