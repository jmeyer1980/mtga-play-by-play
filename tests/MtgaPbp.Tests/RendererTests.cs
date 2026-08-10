using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class RendererTests
{
    internal static Transcript Sample(bool incomplete = false) => new(
        "abc-123", 1786326812781, 1786327812781, "Ladder",
        new PlayerInfo(1, "ME", "PlayerOne", "SteamWindows"),
        new PlayerInfo(2, "THEM", "PlayerTwo", "iPhone"),
        WinningTeamId: 1, GamesWon: 2, GamesLost: 1, Incomplete: incomplete,
        [
            new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1 },
            new GameEvent { Seq = 1, Kind = EventKind.LandPlayed, Turn = 1,
                            ActorSeat = 1, SourceName = "Plains" },
            new GameEvent { Seq = 2, Kind = EventKind.SpellCast, Turn = 1,
                            ActorSeat = 2, SourceName = "Lightning Bolt" },
            new GameEvent { Seq = 3, Kind = EventKind.GameEnd, Detail = "You win the match" },
        ],
        new Dictionary<string, int>(),
        new HashSet<string> { "Plains", "Lightning Bolt" });

    [Test]
    public void Match_times_render_in_the_configured_time_zone()
    {
        var original = TranscriptSummary.DisplayTimeZone;
        try
        {
            TranscriptSummary.DisplayTimeZone = TimeZoneInfo.Utc;
            var utc = TranscriptSummary.Date(Sample());

            // 1786326812781 ms since the epoch is 2026-08-10 01:53:32 UTC.
            Assert.That(utc.ToString("yyyy-MM-dd HH:mm"), Is.EqualTo("2026-08-10 01:53"));
            Assert.That(utc.Offset, Is.EqualTo(TimeSpan.Zero));

            var minus5 = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(-5), "t", "t");
            TranscriptSummary.DisplayTimeZone = minus5;
            Assert.That(TranscriptSummary.Date(Sample()).ToString("yyyy-MM-dd HH:mm"),
                Is.EqualTo("2026-08-09 20:53"));
        }
        finally
        {
            TranscriptSummary.DisplayTimeZone = original;
        }
    }

    // ---------- Task 8: markdown ----------

    [Test]
    public void Markdown_has_a_heading_with_opponent_and_result()
    {
        var md = MarkdownRenderer.Render(Sample());
        Assert.That(md, Does.StartWith("# "));
        Assert.That(md, Does.Contain("PlayerTwo"));
        Assert.That(md, Does.Contain("Won 2-1"));
    }

    [Test]
    public void Markdown_contains_the_beats_not_the_verbose_stream()
    {
        var md = MarkdownRenderer.Render(Sample());
        Assert.That(md, Does.Contain("Plains"));
        Assert.That(md, Does.Contain("Lightning Bolt"));
        Assert.That(md, Does.Not.Contain("unhandled"));
    }

    [Test]
    public void Markdown_flags_a_truncated_match()
    {
        Assert.That(MarkdownRenderer.Render(Sample(incomplete: true)),
            Does.Contain("incomplete"));
    }

    [Test]
    public void Markdown_renders_turn_headers_as_subheadings()
    {
        Assert.That(MarkdownRenderer.Render(Sample()), Does.Contain("## Turn 1"));
    }

    // ---------- Task 9: per-game HTML ----------

    [Test]
    public void GamePage_is_self_contained_with_no_external_requests()
    {
        var html = GamePageRenderer.Render(Sample());
        Assert.That(html, Does.Not.Contain("fetch("));
        Assert.That(html, Does.Not.Contain("<script src="));
        Assert.That(html, Does.Not.Contain("<link rel=\"stylesheet\""));
        Assert.That(html, Does.Not.Contain("http://"));
    }

    [Test]
    public void GamePage_contains_both_densities_and_a_toggle()
    {
        var html = GamePageRenderer.Render(Sample());
        Assert.That(html, Does.Contain("data-density=\"beats\""));
        Assert.That(html, Does.Contain("data-density=\"verbose\""));
        Assert.That(html, Does.Contain("id=\"density-toggle\""));
    }

    [Test]
    public void GamePage_gives_each_turn_an_anchor()
    {
        Assert.That(GamePageRenderer.Render(Sample()), Does.Contain("id=\"t1\""));
    }

    [Test]
    public void GamePage_escapes_html_in_player_names()
    {
        var t = Sample() with { Opponent = new PlayerInfo(2, "X", "<script>bad</script>", "PC") };
        var html = GamePageRenderer.Render(t);
        Assert.That(html, Does.Not.Contain("<script>bad"));
        Assert.That(html, Does.Contain("&lt;script&gt;bad"));
    }

    [Test]
    public void GamePage_has_a_copy_button()
    {
        var html = GamePageRenderer.Render(Sample());
        Assert.That(html, Does.Contain("id=\"copy-button\""));
    }

    [Test]
    public void GamePage_copy_reads_the_visible_density_and_not_the_controls()
    {
        var html = GamePageRenderer.Render(Sample());

        // Copy must gather from the transcript sections, never from the header, so
        // the toggle and copy button labels cannot end up in the clipboard.
        Assert.That(html, Does.Contain("section[data-density]"));
        Assert.That(html, Does.Contain("h2, p.beat"));
        Assert.That(html, Does.Not.Contain("copy-button').textContent"),
            "the button's own label must not be part of the copied text");
    }

    [Test]
    public void GamePage_copy_falls_back_when_the_clipboard_api_is_unavailable()
    {
        // file:// is not a secure context in every browser, so navigator.clipboard
        // can be absent or reject. Failing silently would look like a broken button.
        var html = GamePageRenderer.Render(Sample());
        Assert.That(html, Does.Contain("execCommand"));
        Assert.That(html, Does.Contain("Copy failed"));
    }

    // ---------- Task 10: index ----------

    [Test]
    public void Summarize_extracts_the_searchable_fields()
    {
        var s = IndexRenderer.Summarize(Sample());
        Assert.That(s.MatchId, Is.EqualTo("abc-123"));
        Assert.That(s.Opponent, Is.EqualTo("PlayerTwo"));
        Assert.That(s.Result, Is.EqualTo("Won 2-1"));
        Assert.That(s.Cards, Does.Contain("Lightning Bolt"));
        Assert.That(s.EventName, Is.EqualTo("Ladder"));
    }

    [Test]
    public void Index_embeds_data_rather_than_fetching_it()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        Assert.That(html, Does.Not.Contain("fetch("));
        Assert.That(html, Does.Contain("id=\"data\""));
        Assert.That(html, Does.Contain("PlayerTwo"));
    }

    [Test]
    public void Index_renders_rows_statically_so_the_page_works_without_script()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        // Content must be in the markup, not assembled by JS, so find-in-page works.
        Assert.That(html, Does.Contain("<tr data-search="));
        Assert.That(html, Does.Contain("<td>Ladder</td>"));
        Assert.That(html, Does.Contain("lightning bolt"), "cards belong in the search haystack");
    }

    [Test]
    public void Index_links_to_each_game_page()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        Assert.That(html, Does.Contain("games/abc-123.html"));
    }

    [Test]
    public void Index_sorts_most_recent_first()
    {
        var older = Sample() with { MatchId = "old", StartedAtMs = 1_000_000_000_000 };
        var newer = Sample() with { MatchId = "new", StartedAtMs = 2_000_000_000_000 };
        var html = IndexRenderer.Render(
            [IndexRenderer.Summarize(older), IndexRenderer.Summarize(newer)]);

        Assert.That(html.IndexOf("games/new.html", StringComparison.Ordinal),
            Is.LessThan(html.IndexOf("games/old.html", StringComparison.Ordinal)));
    }

    [Test]
    public void Index_has_a_search_box()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        Assert.That(html, Does.Contain("id=\"q\""));
    }

    [Test]
    public void Index_renders_an_empty_archive_without_crashing()
    {
        Assert.That(IndexRenderer.Render([]), Does.Contain("No games"));
    }
}
