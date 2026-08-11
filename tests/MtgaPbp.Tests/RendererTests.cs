using System.Text.RegularExpressions;
using System.Xml.Linq;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class RendererTests
{
    /// <summary>
    /// A transcript whose narration collapses into a run, so the "×3" notation and
    /// what a screen reader does with it can be asserted.
    /// </summary>
    internal static Transcript Repeating() => Sample() with
    {
        Events =
        [
            new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1 },
            new GameEvent { Seq = 1, Kind = EventKind.Triggered, Turn = 1,
                            ActorSeat = 1, SourceName = "Hare Apparent" },
            new GameEvent { Seq = 2, Kind = EventKind.Triggered, Turn = 1,
                            ActorSeat = 1, SourceName = "Hare Apparent" },
            new GameEvent { Seq = 3, Kind = EventKind.Triggered, Turn = 1,
                            ActorSeat = 1, SourceName = "Hare Apparent" },
        ]
    };

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
        Assert.That(html, Does.Contain("h2, li.beat"));
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
        Assert.That(html, Does.Contain("id=\"data\""));
        Assert.That(html, Does.Contain("PlayerTwo"));

        // The page may fetch when served by `watch`, but never on file:// — browsers
        // block it there, so anything behind that guard must be pure enhancement.
        var guard = html.IndexOf("location.protocol.indexOf('http')", StringComparison.Ordinal);
        Assert.That(guard, Is.GreaterThan(0), "live features must be protocol-guarded");
        Assert.That(html.IndexOf("fetch(", StringComparison.Ordinal), Is.GreaterThan(guard),
            "no fetch may run before the http guard");
    }

    [Test]
    public void Index_shows_kept_matches_with_a_filled_star()
    {
        var kept = IndexRenderer.Summarize(Sample()) with { Favorite = true };
        var html = IndexRenderer.Render([kept]);

        Assert.That(html, Does.Contain("class=\"star on\""));
        Assert.That(html, Does.Contain("★"));
        Assert.That(html, Does.Contain("data-id=\"abc-123\""));
    }

    [Test]
    public void Index_shows_unkept_matches_with_an_empty_star()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        Assert.That(html, Does.Contain("class=\"star\""));
        Assert.That(html, Does.Contain("☆"));
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

    // ---------- Task 11: usable with a screen reader ----------
    //
    // The premise of the whole project is that a text transcript is the one MTG Arena
    // artefact a screen reader can actually read, so these are load-bearing, not
    // garnish. What they cannot cover is how a given synthesiser sounds; see the
    // per-test notes where the browser or the AT gets the final say.

    private static string IndexHtml(bool incomplete = false) =>
        IndexRenderer.Render([IndexRenderer.Summarize(Sample(incomplete))]);

    private static string GameHtml() => GamePageRenderer.Render(Sample());

    [Test]
    public void Both_pages_are_structurally_well_formed()
    {
        // Parsed, not eyeballed: an unclosed tag, a crossed tag, an unquoted attribute
        // or a stray "<" all become a parse failure here. Assistive technology reads
        // the browser's accessibility tree, which is built from this structure, so
        // markup the parser has to guess at is markup a screen reader gets wrong.
        Assert.DoesNotThrow(() => Markup.Parse(IndexHtml()));
        Assert.DoesNotThrow(() => Markup.Parse(IndexHtml(incomplete: true)));
        Assert.DoesNotThrow(() => Markup.Parse(IndexRenderer.Render([])));
        Assert.DoesNotThrow(() => Markup.Parse(GameHtml()));
        Assert.DoesNotThrow(() => Markup.Parse(GamePageRenderer.Render(Sample(incomplete: true))));
        Assert.DoesNotThrow(() => Markup.Parse(GamePageRenderer.Render(Repeating())));
    }

    [Test]
    public void No_page_repeats_an_element_id()
    {
        // The game page renders every turn twice, once per density. Both copies used
        // to claim id="t5", which makes the anchor ambiguous and gives the toggle two
        // nodes to point at.
        foreach (var html in new[] { IndexHtml(), GameHtml(), IndexRenderer.Render([]) })
        {
            var ids = Markup.Parse(html).Descendants()
                .Select(e => e.Attribute("id")?.Value)
                .Where(id => id is not null)
                .ToList();

            Assert.That(ids, Is.Unique, $"duplicate id in:\n{html}");
        }
    }

    [Test]
    public void Heading_levels_start_at_one_and_never_skip()
    {
        // Heading level is how a screen reader user builds a mental outline and jumps
        // around the page; a skipped level reads as a missing section.
        foreach (var html in new[] { IndexHtml(), GameHtml() })
        {
            var levels = Markup.Headings(Markup.Parse(html)).ToList();
            Assert.That(levels, Is.Not.Empty);
            Assert.That(levels[0], Is.EqualTo(1), "the first heading must be the h1");
            for (var i = 1; i < levels.Count; i++)
            {
                Assert.That(levels[i], Is.LessThanOrEqualTo(levels[i - 1] + 1),
                    $"h{levels[i - 1]} is followed by h{levels[i]}");
            }
        }
    }

    [Test]
    public void Every_control_has_an_accessible_name()
    {
        foreach (var html in new[] { IndexHtml(), GameHtml() })
        {
            var root = Markup.Parse(html);
            foreach (var control in root.Descendants()
                         .Where(e => e.Name.LocalName is "button" or "input" or "a"))
            {
                Assert.That(Markup.AccessibleName(root, control), Is.Not.Empty,
                    $"<{control.Name.LocalName}> has nothing to announce: {control}");
            }
        }
    }

    [Test]
    public void Index_labels_the_search_box_instead_of_leaning_on_the_placeholder()
    {
        // A placeholder is not a label: it disappears the moment you type, and it is
        // not reliably the accessible name. WCAG 3.3.2 wants a real one.
        var root = Markup.Parse(IndexHtml());
        var label = root.Descendants("label").Single();

        Assert.That(label.Attribute("for")?.Value, Is.EqualTo("q"));
        Assert.That(label.Value.Trim(), Is.Not.Empty);
    }

    [Test]
    public void Index_counter_is_a_live_region_that_starts_out_correct()
    {
        var root = Markup.Parse(IndexHtml());
        var count = root.Descendants().Single(e => e.Attribute("id")?.Value == "count");

        Assert.That(count.Attribute("role")?.Value, Is.EqualTo("status"),
            "the filtered count changes with no focus move, so it has to announce itself");

        // Rendered by the server, not filled in by script: a live region that gains
        // its first text after load announces that text, and the count also has to be
        // right with JavaScript off. The script only rewrites it when it differs.
        Assert.That(count.Value, Does.Contain("1 of 1 shown"));
        Assert.That(IndexHtml(), Does.Contain("if (count.textContent !== text)"));
    }

    [Test]
    public void Index_table_header_cells_are_scoped_and_none_are_blank()
    {
        var root = Markup.Parse(IndexHtml());
        var columns = root.Descendants("thead").Single().Descendants("th").ToList();

        Assert.That(columns, Has.Count.EqualTo(6));
        foreach (var th in columns)
        {
            Assert.That(th.Attribute("scope")?.Value, Is.EqualTo("col"));
            // The star column used to be an empty <th>, so every star button was
            // announced under a column with no name.
            Assert.That(th.Value.Trim(), Is.Not.Empty);
        }

        // The date identifies the row, so moving across a row announces which match
        // you are in rather than five values with no subject.
        var rowHeader = root.Descendants("tbody").Single().Descendants("th").Single();
        Assert.That(rowHeader.Attribute("scope")?.Value, Is.EqualTo("row"));
        Assert.That(rowHeader.Descendants("a").Single().Value, Does.StartWith("2026-"));
    }

    [Test]
    public void Index_table_has_a_caption_that_names_it_and_its_order()
    {
        // Screen readers list and jump between tables by name, and "most recent first"
        // is otherwise information carried only by the visual order.
        var caption = Markup.Parse(IndexHtml()).Descendants("caption").Single();
        Assert.That(caption.Value, Does.Contain("most recent first"));
    }

    [Test]
    public void Index_star_is_a_toggle_button_whose_state_lives_in_aria_pressed()
    {
        var off = Markup.Star(IndexRenderer.Render([IndexRenderer.Summarize(Sample())]));
        var on = Markup.Star(IndexRenderer.Render(
            [IndexRenderer.Summarize(Sample()) with { Favorite = true }]));

        Assert.That(off.Attribute("aria-pressed")?.Value, Is.EqualTo("false"));
        Assert.That(on.Attribute("aria-pressed")?.Value, Is.EqualTo("true"));

        // "☆" announces as "white star" at best and as nothing at worst, so the name
        // is stated and the glyph is taken out of the accessibility tree entirely.
        Assert.That(off.Attribute("aria-label")?.Value, Does.Contain("Keep"));
        Assert.That(off.Attribute("aria-label")?.Value, Does.Contain("PlayerTwo"),
            "93 buttons all called \"Keep\" are 93 buttons you cannot tell apart");
        Assert.That(off.Elements("span").Single().Attribute("aria-hidden")?.Value,
            Is.EqualTo("true"));
    }

    [Test]
    public void Index_star_ships_disabled_and_explains_why()
    {
        // Opened from file:// there is no server to record the change, so the button
        // cannot work. Disabled and described is honest; a button that swallows every
        // click looks broken to everyone and silently to a screen reader user.
        var html = IndexHtml();
        var root = Markup.Parse(html);
        var star = Markup.Star(html);

        Assert.That(star.Attribute("disabled")?.Value, Is.EqualTo("disabled"));

        var note = star.Attribute("aria-describedby")?.Value;
        Assert.That(note, Is.EqualTo("keep-note"));
        Assert.That(
            root.Descendants().Single(e => e.Attribute("id")?.Value == note).Value,
            Does.Contain("read-only"));
    }

    [Test]
    public void Index_star_only_becomes_operable_behind_the_http_guard()
    {
        var html = IndexHtml();
        var guard = html.IndexOf("location.protocol.indexOf('http')", StringComparison.Ordinal);

        Assert.That(html.IndexOf("b.disabled = false", StringComparison.Ordinal),
            Is.GreaterThan(guard), "the star may only be enabled where the server exists");

        // A description that says the control is unavailable must not survive the
        // control becoming available — aria-describedby reads hidden targets too.
        Assert.That(html, Does.Contain("b.removeAttribute('aria-describedby')"));
    }

    [Test]
    public void Index_result_still_reads_as_a_win_or_a_loss_without_colour()
    {
        // .win/.loss are the suspect for WCAG 1.4.1, but the cell text carries the
        // outcome on its own, so the colour is redundant rather than load-bearing.
        // This test exists to keep it that way.
        var won = Markup.Parse(IndexRenderer.Render([IndexRenderer.Summarize(Sample())]))
            .Descendants("td").Single(td => td.Attribute("class")?.Value == "win");

        Assert.That(won.Value, Does.StartWith("Won"));

        var lost = Sample() with { WinningTeamId = 2 };
        var cell = Markup.Parse(IndexRenderer.Render([IndexRenderer.Summarize(lost)]))
            .Descendants("td").Single(td => td.Attribute("class")?.Value == "loss");

        Assert.That(cell.Value, Does.StartWith("Lost"));
    }

    [Test]
    public void Index_marks_an_incomplete_match_in_words_not_only_an_asterisk()
    {
        // "Lost 0-1 star" is not a sentence, and an asterisk with no key on the page
        // is not information for anyone.
        var root = Markup.Parse(IndexHtml(incomplete: true));
        var cell = root.Descendants("td")
            .Single(td => td.Attribute("class")?.Value is "win" or "loss");

        Assert.That(cell.Elements("span").First().Attribute("aria-hidden")?.Value,
            Is.EqualTo("true"));
        Assert.That(Markup.Spoken(cell), Is.EqualTo("Won 2-1 (incomplete)"));
        Assert.That(cell.Value, Does.Contain("*"), "the asterisk still has to be there to look at");
        Assert.That(
            root.Descendants().Single(e => e.Attribute("id")?.Value == "incomplete-note").Value,
            Does.Contain("rotated"));

        // ...and no footnote when there is nothing to footnote.
        Assert.That(IndexHtml(), Does.Not.Contain("incomplete-note"));
    }

    [Test]
    public void GamePage_groups_each_turn_into_a_list_that_keeps_its_role()
    {
        var root = Markup.Parse(GameHtml());
        var beats = root.Descendants("section")
            .Single(s => s.Attribute("data-density")?.Value == "beats");
        var list = beats.Descendants("ol").Single();

        // Entering a list tells a screen reader user how many things happened this
        // turn before reading any of them, and turns a turn into something the list
        // quick-keys can step through. Paragraphs give none of that.
        Assert.That(list.Elements("li").Count(), Is.EqualTo(3));

        // Safari drops the list role when list-style is none, which is exactly the
        // styling used here, so the role is stated rather than inferred.
        Assert.That(list.Attribute("role")?.Value, Is.EqualTo("list"));
        Assert.That(GameHtml(), Does.Contain("list-style:none"));

        foreach (var li in list.Elements("li"))
            Assert.That(li.Parent!.Name.LocalName, Is.EqualTo("ol"));
    }

    [Test]
    public void GamePage_turn_anchors_stay_unique_across_the_two_densities()
    {
        var root = Markup.Parse(GameHtml());
        var headings = root.Descendants("h2").ToList();

        Assert.That(headings, Has.Count.EqualTo(2), "one turn, rendered in both densities");
        Assert.That(headings[0].Attribute("id")?.Value, Is.EqualTo("t1"),
            "the density that is visible by default keeps the short anchor");
        Assert.That(headings[1].Attribute("id")?.Value, Is.EqualTo("v-t1"));
    }

    [Test]
    public void GamePage_spells_out_a_collapsed_run_for_screen_readers()
    {
        // "×" is skipped outright at most default punctuation levels, which turns
        // "triggers ×3" into "triggers 3" — indistinguishable from a turn number.
        var root = Markup.Parse(GamePageRenderer.Render(Repeating()));
        var li = root.Descendants("li").First();

        var glyph = li.Elements("span").Single(s => s.Attribute("class")?.Value == "run");
        Assert.That(glyph.Attribute("aria-hidden")?.Value, Is.EqualTo("true"));
        Assert.That(glyph.Value, Does.Contain("×3"));

        Assert.That(Markup.Spoken(li), Is.EqualTo("Hare Apparent triggers, 3 times in a row"));
        Assert.That(li.Value, Does.Contain("×3"), "the glyph still has to be there to look at");

        // A line with no run must stay a plain string — no wrapper, nothing extra.
        Assert.That(Markup.Parse(GameHtml()).Descendants("li").First().Elements(),
            Is.Empty);
    }

    [Test]
    public void Hiding_a_glyph_never_swallows_the_word_boundary()
    {
        // Taking a decorative span out of the accessibility tree takes the whitespace
        // inside it too. "93 games archived · live updating" hid the dot along with
        // both its spaces and arrived as "archivedlive updating", which a synthesiser
        // then tries to pronounce as a word. Every seam needs a break on one side.
        foreach (var html in new[]
                 {
                     IndexHtml(), IndexHtml(incomplete: true),
                     GameHtml(), GamePageRenderer.Render(Repeating()),
                 })
        {
            foreach (var (before, after) in Markup.Seams(Markup.Parse(html)))
            {
                Assert.That(char.IsLetterOrDigit(before ?? ' ') &&
                            char.IsLetterOrDigit(after ?? ' '), Is.False,
                    $"hiding a glyph joins '{before}' to '{after}'");
            }
        }
    }

    [Test]
    public void GamePage_gives_the_middle_dot_separator_something_to_say()
    {
        // "Ladder · 2026-08-10 10:45 · Won 2-1 · 1 turns" is four fields to a sighted
        // reader and one run-on number to a synthesiser that skips U+00B7. The comma
        // is what actually produces the pause.
        var sub = Markup.Parse(GameHtml()).Descendants("p")
            .Single(p => p.Attribute("class")?.Value == "sub");

        Assert.That(sub.Elements("span").Where(s => s.Attribute("aria-hidden") is not null)
            .Select(s => s.Value), Is.All.EqualTo(" · "));
        Assert.That(sub.Elements("span").Where(s => s.Attribute("class")?.Value == "vh")
            .Select(s => s.Value), Is.All.EqualTo(", "));

        // Turn headings carry life totals through the same separator.
        var scored = Sample() with
        {
            Events =
            [
                new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1,
                                LifeSeat1 = 20, LifeSeat2 = 17 },
            ]
        };
        var heading = Markup.Parse(GamePageRenderer.Render(scored)).Descendants("h2").First();

        Assert.That(heading.Elements("span").Count(s => s.Attribute("class")?.Value == "vh"),
            Is.EqualTo(1));
        Assert.That(Markup.Spoken(heading), Does.Contain("(You 20, Opponent 17)"));
        Assert.That(heading.Value, Does.Contain("(You 20 · "),
            "the dot still has to be there to look at");
    }

    [Test]
    public void GamePage_copy_leaves_the_screen_reader_only_text_out_of_the_clipboard()
    {
        // The spelled-out repeat count exists for speech, not for paste; the pasted
        // transcript should match what the markdown export produces.
        var html = GamePageRenderer.Render(Repeating());
        Assert.That(html, Does.Contain("clone.querySelectorAll('.vh')"));
        Assert.That(html, Does.Contain("h2, li.beat, li.board"));
    }

    [Test]
    public void GamePage_announces_which_density_it_switched_to()
    {
        // The button's label names the next action, which ARIA says to do instead of
        // aria-pressed, not as well as. What a label cannot do is report that the
        // whole page just changed under you.
        var html = GameHtml();
        var status = Markup.Parse(html).Descendants()
            .Single(e => e.Attribute("id")?.Value == "status");

        Assert.That(status.Attribute("role")?.Value, Is.EqualTo("status"));
        Assert.That(html, Does.Contain("Verbose transcript shown."));
        Assert.That(html, Does.Contain("Readable transcript shown."));
        Assert.That(
            Markup.Parse(html).Descendants().Any(e => e.Attribute("aria-pressed") is not null),
            Is.False, "a toggle either renames itself or reports aria-pressed, never both");
    }

    [Test]
    public void GamePage_names_both_transcript_regions()
    {
        var sections = Markup.Parse(GameHtml()).Descendants("section").ToList();
        Assert.That(sections.Select(s => s.Attribute("aria-label")?.Value),
            Is.EqualTo(new[] { "Readable transcript", "Verbose transcript" }));

        // A bare `hidden` is fine HTML but the value is spelled out so the page stays
        // parseable by the strict check above.
        Assert.That(sections[1].Attribute("hidden")?.Value, Is.EqualTo("hidden"));
        Assert.That(sections[0].Attribute("hidden"), Is.Null);
    }

    [Test]
    public void Both_pages_offer_a_main_landmark_and_a_visible_focus_ring()
    {
        foreach (var html in new[] { IndexHtml(), GameHtml() })
        {
            var main = Markup.Parse(html).Descendants("main").Single();

            // The page heading and every control live inside the landmark, so "jump to
            // main content" does not skip past them. On the game page that means the
            // <header> is nested in <main>, which also stops it claiming the banner
            // role it would have as a child of <body>.
            Assert.That(main.Descendants("h1").Count(), Is.EqualTo(1));
            Assert.That(main.Descendants("button").Any(), Is.True);

            // Keyboard operability is worth nothing if you cannot see where you are;
            // :focus-visible rather than :focus so mouse clicks stay quiet.
            Assert.That(html, Does.Contain(":focus-visible{outline:2px solid currentColor"));
        }
    }

    [Test]
    public void Foreground_colours_clear_the_contrast_threshold_on_both_canvases()
    {
        // `color-scheme: light dark` means two backdrops: #fff in light, and a canvas
        // between #121212 (Chrome) and #1e1e1e (Safari) in dark. The lighter dark
        // canvas is the worse case for light-on-dark text, so it is the one asserted.
        const string light = "#ffffff";
        const string dark = "#1e1e1e";
        var index = IndexHtml();

        foreach (var (colour, backdrop) in new[]
                 {
                     ("#137333", light),  // .win          5.95:1, was #2a2 at 3.07:1
                     ("#4ade80", dark),   // .win  dark    9.57:1
                     ("#666666", light),  // .star         5.74:1, was opacity .35 at 2.44:1
                     ("#9a9a9a", dark),   // .star dark    5.92:1, was 3.21:1
                     ("#8a6100", light),  // .star.on      5.54:1, was #e8b923 at 1.84:1
                     ("#f2c14a", dark),   // .star.on dark 9.93:1
                 })
        {
            Assert.That(Contrast.Ratio(colour, backdrop), Is.GreaterThanOrEqualTo(4.5),
                $"{colour} on {backdrop}");
            Assert.That(index, Does.Contain(colour), $"{colour} is not actually shipped");
        }

        // The incomplete-match rule is a graphic, so 1.4.11's 3:1 applies, not 4.5:1.
        var game = GameHtml();
        Assert.That(Contrast.Ratio("#a35b00", light), Is.GreaterThanOrEqualTo(3.0));
        Assert.That(Contrast.Ratio("#e0a33a", dark), Is.GreaterThanOrEqualTo(3.0));
        Assert.That(GamePageRenderer.Render(Sample(incomplete: true)), Does.Contain("#a35b00"));
        Assert.That(game, Does.Contain("#e0a33a"));
    }

    [Test]
    public void Dimming_by_opacity_still_clears_the_contrast_threshold()
    {
        // `opacity` composites the text against whatever is behind it, so it really
        // does cut contrast — but these all sit on a 21:1 base, and the result stays
        // well over 4.5:1. They are left alone deliberately rather than churned.
        Assert.That(Contrast.Ratio("#000000", "#ffffff", 0.65), Is.GreaterThanOrEqualTo(4.5));
        Assert.That(Contrast.Ratio("#ffffff", "#1e1e1e", 0.65), Is.GreaterThanOrEqualTo(4.5));
        Assert.That(Contrast.Ratio("#000000", "#ffffff", 0.60), Is.GreaterThanOrEqualTo(4.5));
        Assert.That(Contrast.Ratio("#ffffff", "#1e1e1e", 0.60), Is.GreaterThanOrEqualTo(4.5));

        // The one that did not survive was compounding: the game page header used to
        // carry opacity:.95, which multiplied with every child's own opacity.
        Assert.That(GameHtml(), Does.Not.Contain("padding-bottom:1rem;margin-bottom:1rem;opacity"));
    }

    [Test]
    public void The_colours_and_dimming_that_failed_are_gone()
    {
        foreach (var html in new[] { IndexHtml(), GameHtml() })
        {
            Assert.That(html, Does.Not.Contain("#2a2"), "3.07:1 on white");
            Assert.That(html, Does.Not.Contain("#e8b923"), "1.84:1 on white");
            Assert.That(html, Does.Not.Contain("opacity:.35"), "2.44:1 on white");
            Assert.That(html, Does.Not.Contain("#c80"), "2.96:1 on white");
        }
    }

    [Test]
    public void Every_pointer_target_is_at_least_the_minimum_size()
    {
        // WCAG 2.2 SC 2.5.8 wants 24x24 CSS px. The star was a bare 16px glyph with
        // .2rem of side padding, which is under it in both directions.
        Assert.That(IndexHtml(), Does.Contain("min-width:1.75rem;min-height:1.75rem"));
        Assert.That(GameHtml(), Does.Contain("button{font:inherit;padding:.3rem .8rem;" +
                                             "cursor:pointer;min-height:1.75rem}"));
    }
}

/// <summary>
/// Structural checks over a generated page, run by a real parser rather than by eye.
/// The templates are deliberately written to stay XML-well-formed — void elements
/// self-close, boolean attributes spell out their value, no named entities beyond the
/// XML five — so <see cref="XDocument"/> can act as the validator with no dependency
/// to restore, which matters for a tool that has to build offline. Script and style
/// bodies are blanked first: their contents are legal HTML but not legal XML.
/// </summary>
internal static class Markup
{
    internal static XElement Parse(string html)
    {
        var stripped = Blank(Blank(html, "script"), "style");
        var start = stripped.IndexOf("<html", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "the page has no <html> element");

        // Whitespace is kept: a browser treats the space in "<span> <span>…" as real
        // text, and dropping it here would hide exactly the word-boundary bugs that
        // hiding a glyph causes.
        return XDocument.Parse(stripped[start..], LoadOptions.PreserveWhitespace).Root!;
    }

    /// <summary>
    /// The text an assistive technology would be handed: everything except the
    /// subtrees marked aria-hidden, which the browser leaves out of the tree it
    /// exposes. <see cref="XElement.Value"/> alone would include the decorative glyphs.
    /// </summary>
    internal static string Spoken(XElement element) =>
        string.Concat(element.Nodes().Select(n => n switch
        {
            XText text => text.Value,
            XElement child when child.Attribute("aria-hidden")?.Value == "true" => "",
            XElement child => Spoken(child),
            _ => ""
        }));

    /// <summary>
    /// Inline elements a synthesiser reads straight through. Anything else is a break
    /// in the spoken flow, so text either side of it was never going to run together.
    /// </summary>
    private static readonly HashSet<string> Inline =
        ["span", "a", "code", "em", "strong", "b", "i", "abbr", "small"];

    /// <summary>
    /// Every point where an aria-hidden subtree is dropped, as the character before it
    /// and the character after it in the remaining spoken text. Hiding a glyph hides
    /// the whitespace inside it too, which is how "archived · live" becomes
    /// "archivedlive" — a seam with a word character on both sides is that bug.
    /// </summary>
    internal static IEnumerable<(char? Before, char? After)> Seams(XElement root)
    {
        var text = new System.Text.StringBuilder();
        var marks = new List<int>();
        Walk(root, text, marks);

        return marks.Select(i => (
            i > 0 ? text[i - 1] : (char?)null,
            i < text.Length ? text[i] : (char?)null));
    }

    private static void Walk(XElement element, System.Text.StringBuilder text, List<int> marks)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText t:
                    text.Append(t.Value);
                    break;
                case XElement e when e.Attribute("aria-hidden")?.Value == "true":
                    marks.Add(text.Length);
                    break;
                case XElement e when Inline.Contains(e.Name.LocalName):
                    Walk(e, text, marks);
                    break;
                case XElement e:
                    text.Append('\n');
                    Walk(e, text, marks);
                    text.Append('\n');
                    break;
            }
        }
    }

    internal static IEnumerable<int> Headings(XElement root) =>
        root.Descendants()
            .Where(e => e.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            .Select(e => e.Name.LocalName[1] - '0');

    internal static XElement Star(string html) =>
        Parse(html).Descendants("button")
            .Single(b => b.Attribute("class")?.Value.StartsWith("star", StringComparison.Ordinal)
                         == true);

    /// <summary>
    /// The subset of the accessible-name algorithm these pages actually rely on:
    /// aria-label wins, then a label pointing at the control, then its own text.
    /// </summary>
    internal static string AccessibleName(XElement root, XElement control)
    {
        var label = control.Attribute("aria-label")?.Value;
        if (!string.IsNullOrWhiteSpace(label)) return label.Trim();

        var id = control.Attribute("id")?.Value;
        if (id is not null)
        {
            var forControl = root.Descendants("label")
                .FirstOrDefault(l => l.Attribute("for")?.Value == id);
            if (forControl is not null) return Spoken(forControl).Trim();
        }
        return Spoken(control).Trim();
    }

    private static string Blank(string html, string tag) =>
        Regex.Replace(html, $"<{tag}\\b[^>]*>.*?</{tag}>", $"<{tag}></{tag}>",
            RegexOptions.Singleline);
}

/// <summary>WCAG 2.x relative-luminance contrast, including CSS opacity compositing.</summary>
internal static class Contrast
{
    internal static double Ratio(string foreground, string background, double opacity = 1.0)
    {
        var (fr, fg, fb) = Rgb(foreground);
        var (br, bg, bb) = Rgb(background);

        // `opacity` blends the already-encoded sRGB values against the backdrop, which
        // is why dimming text with it is a contrast change and not just a visual one.
        var blended = Luminance(
            opacity * fr + (1 - opacity) * br,
            opacity * fg + (1 - opacity) * bg,
            opacity * fb + (1 - opacity) * bb);
        var behind = Luminance(br, bg, bb);

        return (Math.Max(blended, behind) + 0.05) / (Math.Min(blended, behind) + 0.05);
    }

    private static (double R, double G, double B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        return (Convert.ToInt32(h[..2], 16),
                Convert.ToInt32(h.Substring(2, 2), 16),
                Convert.ToInt32(h.Substring(4, 2), 16));
    }

    private static double Luminance(double r, double g, double b) =>
        0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);

    private static double Channel(double value)
    {
        var c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
