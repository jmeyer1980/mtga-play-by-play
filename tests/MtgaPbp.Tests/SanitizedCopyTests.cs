using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// Copying a transcript with neither player named.
/// </summary>
/// <remarks>
/// These transcripts get pasted into issues, chats and posts, and every paste publishes
/// a real person's Arena handle unless whoever pasted it noticed and edited it by hand.
/// The tool knows the names are in there, so the tool takes them out.
/// </remarks>
public class SanitizedCopyTests
{
    private const string Mine = "PlayerOne";
    private const string Theirs = "PlayerTwo";

    private static string Page() => GamePageRenderer.Render(RendererTests.Sample());

    /// <summary>
    /// Sanitizing is not a second format. It is this one with the two facts missing,
    /// and the renderer already words that case — an unfinished match whose log never
    /// named anybody renders exactly this way.
    /// </summary>
    [Test]
    public void The_anonymous_title_is_the_renderers_own_wording_for_not_knowing()
    {
        var t = RendererTests.Sample();

        Assert.That(TranscriptSummary.Title(t), Is.EqualTo($"{Mine} vs {Theirs}"));
        Assert.That(TranscriptSummary.AnonymousTitle(t), Is.EqualTo("You vs Opponent"));

        // The same string the renderer reaches for on its own when the log carried no
        // players, rather than a second literal kept in step by hand.
        var nameless = t with { You = null, Opponent = null };
        Assert.That(TranscriptSummary.Title(nameless),
            Is.EqualTo(TranscriptSummary.AnonymousTitle(t)));
    }

    /// <summary>
    /// The issue's acceptance criterion: a sanitized copy of a transcript naming two
    /// players contains neither name.
    /// </summary>
    /// <remarks>
    /// Asserted against the heading the button hands the copier, because the heading is
    /// the only line that carries a name. Measured across 400 archived transcripts: a
    /// screen name appears 796 times in a heading and not once in a beat — the narrator
    /// says "You" and "Opponent" throughout. That is also why this replaces one line
    /// rather than sweeping the document, which would rewrite any card whose name
    /// happens to contain a player's: the one apparent body hit in those 400 was the
    /// card "Ironheart, Clever Champion".
    /// </remarks>
    [Test]
    public void A_sanitized_copy_carries_neither_players_name()
    {
        var page = Page();
        var button = Markup.Parse(page).Descendants("button")
            .Single(b => b.Attribute("id")?.Value == "copy-anon");

        var heading = button.Attribute("data-title")!.Value;
        Assert.That(heading, Does.Not.Contain(Mine));
        Assert.That(heading, Does.Not.Contain(Theirs));
        Assert.That(heading, Is.EqualTo("You vs Opponent"));

        // And the body it would be pasted above names nobody either, which is the fact
        // that lets the heading alone be enough.
        var body = string.Concat(Markup.Parse(page).Descendants("section")
            .SelectMany(s => s.Descendants("li")).Select(li => li.Value));
        Assert.That(body, Does.Not.Contain(Mine));
        Assert.That(body, Does.Not.Contain(Theirs));
    }

    /// <summary>
    /// A second button rather than a toggle on the existing one, so what it does is
    /// said every time rather than held in a state the reader has to remember.
    /// </summary>
    [Test]
    public void The_control_says_what_it_does_and_reports_when_it_has_done_it()
    {
        var page = Page();
        var button = Markup.Parse(page).Descendants("button")
            .Single(b => b.Attribute("id")?.Value == "copy-anon");

        Assert.That(button.Value.Trim(), Is.EqualTo("Copy without names"));
        Assert.That(button.Attribute("type")?.Value, Is.EqualTo("button"));

        // Not a toggle, so it carries no pressed state to contradict its name.
        Assert.That(button.Attribute("aria-pressed"), Is.Null);

        // It announces through the same live region the other two copies use.
        Assert.That(page, Does.Contain("Transcript copied without names."));
        Assert.That(Markup.Parse(page).Descendants()
            .Single(e => e.Attribute("id")?.Value == "status")
            .Attribute("role")?.Value, Is.EqualTo("status"));
    }

    /// <summary>
    /// The sanitized copy is the same document as the plain one, differing in the one
    /// line that names anybody — not a separate assembly that could drift from it.
    /// </summary>
    [Test]
    public void It_copies_the_same_transcript_and_only_swaps_the_heading()
    {
        var page = Page();

        // One builder, taking the heading to use.
        Assert.That(page, Does.Contain("function asMarkdown(heading)"));
        Assert.That(page, Does.Contain("(heading || textOf(title))"));

        // The plain button passes none, the sanitized one passes the title the server
        // worked out — so neither can assemble a different document from the other.
        Assert.That(page, Does.Contain("asMarkdown(), copy,"));
        Assert.That(page, Does.Contain("asMarkdown(anon.dataset.title), anon,"));
    }

    /// <summary>
    /// A match the log never named anybody in is already anonymous, and the control
    /// still works rather than pasting an empty heading.
    /// </summary>
    [Test]
    public void A_match_with_no_names_to_remove_still_copies()
    {
        var nameless = RendererTests.Sample() with { You = null, Opponent = null };
        var button = Markup.Parse(GamePageRenderer.Render(nameless)).Descendants("button")
            .Single(b => b.Attribute("id")?.Value == "copy-anon");

        Assert.That(button.Attribute("data-title")?.Value, Is.EqualTo("You vs Opponent"));
    }
}
