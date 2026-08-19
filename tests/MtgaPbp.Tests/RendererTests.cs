using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Globalization;
using MtgaPbp.Core;
using MtgaPbp.Render;
using NUnit.Framework;

namespace MtgaPbp.Tests;

public class RendererTests
{
    /// <summary>
    /// A transcript whose narration collapses into a run, so the "×3" notation and
    /// what a screen reader does with it can be asserted. No opening: this fixture
    /// exists to put one specific line first, and an opening would sit in front of it.
    /// </summary>
    internal static Transcript Repeating() => Sample(opening: false) with
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

    /// <summary>
    /// A transcript carrying a before-and-after statline, so what a synthesiser does
    /// with the arrow can be asserted. No opening, for the same reason
    /// <see cref="Repeating"/> has none.
    /// </summary>
    internal static Transcript Buffed() => Sample(opening: false) with
    {
        Events =
        [
            new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1 },
            new GameEvent { Seq = 1, Kind = EventKind.SpellCast, Turn = 1, ActorSeat = 1,
                            SourceName = "Ethereal Armor", TargetName = "Rabbit A (1/1 → 6/6)" },
        ]
    };

    /// <summary>
    /// A transcript with a turn slow enough to be worth remarking on, so the duration on
    /// a turn header and the note explaining it face the structural and accessibility
    /// sweeps rather than only their own tests. Turn two runs 1m 48s and turn three 22s,
    /// which is also the pair that proves only the slow one is marked.
    /// </summary>
    internal static Transcript Timed() => Sample(opening: false) with
    {
        Events =
        [
            new GameEvent { Seq = 0, Kind = EventKind.TurnStart, Turn = 1, ActorSeat = 1,
                            TimestampMs = 1786326812781 },
            new GameEvent { Seq = 1, Kind = EventKind.LandPlayed, Turn = 1,
                            ActorSeat = 1, SourceName = "Plains" },
            new GameEvent { Seq = 2, Kind = EventKind.TurnStart, Turn = 2, ActorSeat = 2,
                            TimestampMs = 1786326812781 + 108_000 },
            new GameEvent { Seq = 3, Kind = EventKind.TurnStart, Turn = 3, ActorSeat = 1,
                            TimestampMs = 1786326812781 + 130_000 },
        ]
    };

    /// <summary>
    /// A gap standing for a game-state update Arena declined to log. Synthetic, but
    /// shaped from the two real occurrences found in Player-prev.log, which reported
    /// 77 and 55 game objects against limits of 50.
    /// </summary>
    internal static LogGap SummarizedGap(long line = 10486) =>
        new(LogGapKind.Summarized, line, GameObjects: 77, Annotations: 3,
            Messages: ["GameStateMessage", "ActionsAvailableReq"]);

    /// <summary>
    /// A four-card decklist, one copy of which never showed up during the match, so the
    /// seen/unseen mark and the singular/plural of "copy" can both be asserted.
    /// </summary>
    internal static IReadOnlyList<DeckEntry> SampleDeck() =>
    [
        new DeckEntry("Hare Apparent", 4, Seen: true),
        new DeckEntry("Plains", 1, Seen: true),
        new DeckEntry("Split Up", 2, Seen: false),
    ];

    /// <summary>
    /// The opening the sample match carries: you win the roll 14 to 3, take the play,
    /// and neither player mulligans. Shaped from the archive's ordinary case, so the
    /// structural and accessibility checks that run over <see cref="Sample"/> cover the
    /// opening markup rather than only the turns.
    /// </summary>
    internal static Opening SampleOpening() => new(
        [new DieRoll(1, 14), new DieRoll(2, 3)],
        FirstPlayerSeat: 1,
        new Dictionary<int, int> { [1] = 0, [2] = 0 });

    internal static Transcript Sample(
        bool incomplete = false, IReadOnlyList<LogGap>? gaps = null,
        IReadOnlyList<DeckEntry>? deck = null, bool opening = true,
        IReadOnlyList<string>? commanders = null, string? colors = null) => new(
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
        new HashSet<string> { "Plains", "Lightning Bolt" },
        new Dictionary<string, int>(),
        gaps ?? [],
        deck ?? [],
        opening ? SampleOpening() : null)
        { Commanders = commanders ?? [], DeckColors = colors };

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

    /// <summary>
    /// The archive's first drawn match rendered as "Lost 0-0": no winning team, and
    /// "no winning team" fell through to the losing branch. A draw is a result of its
    /// own and has to say so — issue #9.
    /// </summary>
    [Test]
    public void Result_says_drew_for_a_drawn_match()
    {
        var t = Sample() with { WinningTeamId = null, GamesWon = 0, GamesLost = 0, Drawn = true };
        Assert.That(TranscriptSummary.Result(t), Is.EqualTo("Drew 0-0"));
    }

    /// <summary>
    /// A Bo3 that reached 1-1 before the match was called still shows the games that
    /// were played — the draw replaces the verdict, not the tally.
    /// </summary>
    [Test]
    public void Result_keeps_the_games_tally_in_a_drawn_match()
    {
        var t = Sample() with { WinningTeamId = null, GamesWon = 1, GamesLost = 1, Drawn = true };
        Assert.That(TranscriptSummary.Result(t), Is.EqualTo("Drew 1-1"));
    }

    /// <summary>
    /// The guard the draw must not regress: a log that stopped early still reads
    /// Unfinished, never Drew — drawn means Arena said so, not "no winner found".
    /// </summary>
    [Test]
    public void Result_still_says_unfinished_when_the_log_stopped_early()
    {
        var t = Sample(incomplete: true) with { WinningTeamId = null };
        Assert.That(TranscriptSummary.Result(t), Is.EqualTo("Unfinished"));
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
        Assert.That(html, Does.Contain("h2, h3, li.beat"));
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

    /// <summary>
    /// The row class used to be a Won-prefix coin flip, so a draw was styled as a
    /// loss. It gets its own state instead: dimmed like a loss — winning stays the
    /// only highlighted result — but never labelled as one.
    /// </summary>
    [Test]
    public void Index_gives_a_draw_its_own_row_state()
    {
        var drawn = Sample() with { WinningTeamId = null, GamesWon = 0, GamesLost = 0, Drawn = true };
        var html = IndexRenderer.Render([IndexRenderer.Summarize(drawn)]);
        var cell = MatchTable(Markup.Parse(html)).Descendants("td")
            .Single(td => td.Attribute("class")?.Value == "draw");
        Assert.That(cell.Value, Does.StartWith("Drew 0-0"));
        Assert.That(html, Does.Not.Contain("Lost 0-0"));
    }

    /// <summary>
    /// The floating pager is plain links, and omits a direction at the ends of the
    /// archive rather than rendering one that goes nowhere.
    /// </summary>
    /// <remarks>
    /// Links, not buttons, and no script: a game page has to work opened straight off
    /// disk where nothing can fetch, and an anchor additionally gives middle-click,
    /// open-in-new-tab and link semantics to a screen reader. "Newer" and "older" rather
    /// than "next" and "previous", because the index lists matches newest first — "next"
    /// there means the older one and the opposite everywhere else.
    /// </remarks>
    [Test]
    public void The_pager_links_to_the_matches_either_side_and_to_the_top()
    {
        var html = GamePageRenderer.Render(Sample(),
            new Neighbours("newer-id", "2026-08-12 10:00", "older-id", "2026-08-11 09:00"));
        var nav = Markup.Parse(html).Descendants("nav").Single();

        Assert.That(nav.Attribute("aria-label")?.Value, Is.EqualTo("Match navigation"));

        var links = nav.Descendants("a").ToList();
        Assert.That(links, Has.Count.EqualTo(3));
        Assert.That(links.Select(a => a.Attribute("href")?.Value),
            Is.EqualTo(new[] { "newer-id.html", "#top", "older-id.html" }));

        // The destination is in the accessible name: "Older" alone tells a screen reader
        // user nothing about where they would land.
        Assert.That(Markup.Spoken(links[0]), Is.EqualTo("Newer match, 2026-08-12 10:00"));
        Assert.That(Markup.Spoken(links[2]), Is.EqualTo("Older match, 2026-08-11 09:00"));

        // Something to land on, and focusable so focus follows the view rather than
        // staying four hundred lines down.
        var h1 = Markup.Parse(html).Descendants("h1").Single();
        Assert.That(h1.Attribute("id")?.Value, Is.EqualTo("top"));
        Assert.That(h1.Attribute("tabindex")?.Value, Is.EqualTo("-1"));
    }

    /// <summary>
    /// Issue 27: the pager showed the neighbour's timestamp and no word, so the arrow
    /// was the only cue to which way through the archive it went — and the index is
    /// sorted newest first, which makes "left" genuinely ambiguous.
    /// </summary>
    /// <remarks>
    /// The word is the visible half and the destination follows it in the hidden half,
    /// which keeps the accessible name the renderer already produced. Both are asserted
    /// here because satisfying one by breaking the other is the obvious wrong turn: a
    /// screen reader was never the reader who was confused.
    /// </remarks>
    [Test]
    public void The_pager_says_which_way_it_goes_where_the_eye_can_see_it()
    {
        var nav = Markup.Parse(GamePageRenderer.Render(Sample(),
                new Neighbours("newer-id", "2026-08-12 10:00", "older-id", "2026-08-11 09:00")))
            .Descendants("nav").Single();
        var links = nav.Descendants("a").ToList();

        // What the eye gets: the direction, and not a date it would have to compare
        // against this page's own to work out which way it is going.
        Assert.That(Markup.Clipboard(links[0]), Does.Contain("Newer"));
        Assert.That(Markup.Clipboard(links[0]), Does.Not.Contain("2026-08-12"));
        Assert.That(Markup.Clipboard(links[2]), Does.Contain("Older"));
        Assert.That(Markup.Clipboard(links[2]), Does.Not.Contain("2026-08-11"));

        // The arrows stay decorative. A synthesiser reads U+2190 as "leftwards arrow",
        // which would land immediately before the word that now follows it.
        //
        // Found by what they are rather than by the attribute under test: selecting them
        // on aria-hidden would make this pass by iterating nothing on the day the
        // attribute goes missing, which is the one day it needs to fail.
        var arrows = links.SelectMany(a => a.Elements("span"))
            .Where(s => s.Value.Trim() is "←" or "→").ToList();

        Assert.That(arrows, Has.Count.EqualTo(2), "one arrow per direction");
        foreach (var arrow in arrows)
            Assert.That(arrow.Attribute("aria-hidden")?.Value, Is.EqualTo("true"), arrow.Value);

        // With styling off the hidden half stops hiding and both halves land in one
        // line, so they have to read as one sentence rather than repeat the word.
        Assert.That(links[0].Value, Is.EqualTo("←Newer match, 2026-08-12 10:00"));
        Assert.That(links[2].Value, Is.EqualTo("Older match, 2026-08-11 09:00→"));
    }

    /// <summary>
    /// A neighbour with no timestamp leaves a whole phrase behind, not a dangling comma.
    /// </summary>
    [Test]
    public void The_pager_still_reads_as_a_phrase_when_a_neighbour_has_no_date()
    {
        var links = Markup.Parse(GamePageRenderer.Render(Sample(),
                new Neighbours("newer-id", null, "older-id", null)))
            .Descendants("nav").Single().Descendants("a").ToList();

        Assert.That(Markup.Spoken(links[0]), Is.EqualTo("Newer match"));
        Assert.That(Markup.Spoken(links[2]), Is.EqualTo("Older match"));
    }

    [Test]
    public void The_pager_omits_a_direction_that_has_no_match_rather_than_disabling_it()
    {
        // A focusable control that does nothing is worse than one that is not there.
        var oldest = Markup.Parse(GamePageRenderer.Render(Sample(),
            new Neighbours("newer-id", "2026-08-12 10:00", null, null)));
        Assert.That(oldest.Descendants("nav").Single().Descendants("a").Count(),
            Is.EqualTo(2));
        Assert.That(oldest.Descendants("button").Any(b => b.Value.Contains("older")), Is.False);

        // And a page rendered without neighbours at all grows no pager.
        Assert.That(Markup.Parse(GamePageRenderer.Render(Sample())).Descendants("nav"),
            Is.Empty);
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
        Assert.That(MatchTable(Markup.Parse(html)).Descendants("td").Select(td => td.Value),
            Has.One.EqualTo("Ladder"));
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

    // ---------- Issue 24: which deck was played ----------

    /// <summary>
    /// Arena never sends a deck name, so the column says colours. Letters for the eye,
    /// spelled out for a synthesiser — which would otherwise read "WU" as a word.
    /// </summary>
    [Test]
    public void Index_names_the_deck_by_its_colours_twice()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample(colors: "WU"))]);

        Assert.That(ColumnNames(html), Does.Contain("Deck"));
        Assert.That(html, Does.Contain("<span aria-hidden=\"true\">WU</span>"));
        Assert.That(html, Does.Contain("<span class=\"vh\">white and blue</span>"));
    }

    /// <summary>
    /// 101 of the archived matches predate the deck being captured at all. An empty
    /// cell is the only honest thing to put there — "colourless", a dash or a question
    /// mark would each be a claim about a deck nobody has a record of.
    /// </summary>
    [Test]
    public void Index_leaves_the_deck_cell_empty_when_no_deck_was_registered()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);

        Assert.That(ColumnNames(html), Does.Contain("Deck"));
        Assert.That(html, Does.Not.Contain("colourless"));

        // Empty of content. It carries an empty sort key, which is how the comparator
        // is told there is nothing here rather than a blank name that sorts first.
        var cell = MatchTable(Markup.Parse(html)).Descendants("td")
            .Single(td => td.Attribute("class")?.Value == "deck");
        Assert.That(cell.Value, Is.Empty);
        Assert.That(cell.Attribute("data-key")?.Value, Is.Empty);
    }

    [Test]
    public void Index_lets_a_colour_be_searched_by_letter_or_by_name()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample(colors: "WU"))]);
        var haystack = html[html.IndexOf("data-search=\"", StringComparison.Ordinal)..];
        haystack = haystack[..haystack.IndexOf('"', 13)];

        Assert.That(haystack, Does.Contain("wu"));
        Assert.That(haystack, Does.Contain("white and blue"));
    }

    /// <summary>
    /// A row whose deck nobody recorded must not answer a colour search. Filtering on
    /// "white" would otherwise return matches that have no claim to be white.
    /// </summary>
    [Test]
    public void Index_does_not_let_a_deckless_match_answer_a_colour_search()
    {
        var html = IndexRenderer.Render([IndexRenderer.Summarize(Sample())]);
        var haystack = html[html.IndexOf("data-search=\"", StringComparison.Ordinal)..];
        haystack = haystack[..haystack.IndexOf('"', 13)];

        Assert.That(haystack, Does.Not.Contain("white"));
    }

    /// <summary>
    /// The colour has to reach the row from the transcript on its own — the ids it was
    /// derived from are gone by the time a summary is built, so nothing downstream can
    /// recover it if this link is dropped.
    /// </summary>
    [Test]
    public void Summarize_carries_the_deck_colours_through()
    {
        Assert.That(IndexRenderer.Summarize(Sample(colors: "BG")).Colors, Is.EqualTo("BG"));
        Assert.That(IndexRenderer.Summarize(Sample()).Colors, Is.Null);
    }

    // ---------- Task 11: usable with a screen reader ----------
    //
    // The premise of the whole project is that a text transcript is the one MTG Arena
    // artefact a screen reader can actually read, so these are load-bearing, not
    // garnish. What they cannot cover is how a given synthesiser sounds; see the
    // per-test notes where the browser or the AT gets the final say.

    private static string IndexHtml(bool incomplete = false, bool gaps = false, bool deck = false) =>
        IndexRenderer.Render([
            IndexRenderer.Summarize(Sample(incomplete, gaps ? [SummarizedGap()] : null,
                deck: deck ? SampleDeck() : null))
        ]);

    private static string GameHtml() => GamePageRenderer.Render(Sample());

    // Kept apart from GameHtml: the deck adds the first <li> on the page, and the
    // tests that assert what a transcript line looks like index into that.
    private static string GameDeckHtml() =>
        GamePageRenderer.Render(Sample(deck: SampleDeck()));

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
        Assert.DoesNotThrow(() => Markup.Parse(IndexHtml(gaps: true)));
        Assert.DoesNotThrow(() => Markup.Parse(IndexHtml(incomplete: true, gaps: true)));
        Assert.DoesNotThrow(() => Markup.Parse(
            GamePageRenderer.Render(Sample(incomplete: true, gaps: [SummarizedGap()]))));
        Assert.DoesNotThrow(() => Markup.Parse(GamePageRenderer.Render(Repeating())));
        Assert.DoesNotThrow(() => Markup.Parse(GamePageRenderer.Render(Buffed())));
        Assert.DoesNotThrow(() => Markup.Parse(GamePageRenderer.Render(Timed())));
        Assert.DoesNotThrow(() => Markup.Parse(GameDeckHtml()));
    }

    [Test]
    public void No_page_repeats_an_element_id()
    {
        // The game page renders every turn twice, once per density. Both copies used
        // to claim id="t5", which makes the anchor ambiguous and gives the toggle two
        // nodes to point at.
        foreach (var html in new[]
                 {
                     IndexHtml(), IndexHtml(deck: true), IndexHtml(incomplete: true, gaps: true),
                     GameHtml(), IndexRenderer.Render([]),
                     // The warning banners and their footnotes carry ids of their own,
                     // and only appear on the variants that need them.
                     IndexHtml(incomplete: true, gaps: true),
                     GamePageRenderer.Render(Sample(incomplete: true, gaps: [SummarizedGap()])),
                     // The decklist owns an id so the copy button can find it, and so
                     // does the note explaining a turn duration.
                     GameDeckHtml(),
                     GamePageRenderer.Render(Timed()),
                 })
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
        foreach (var html in new[] { IndexHtml(), IndexHtml(deck: true), GameHtml() })
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
        foreach (var html in new[] { IndexHtml(), IndexHtml(deck: true), GameHtml() })
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

    /// <summary>
    /// The matches table specifically. The index also carries the stats panel's tables
    /// above it, so "the only tbody on the page" stopped naming this one.
    /// </summary>
    private static XElement MatchTable(XElement root) =>
        root.Descendants("table").Single(t => t.Attribute("id")?.Value == "rows");

    /// <summary>
    /// The match table's column headings by name. Read through the parser rather than
    /// matched as literal markup, because a heading grows attributes — a sort rule, for
    /// one — and a test that pins the whole tag fails on changes it was never about.
    /// </summary>
    private static IReadOnlyList<string> ColumnNames(string html) =>
        MatchTable(Markup.Parse(html)).Descendants("thead").Single()
            .Descendants("th").Select(th => th.Value.Trim()).ToList();

    // ---------- Issue 13: the record above the table ----------

    /// <summary>
    /// The panel is a real table with scoped headers, rendered by the build. The index
    /// works with JavaScript off and is read with a screen reader, and a record is
    /// tabular data in the plainest sense.
    /// </summary>
    [Test]
    public void The_stats_panel_is_a_real_table_rendered_by_the_build()
    {
        var stats = Markup.Parse(IndexHtml()).Descendants()
            .Single(e => e.Attribute("id")?.Value == "stats");

        Assert.That(stats.Name.LocalName, Is.EqualTo("section"));
        Assert.That(stats.Attribute("aria-labelledby")?.Value, Is.EqualTo("stats-heading"));

        var tables = stats.Descendants("table").ToList();
        Assert.That(tables, Is.Not.Empty);
        foreach (var th in tables.SelectMany(t => t.Descendants("th")))
        {
            Assert.That(th.Attribute("scope")?.Value, Is.AnyOf("col", "row"));
            Assert.That(th.Value.Trim(), Is.Not.Empty);
        }

        // Server-rendered, so the numbers are there with no script at all.
        Assert.That(stats.Descendants("td").Any(td => td.Value.Contains('%')), Is.True);
    }

    /// <summary>
    /// A deck name is plain text in the markup and becomes a filter only once script
    /// upgrades it, because a button that does nothing without script is worse than a
    /// name that never claimed to be one.
    /// </summary>
    [Test]
    public void A_deck_name_is_not_a_control_until_script_makes_it_one()
    {
        var html = IndexHtml(deck: true);
        var stats = Markup.Parse(html).Descendants()
            .Single(e => e.Attribute("id")?.Value == "stats");

        var name = stats.Descendants("span")
            .Single(s => s.Attribute("class")?.Value == "deck-name");
        Assert.That(name.Attribute("data-deck")?.Value, Is.Not.Empty);
        Assert.That(stats.Descendants("button"), Is.Empty, "no inert control ships");

        // And the row it filters to carries the matching token.
        var row = MatchTable(Markup.Parse(html)).Descendants("tr")
            .Single(tr => tr.Attribute("data-search") is not null);
        Assert.That(row.Attribute("data-search")!.Value,
            Does.Contain($"deck:{name.Attribute("data-deck")!.Value}"));

        // The upgrade is in the script, and it is what creates the button. Re-runnable,
        // because a live refresh replaces the panel and the names have to be wired again.
        Assert.That(html, Does.Contain("#stats span.deck-name"));
        Assert.That(html, Does.Contain("wireDeckNames()"));
    }

    /// <summary>
    /// A record is two numbers, and both of them need saying. The first attempt threaded
    /// a spoken " and " between the digits, which named neither number and lost its own
    /// padding spaces to `.vh`'s position:absolute.
    /// </summary>
    [Test]
    public void A_record_says_which_number_is_wins()
    {
        var stats = Markup.Parse(IndexHtml(deck: true)).Descendants()
            .Single(e => e.Attribute("id")?.Value == "stats");
        var cell = stats.Descendants("td")
            .First(td => td.Elements("span").Any(s => s.Attribute("class")?.Value == "vh"));

        // One match, won. The panel counts matches, not the games inside them — the
        // sample's own result line reads "Won 2-1" and this cell must not.
        Assert.That(Markup.Clipboard(cell), Is.EqualTo("1-0"), "the eye gets the notation");
        Assert.That(Markup.Spoken(cell), Is.EqualTo("1 won, 0 lost"), "the ear gets the meaning");

        // Neither half is threaded through the other, so no digit abuts a word.
        Assert.That(cell.Elements("span").Count(), Is.EqualTo(2));
    }

    /// <summary>
    /// The dash means two different things one column apart — no wins yet, and a log
    /// that never recorded an opening — so it cannot be the only thing rendered.
    /// </summary>
    [Test]
    public void A_missing_number_says_what_is_missing_rather_than_printing_a_dash()
    {
        var stats = Markup.Parse(IndexHtml(deck: true)).Descendants()
            .Single(e => e.Attribute("id")?.Value == "stats");

        var dashes = stats.Descendants("span")
            .Where(s => s.Attribute("aria-hidden")?.Value == "true" && s.Value.Trim() == "—")
            .ToList();
        Assert.That(dashes, Is.Not.Empty, "the sample has a deck with no losses");

        foreach (var dash in dashes)
        {
            var twin = dash.NodesAfterSelf().OfType<XElement>().First();
            Assert.That(twin.Attribute("class")?.Value, Is.EqualTo("vh"));
            Assert.That(twin.Value.Trim(), Is.Not.Empty);
            Assert.That(twin.Value, Does.Not.Contain("—"));
        }

        // And the two meanings are told apart rather than sharing one phrase.
        var said = dashes.Select(d => d.NodesAfterSelf().OfType<XElement>().First().Value.Trim())
            .Distinct().ToList();
        Assert.That(said, Does.Contain("no losses yet").Or.Contain("no wins yet"));
    }

    /// <summary>
    /// The deck filter is a toggle inside a row header, which constrains it twice: its
    /// state has to ride on aria-pressed, and its name has to stay the deck's name or
    /// every cell in that row is announced under an instruction.
    /// </summary>
    [Test]
    public void The_deck_filter_is_a_toggle_that_does_not_rename_its_own_row()
    {
        var html = IndexHtml(deck: true);
        var root = Markup.Parse(html);

        // The affordance is a description, which does not become part of the name.
        var note = root.Descendants().Single(e => e.Attribute("id")?.Value == "deck-filter-note");
        Assert.That(note.Value, Does.Contain("filters the match"));
        Assert.That(html, Does.Contain("aria-describedby"), "the button points at it");
        Assert.That(html, Does.Contain("aria-pressed"), "and carries its state");

        // The row header is the deck's name and nothing else.
        var header = root.Descendants("th")
            .Single(th => th.Descendants().Any(d => d.Attribute("class")?.Value == "deck-name"));
        Assert.That(header.Attribute("scope")?.Value, Is.EqualTo("row"));
        Assert.That(header.Value.Trim(), Is.Not.Empty);
        Assert.That(header.Value, Does.Not.Contain("Show only"));
    }

    // ---------- Issue 40: the breakdowns fold away ----------

    /// <summary>
    /// The two breakdowns sit behind a disclosure and the overall record does not.
    /// </summary>
    /// <remarks>
    /// The panel used to put 37 table rows above 529 matches, and the deck table grows
    /// with every new deck played, so the block a reader scrolls past to reach the list
    /// they opened the page for kept getting taller. Hiding the overall record too
    /// would have answered that by making the page worse: it is one row, and it is the
    /// number people come for.
    /// </remarks>
    [Test]
    public void The_breakdowns_fold_away_and_the_overall_record_does_not()
    {
        var stats = Markup.Parse(IndexHtml(deck: true)).Descendants()
            .Single(e => e.Attribute("id")?.Value == "stats");

        var disclosure = stats.Descendants("details")
            .Single(d => d.Attribute("id")?.Value == "breakdowns");

        // Closed until asked for. An open one would leave the layout as it was.
        Assert.That(disclosure.Attribute("open"), Is.Null);

        var folded = disclosure.Descendants("table").ToList();
        Assert.That(folded.Select(t => t.Descendants("caption").First().Value),
            Is.EquivalentTo(new[] { "By format", "By deck" }));

        // The record is still on the page without opening anything.
        var loose = stats.Descendants("table")
            .Where(t => !t.Ancestors("details").Any()).ToList();
        Assert.That(loose, Has.Count.EqualTo(1));
        Assert.That(loose[0].Descendants("td").Any(td => td.Value.Contains('%')), Is.True);
    }

    /// <summary>
    /// A caveat about the record stays with the record. A panel that quietly leaves out
    /// part of the archive is a correctness bug wearing a feature's clothes, and one
    /// that folds the warning away while the number it qualifies stays on screen is the
    /// same bug in a new place.
    /// </summary>
    [Test]
    public void The_caveats_on_the_record_do_not_fold_away_with_the_breakdowns()
    {
        // One match with a deck and one without: the second is counted in the overall
        // record but under no deck, which is the note this is about.
        var html = IndexRenderer.Render([
            IndexRenderer.Summarize(Sample(deck: SampleDeck())),
            IndexRenderer.Summarize(Sample())
        ]);

        var note = Markup.Parse(html).Descendants("p")
            .Single(p => p.Value.Contains("under no deck"));

        Assert.That(note.Ancestors("details"), Is.Empty);
    }

    /// <summary>
    /// The heading stays a real heading rather than moving into the summary. The page
    /// has two of them, and heading navigation is how a screen-reader user skips to
    /// either one.
    /// </summary>
    [Test]
    public void The_stats_heading_survives_the_disclosure()
    {
        var stats = Markup.Parse(IndexHtml(deck: true)).Descendants()
            .Single(e => e.Attribute("id")?.Value == "stats");

        var heading = stats.Descendants("h2")
            .Single(h => h.Attribute("id")?.Value == "stats-heading");

        Assert.That(stats.Attribute("aria-labelledby")?.Value, Is.EqualTo("stats-heading"));
        Assert.That(heading.Ancestors("details"), Is.Empty, "and is not folded away");
    }

    /// <summary>
    /// The one visible line says what opening it gets you, counting rather than saying
    /// "More" — the same reason a game page's deck heading says how many cards.
    /// </summary>
    [Test]
    public void The_summary_says_what_is_behind_it()
    {
        var summary = Markup.Parse(IndexHtml(deck: true)).Descendants("summary").Single();

        Assert.That(summary.Value, Does.Contain("1 format"));
        Assert.That(summary.Value, Does.Contain("1 deck"));
        Assert.That(summary.Value, Does.Not.Contain("1 formats"));
    }

    /// <summary>
    /// The affordance note travels with the buttons it describes, into the disclosure.
    /// </summary>
    /// <remarks>
    /// Its id is the target of every deck button's <c>aria-describedby</c>, so it has
    /// to exist wherever it sits; a description may be resolved from content a closed
    /// disclosure is not rendering, and the buttons are inside it anyway.
    /// </remarks>
    [Test]
    public void The_deck_filter_note_sits_with_the_buttons_it_describes()
    {
        var root = Markup.Parse(IndexHtml(deck: true));

        var note = root.Descendants().Single(e => e.Attribute("id")?.Value == "deck-filter-note");
        Assert.That(note.Ancestors("details").Any(d => d.Attribute("id")?.Value == "breakdowns"),
            Is.True);

        var name = root.Descendants().First(e => e.Attribute("class")?.Value == "deck-name");
        Assert.That(name.Ancestors("details").Any(d => d.Attribute("id")?.Value == "breakdowns"),
            Is.True, "and so do they");
    }

    /// <summary>
    /// A live refresh replaces the panel's contents, and the disclosure is one of them,
    /// so its open state has to be carried across by hand — otherwise the breakdowns a
    /// reader had opened would fold up under them every time a match finished, which is
    /// exactly when they are watching this page.
    /// </summary>
    [Test]
    public void A_live_refresh_leaves_the_disclosure_as_the_reader_left_it()
    {
        var html = IndexHtml(deck: true);

        Assert.That(html, Does.Contain("var open = was ? was.open : false;"));
        Assert.That(html, Does.Contain("now.open = open;"));
    }

    /// <summary>
    /// And leaves the reader on the control they were on. The summary is focusable and
    /// is destroyed by the same swap, so without this a keyboard or screen-reader user
    /// resting on it would be dropped back to the top of the page every time a match
    /// finished — which is the failure the row restoration below it already exists to
    /// prevent.
    /// </summary>
    [Test]
    public void A_live_refresh_leaves_the_reader_on_the_disclosure_they_were_using()
    {
        var html = IndexHtml(deck: true);

        Assert.That(html, Does.Contain("active.tagName === 'SUMMARY'"));
        Assert.That(html, Does.Contain("if (summary) summary.focus();"));
    }

    // ---------- Issue 40: sortable columns ----------

    /// <summary>
    /// Every column that says it sorts supplies a key on every one of its cells.
    /// </summary>
    /// <remarks>
    /// This is the invariant the whole feature rests on. Cells here carry two versions
    /// of themselves - a visible one hidden from assistive technology and a spoken twin
    /// hidden from sight - so a length cell's text content is "4m4 minutes" and a rate's
    /// is "58%58 percent". Sorting on rendered text would order the table by those
    /// strings and look almost right, which is the worst way for it to be wrong. The
    /// comparator falls back to text so nothing breaks if a key is ever missed; this is
    /// what makes sure it never is.
    /// </remarks>
    [Test]
    public void Every_sortable_column_carries_a_key_on_every_cell()
    {
        var root = Markup.Parse(IndexHtml(deck: true));

        foreach (var table in root.Descendants("table"))
        {
            var head = table.Descendants("thead").SingleOrDefault();
            if (head is null) continue;

            var headers = head.Descendants("th").ToList();
            var sortable = headers
                .Select((th, i) => (Index: i, Sorts: th.Attribute("data-sort")?.Value))
                .Where(c => c.Sorts is not null)
                .ToList();

            foreach (var row in table.Descendants("tbody").SelectMany(b => b.Descendants("tr")))
            {
                var cells = row.Elements().ToList();
                foreach (var (index, _) in sortable)
                {
                    Assert.That(cells, Has.Count.GreaterThan(index));
                    Assert.That(cells[index].Attribute("data-key"), Is.Not.Null,
                        $"column {headers[index].Value.Trim()} left a cell with no sort key");
                }
            }
        }
    }

    /// <summary>
    /// A heading is plain text until script makes it a control, and a column with no
    /// order worth putting rows in never claims one.
    /// </summary>
    [Test]
    public void A_column_heading_is_not_a_control_until_script_makes_it_one()
    {
        var html = IndexHtml(deck: true);
        var headers = MatchTable(Markup.Parse(html)).Descendants("thead")
            .Single().Descendants("th").ToList();

        Assert.That(headers.SelectMany(th => th.Descendants("button")), Is.Empty,
            "the button is added by script, not shipped inert");

        var sorts = headers.ToDictionary(th => th.Value.Trim(),
                                         th => th.Attribute("data-sort")?.Value);
        Assert.That(sorts["Date"], Is.EqualTo("num"));
        Assert.That(sorts["Turns"], Is.EqualTo("num"));
        Assert.That(sorts["Length"], Is.EqualTo("num"));
        Assert.That(sorts["Opponent"], Is.EqualTo("text"));

        // The ID column holds a copy button and has no order worth putting rows in.
        Assert.That(sorts["ID"], Is.Null);
    }

    /// <summary>
    /// The keys are the values, not the words. A date sorts by the timestamp the archive
    /// stores rather than by the text shown, and a length by seconds rather than by "9m"
    /// coming after "10m".
    /// </summary>
    [Test]
    public void A_date_sorts_by_its_timestamp_and_a_length_by_its_seconds()
    {
        var sample = Sample();
        var row = MatchTable(Markup.Parse(
            IndexRenderer.Render([IndexRenderer.Summarize(sample)]))).Descendants("tr")
            .Single(tr => tr.Attribute("data-search") is not null);

        var cells = row.Elements().ToList();
        Assert.That(cells[1].Attribute("data-key")?.Value,
            Is.EqualTo(sample.StartedAtMs.ToString(CultureInfo.InvariantCulture)));

        var length = cells[8].Attribute("data-key")?.Value;
        Assert.That(length, Is.Not.Null);
        Assert.That(double.Parse(length!, CultureInfo.InvariantCulture), Is.GreaterThan(0));
    }

    /// <summary>
    /// A cell with nothing in it carries an empty key, which the comparator reads as
    /// missing rather than as zero. An unfinished match has no length, and reading that
    /// as nought seconds would file it among the fastest games ever played.
    /// </summary>
    /// <remarks>
    /// Empty rather than absent, so every cell of a sortable column has one. The
    /// comparator falls back to rendered text when a key is missing, and rendered text
    /// here is "4m4 minutes".
    /// </remarks>
    [Test]
    public void A_cell_with_nothing_in_it_carries_an_empty_key_rather_than_a_zero()
    {
        var row = MatchTable(Markup.Parse(IndexHtml(incomplete: true))).Descendants("tr")
            .Single(tr => tr.Attribute("data-search") is not null);

        var length = row.Elements().ToList()[8];
        Assert.That(length.Value, Is.Empty, "an incomplete match has no length");
        Assert.That(length.Attribute("data-key")?.Value, Is.Empty);
    }

    /// <summary>
    /// Sorting moves rows, never cells. Appending a row that is already in the table
    /// moves it whole, which is what keeps a result in the same row as the opponent it
    /// was against - and it rebuilds nothing, so the stars and copy buttons keep their
    /// state and their listeners.
    /// </summary>
    [Test]
    public void Sorting_moves_whole_rows_and_says_which_way_it_pointed()
    {
        var html = IndexHtml(deck: true);

        Assert.That(html, Does.Contain("order.forEach(function (row) { body.appendChild(row); });"));
        Assert.That(html, Does.Contain("aria-sort"), "the direction is said, not only drawn");
        Assert.That(html, Does.Contain("Sorted by "), "and announced when it changes");

        // One implementation, applied to whichever tables declare sortable columns.
        Assert.That(html, Does.Contain("document.querySelectorAll('table').forEach"));
    }

    /// <summary>
    /// A table with one row has no order to put it in, so it never grows a control that
    /// could not change anything. That is the overall record's whole shape.
    /// </summary>
    [Test]
    public void A_one_row_table_grows_no_sort_controls()
    {
        var root = Markup.Parse(IndexHtml(deck: true));
        Assert.That(IndexHtml(deck: true), Does.Contain("body.rows.length < 2"));

        var overall = root.Descendants("table")
            .Single(t => t.Descendants("caption").Any(c => c.Value == "Overall record"));

        Assert.That(overall.Descendants("tbody").Single().Descendants("tr").Count(),
            Is.EqualTo(1));
        Assert.That(overall.Descendants("thead").Single().Descendants("th")
            .Any(th => th.Attribute("data-sort") is not null), Is.False);
    }

    /// <summary>
    /// A live refresh writes the rows back in the build's order and replaces the panel's
    /// tables outright, so a sort the reader chose has to be put back - quietly, because
    /// they did not just ask for it.
    /// </summary>
    [Test]
    public void A_live_refresh_puts_a_chosen_sort_back()
    {
        var html = IndexHtml(deck: true);

        Assert.That(html, Does.Contain("wireTables(true);"));
        Assert.That(html, Does.Contain("if (table.id) sorted[table.id] = state;"));

        // The breakdowns are replaced wholesale, so they need ids to be remembered by.
        var ids = Markup.Parse(html).Descendants("table")
            .Select(t => t.Attribute("id")?.Value).ToList();
        Assert.That(ids, Does.Contain("by-deck"));
        Assert.That(ids, Does.Contain("by-format"));
    }

    /// <summary>
    /// And leaves the reader on the sort control they were using. The breakdown tables
    /// are replaced outright by a refresh, so their controls are new nodes; without
    /// this, a keyboard user resting on one is dropped back to the top of the page every
    /// time a match finishes.
    /// </summary>
    /// <remarks>
    /// The same failure Copilot found for the disclosure's summary, one control along.
    /// A sort button has no id, but the table and the column do — and they are the same
    /// pair the sort itself is remembered by.
    /// </remarks>
    [Test]
    public void A_live_refresh_leaves_the_reader_on_the_sort_control_they_were_using()
    {
        var html = IndexHtml(deck: true);

        Assert.That(html, Does.Contain("active.closest('thead th')"));
        Assert.That(html, Does.Contain("if (control) control.focus();"));
    }

    [Test]
    public void An_empty_archive_grows_no_stats_panel()
    {
        Assert.That(IndexRenderer.Render([]), Does.Not.Contain("id=\"stats\""));
    }

    [Test]
    public void Index_table_header_cells_are_scoped_and_none_are_blank()
    {
        var root = Markup.Parse(IndexHtml());
        var columns = MatchTable(root).Descendants("thead").Single().Descendants("th").ToList();

        Assert.That(columns, Has.Count.EqualTo(9));
        foreach (var th in columns)
        {
            Assert.That(th.Attribute("scope")?.Value, Is.EqualTo("col"));
            // The star column used to be an empty <th>, so every star button was
            // announced under a column with no name.
            Assert.That(th.Value.Trim(), Is.Not.Empty);
        }

        // The date identifies the row, so moving across a row announces which match
        // you are in rather than five values with no subject.
        var rowHeader = MatchTable(root).Descendants("tbody").Single().Descendants("th").Single();
        Assert.That(rowHeader.Attribute("scope")?.Value, Is.EqualTo("row"));
        Assert.That(rowHeader.Descendants("a").Single().Value, Does.StartWith("2026-"));
    }

    // ---------- Issue 21: copying the game id ----------

    /// <summary>
    /// The id is what <c>mtga-pbp why &lt;matchId&gt;</c> takes and what identifies a
    /// match in a bug report, and it is written nowhere on the page — it is the file
    /// name. Both views get a control that copies it.
    /// </summary>
    [Test]
    public void Both_views_offer_the_game_id_and_carry_it_in_the_markup()
    {
        var button = Markup.Parse(GamePageRenderer.Render(Sample())).Descendants("button")
            .Single(b => b.Attribute("id")?.Value == "copy-id");
        Assert.That(button.Attribute("data-id")?.Value, Is.EqualTo("abc-123"));
        Assert.That(button.Value.Trim(), Is.EqualTo("Copy game ID"));

        var row = MatchTable(Markup.Parse(IndexHtml())).Descendants("tbody").Single()
            .Descendants("button").Single(b => b.Attribute("class")?.Value == "copyid");
        Assert.That(row.Attribute("data-id")?.Value, Is.EqualTo("abc-123"));

        // Named for the row it belongs to: an archive of these is otherwise a list of
        // controls all called the same thing.
        Assert.That(row.Attribute("aria-label")?.Value, Does.Contain("2026-"));
        Assert.That(row.Attribute("type")?.Value, Is.EqualTo("button"));

        // The glyph is decoration; a synthesiser reading it would say nothing useful.
        Assert.That(row.Elements("span").Single().Attribute("aria-hidden")?.Value,
            Is.EqualTo("true"));
    }

    /// <summary>
    /// The index's copy control sits beside the row header rather than inside it.
    /// </summary>
    /// <remarks>
    /// A screen reader repeats the row header while moving across a row, so a button
    /// placed inside that <c>th</c> would append its label to every cell announcement
    /// in the row — the date cell is the row's name, and a name is not a place to keep
    /// a control.
    /// </remarks>
    [Test]
    public void The_index_copy_control_stays_out_of_the_row_header()
    {
        var row = MatchTable(Markup.Parse(IndexHtml())).Descendants("tbody").Single()
            .Descendants("tr").Single();

        var header = row.Elements("th").Single();
        Assert.That(header.Descendants("button"), Is.Empty);
        Assert.That(header.Value.Trim(), Does.StartWith("2026-"), "the row is named by its date");

        var cell = row.Elements("td").Single(td => td.Descendants("button")
            .Any(b => b.Attribute("class")?.Value == "copyid"));

        // By element order, not PreviousNode: the markup is indented, so the two are
        // separated by a whitespace text node the parser keeps on purpose.
        var order = row.Elements().ToList();
        Assert.That(order.IndexOf(cell), Is.EqualTo(order.IndexOf(header) + 1),
            "it sits immediately after the name");
    }

    /// <summary>
    /// Copying needs no server, so the index wires it above the line that abandons
    /// everything needing one.
    /// </summary>
    /// <remarks>
    /// The star is disabled without <c>mtga-pbp watch</c> because keeping a match is a
    /// write. Copying is not, and a report opened straight off disk is the case this
    /// project is built around — a copy button dead in that mode would be dead where
    /// it is most wanted.
    /// </remarks>
    [Test]
    public void Copying_an_id_works_on_a_page_opened_from_disk()
    {
        var html = IndexHtml();
        var servedOnly = html.IndexOf("location.protocol.indexOf('http')", StringComparison.Ordinal);
        var wiring = html.IndexOf(".copyid", StringComparison.Ordinal);

        Assert.That(servedOnly, Is.GreaterThan(0), "the served-only guard is still there");
        Assert.That(html.IndexOf("closest('.copyid')", StringComparison.Ordinal),
            Is.InRange(1, servedOnly), "the copy handler is wired before the guard");
        Assert.That(wiring, Is.GreaterThan(0));

        // And the button never ships disabled the way the star does.
        var button = Markup.Parse(html).Descendants("button")
            .Single(b => b.Attribute("class")?.Value == "copyid");
        Assert.That(button.Attribute("disabled"), Is.Null);
    }

    /// <summary>
    /// Both pages keep their own copy of the clipboard fallback, so both are checked.
    /// </summary>
    /// <remarks>
    /// <c>navigator.clipboard</c> needs a secure context and <c>file://</c> does not
    /// qualify everywhere, so a page without the fallback is a page whose copy button
    /// does nothing on disk in some browsers. Two renderers, two copies of the code,
    /// and issue 30 is what happens when only one of a pair gets fixed.
    /// </remarks>
    [Test]
    public void Neither_page_relies_on_a_clipboard_API_that_a_file_URL_may_not_have()
    {
        foreach (var (page, html) in new[]
                 {
                     ("game page", GamePageRenderer.Render(Sample())),
                     ("index", IndexHtml())
                 })
        {
            Assert.That(html, Does.Contain("navigator.clipboard.writeText"), page);
            Assert.That(html, Does.Contain("document.execCommand('copy')"),
                $"{page}: no fallback for a file:// page");
        }
    }

    [Test]
    public void Index_table_has_a_caption_that_names_it_and_its_order()
    {
        // Screen readers list and jump between tables by name, and "most recent first"
        // is otherwise information carried only by the visual order.
        var caption = MatchTable(Markup.Parse(IndexHtml())).Elements("caption").Single();
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

    // ---------- Withheld data ----------
    //
    // Arena drops whole message bodies past 50 game objects or 50 annotations, leaving
    // one line of prose behind. Two of the 152 archived matches are affected. The point
    // of every test below is that such a match must never read as a full account of
    // itself: telling someone how they lost is the whole job, and a confident
    // transcript with a hole in it is worse than no transcript.

    [Test]
    public void Game_page_says_data_is_missing_without_calling_the_match_incomplete()
    {
        // Two different failures. "The log was rotated" sends a reader looking for a
        // missing ending; "the log skipped things" tells them the ending may be right
        // there while the reason for it is not. Conflating them misdirects.
        var html = GamePageRenderer.Render(Sample(gaps: [SummarizedGap()]));

        Assert.That(html, Does.Contain("id=\"gap-warning\""));
        Assert.That(html, Does.Contain("Part of this match is missing"));
        Assert.That(html, Does.Not.Contain("rotated"),
            "nothing was rotated here, and saying so would send the reader to the wrong place");

        // A clean match keeps quiet.
        Assert.That(GameHtml(), Does.Not.Contain("gap-warning"));
    }

    [Test]
    public void Game_page_shows_both_warnings_when_a_match_has_both_faults()
    {
        var root = Markup.Parse(
            GamePageRenderer.Render(Sample(incomplete: true, gaps: [SummarizedGap()])));
        var warnings = root.Descendants("p")
            .Where(p => p.Attribute("class")?.Value == "warn")
            .ToList();

        Assert.That(warnings, Has.Count.EqualTo(2));
        Assert.That(warnings.Select(w => w.Value),
            Has.One.Contains("rotated").And.One.Contains("Part of this match is missing"));
    }

    [Test]
    public void Copying_a_transcript_takes_every_warning_with_it()
    {
        // Asserted against the source text rather than by running the script, since
        // there is no JS engine here. It is still worth pinning: the copy button read
        // querySelector('.warn'), which silently takes the first banner only — so a
        // match that is both cut short and missing data would paste as merely cut
        // short, and the pasted transcript is the one that gets shared.
        Assert.That(GameHtml(), Does.Contain("querySelectorAll('.warn')"));
    }

    [Test]
    public void Markdown_carries_the_missing_data_warning_too()
    {
        // The markdown is what gets pasted into Discord or read by a screen reader
        // outside the browser; a warning that lives only in HTML has not been given.
        var md = MarkdownRenderer.Render(Sample(gaps: [SummarizedGap()]));

        Assert.That(md, Does.Contain("> Part of this match is missing"));
        Assert.That(MarkdownRenderer.Render(Sample()), Does.Not.Contain("missing"));
    }

    [Test]
    public void Missing_data_is_counted_in_words_not_just_marked_with_a_dagger()
    {
        var root = Markup.Parse(IndexHtml(gaps: true));
        var cell = root.Descendants("td")
            .Single(td => td.Attribute("class")?.Value is "win" or "loss");

        Assert.That(Markup.Spoken(cell), Is.EqualTo("Won 2-1 (missing data)"));
        Assert.That(cell.Value, Does.Contain("†"), "the dagger still has to be there to look at");
        Assert.That(
            root.Descendants().Single(e => e.Attribute("id")?.Value == "gaps-note").Value,
            Does.Contain("not a complete account"));

        Assert.That(IndexHtml(), Does.Not.Contain("gaps-note"));
    }

    [Test]
    public void The_two_index_footnotes_do_not_share_a_symbol()
    {
        // A match can be both, and if both marks were asterisks the row would read
        // "Won 2-1 **" against two footnotes with no way to tell which applied.
        var cell = Markup.Parse(IndexHtml(incomplete: true, gaps: true))
            .Descendants("td").Single(td => td.Attribute("class")?.Value is "win" or "loss");

        Assert.That(Markup.Spoken(cell), Is.EqualTo("Won 2-1 (incomplete) (missing data)"));
        Assert.That(cell.Value, Does.Contain("*").And.Contain("†"));
    }

    [Test]
    public void The_warning_counts_what_was_missed_and_agrees_with_itself()
    {
        // One gap and several read differently, and "1 game-state updates" undermines
        // the credibility of the only sentence on the page whose job is to be believed.
        Assert.That(MarkdownRenderer.Render(Sample(gaps: [SummarizedGap()])),
            Does.Contain("in place of 1 game-state update that grew too large"));

        Assert.That(MarkdownRenderer.Render(Sample(gaps: [SummarizedGap(), SummarizedGap(20)])),
            Does.Contain("in place of 2 game-state updates that grew too large"));

        // A torn envelope is a different sentence, because it is a different cause:
        // Arena refused to write one, the other never arrived intact.
        var both = MarkdownRenderer.Render(Sample(gaps:
            [SummarizedGap(), new LogGap(LogGapKind.Torn, 99, 0, 0, [])]));
        Assert.That(both, Does.Contain("1 game-state update that grew too large"));
        Assert.That(both, Does.Contain("1 log line ended mid-message"));
    }

    /// <summary>
    /// The warning names its mechanism and closes the recovery question (#15). Told
    /// only "missing", a careful reader's next move is a re-run — and it cannot help,
    /// because the summarized body was never written and the torn one was destroyed
    /// as it was. The note has to say so, or the reader spends the effort finding out.
    /// </summary>
    [Test]
    public void The_warning_names_the_mechanism_and_says_the_loss_is_permanent()
    {
        var summarized = TranscriptSummary.GapWarning(Sample(gaps: [SummarizedGap()]))!;

        // A summary is Arena's decision at a size limit, not damage or packet loss —
        // the difference tells a reader whether their log file is healthy.
        Assert.That(summarized, Does.Contain("Arena wrote a one-line summary"));
        Assert.That(summarized, Does.Contain("no re-scan can recover it"));

        var torn = TranscriptSummary.GapWarning(
            Sample(gaps: [new LogGap(LogGapKind.Torn, 99, 0, 0, [])]))!;
        Assert.That(torn, Does.Contain("ended mid-message"));
        Assert.That(torn, Does.Not.Contain("summary"),
            "nothing was summarized here, and saying so would misname the failure");
        Assert.That(torn, Does.Contain("no re-scan can recover it"));
    }

    [Test]
    public void GamePage_groups_each_turn_into_a_list_that_keeps_its_role()
    {
        var root = Markup.Parse(GameHtml());
        var beats = root.Descendants("section")
            .Single(s => s.Attribute("data-density")?.Value == "beats");

        // Two lists: the opening and the one turn. The opening is a list for the same
        // reason a turn is — it is a short ordered sequence of things that happened.
        var lists = beats.Descendants("ol").ToList();
        Assert.That(lists, Has.Count.EqualTo(2));

        // Entering a list tells a screen reader user how many things happened this
        // turn before reading any of them, and turns a turn into something the list
        // quick-keys can step through. Paragraphs give none of that.
        Assert.That(lists[0].Elements("li").Count(), Is.EqualTo(2), "the opening");
        Assert.That(lists[1].Elements("li").Count(), Is.EqualTo(3), "turn one");

        // Safari drops the list role when list-style is none, which is exactly the
        // styling used here, so the role is stated rather than inferred.
        Assert.That(lists.Select(l => l.Attribute("role")?.Value), Is.All.EqualTo("list"));
        Assert.That(GameHtml(), Does.Contain("list-style:none"));

        foreach (var li in lists.SelectMany(l => l.Elements("li")))
            Assert.That(li.Parent!.Name.LocalName, Is.EqualTo("ol"));
    }

    [Test]
    public void GamePage_turn_anchors_stay_unique_across_the_two_densities()
    {
        var root = Markup.Parse(GameHtml());
        var headings = root.Descendants("h2").ToList();

        // The opening and one turn, each rendered in both densities. The opening takes
        // turn zero's anchor, which no turn can ever claim — Arena numbers turns from
        // one — so it needs no scheme of its own to stay unique.
        Assert.That(headings.Select(h => h.Attribute("id")?.Value),
            Is.EqualTo(new[] { "t0", "t1", "v-t0", "v-t1" }),
            "the density that is visible by default keeps the short anchors");
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
                     IndexHtml(incomplete: true, gaps: true),
                     GameHtml(), GamePageRenderer.Render(Repeating()),
                     GamePageRenderer.Render(Buffed()),
                     GamePageRenderer.Render(Sample(gaps: [SummarizedGap()])),
                     GamePageRenderer.Render(Timed()),
                     GameDeckHtml(),
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
        // By anchor, not by position: the opening's heading comes first on the page.
        var heading = Markup.Parse(GamePageRenderer.Render(scored)).Descendants("h2")
            .First(h => h.Attribute("id")?.Value == "t1");

        Assert.That(heading.Elements("span").Count(s => s.Attribute("class")?.Value == "vh"),
            Is.EqualTo(1));
        Assert.That(Markup.Spoken(heading), Does.Contain("(You 20, Opponent 17)"));
        Assert.That(heading.Value, Does.Contain("(You 20 · "),
            "the dot still has to be there to look at");
    }

    [Test]
    public void GamePage_gives_the_buff_arrow_something_to_say()
    {
        // "Rabbit A (1/1 → 6/6)" is the point of the whole line, and a synthesiser that
        // drops U+2192 reads it as two statlines with nothing between them. The glyph
        // stays for the eye and the word is supplied for the ear, exactly as the "×" and
        // "·" notations already are.
        var li = Markup.Parse(GamePageRenderer.Render(Buffed())).Descendants("li").First();

        Assert.That(Markup.Spoken(li),
            Is.EqualTo("You cast Ethereal Armor, targeting Rabbit A (1/1 becomes 6/6)"));

        // The glyph is still in the markup, in its own hidden span — which is also what
        // the copy button strips the spoken text back down to, so pasted markdown reads
        // "(1/1 → 6/6)" and matches the markdown export.
        var glyph = li.Elements("span").Single(s => s.Attribute("aria-hidden") is not null);
        Assert.That(glyph.Value, Is.EqualTo(" → "));
    }

    [Test]
    public void GamePage_copy_leaves_the_screen_reader_only_text_out_of_the_clipboard()
    {
        // The spelled-out repeat count exists for speech, not for paste; the pasted
        // transcript should match what the markdown export produces.
        var html = GamePageRenderer.Render(Repeating());
        Assert.That(html, Does.Contain("clone.querySelectorAll('.vh')"));
        Assert.That(html, Does.Contain("h2, h3, li.beat, li.board"));
    }

    // ---------- the decklist ----------

    [Test]
    public void GamePage_renders_the_deck_as_a_list_that_keeps_its_role()
    {
        var deck = Markup.Parse(GameDeckHtml()).Descendants("details")
            .Single(d => d.Attribute("id")?.Value == "deck");
        var list = deck.Descendants("ul").Single();

        // A decklist is a list, and saying so is what lets a screen reader announce
        // how many cards are in it and step through them with the list quick keys.
        // list-style:none costs the role in Safari, so it is stated, as the turns do.
        Assert.That(list.Attribute("role")?.Value, Is.EqualTo("list"));
        Assert.That(list.Elements("li").Count(), Is.EqualTo(3));

        // Collapsed by default and opened with no script, because the page has to work
        // opened straight from a file.
        Assert.That(deck.Attribute("open"), Is.Null);
        Assert.That(deck.Elements("summary").Single().Value, Is.EqualTo("Your deck (7 cards)"));
    }

    [Test]
    public void GamePage_counts_deck_copies_in_words_and_gets_the_singular_right()
    {
        // "4×" arrives as "4" from a synthesiser that skips U+00D7, which next to a
        // card name is indistinguishable from part of the name.
        var items = Markup.Parse(GameDeckHtml()).Descendants("li").ToList();

        Assert.That(Markup.Spoken(items[0]), Is.EqualTo("4 copies of Hare Apparent"));
        Assert.That(items[0].Value, Does.StartWith("4×"), "the glyph stays for the eye");

        // A one-of is a copy, not a copies.
        Assert.That(Markup.Spoken(items[1]), Is.EqualTo("1 copy of Plains"));
    }

    [Test]
    public void GamePage_says_which_cards_never_turned_up_in_words()
    {
        var items = Markup.Parse(GameDeckHtml()).Descendants("li").ToList();
        var unseen = items.Single(li => li.Attribute("class")?.Value == "unseen");

        // Dimming alone would say it only to people who can see the dimming, and the
        // separator needs the same comma every other "·" on the page gets.
        Assert.That(Markup.Spoken(unseen), Is.EqualTo("2 copies of Split Up, not seen"));
        Assert.That(Markup.Clipboard(unseen), Is.EqualTo("2× Split Up · not seen"));

        // Every other line is left alone.
        Assert.That(items.Count(li => li.Attribute("class")?.Value == "unseen"), Is.EqualTo(1));

        // And what the mark means, since "not seen" could be read as "not played".
        Assert.That(Markup.Parse(GameDeckHtml()).Descendants("p")
                .Any(p => p.Attribute("class")?.Value == "note" &&
                          p.Value.Contains("stayed in your library", StringComparison.Ordinal)),
            Is.True);
    }

    [Test]
    public void GamePage_omits_the_deck_entirely_when_the_log_carried_none()
    {
        // Most archived matches predate deck capture. An empty disclosure widget
        // inviting you to open it and find nothing is worse than no widget.
        Assert.That(GameHtml(), Does.Not.Contain("id=\"deck\""));
        Assert.That(GameHtml(), Does.Not.Contain("Your deck"));
    }

    [Test]
    public void Copying_the_page_reproduces_the_markdown_export_of_the_deck()
    {
        // The clipboard and the .md file are meant to be the same document. The page
        // adds the spoken forms of "×" and "·" on top of the shared line text and the
        // copy strips them back off; this asserts the round trip actually lands.
        var html = GameDeckHtml();
        Assert.That(html, Does.Contain("getElementById('deck')"),
            "the copy must gather the deck even while it is collapsed");

        var copied = Markup.Parse(html).Descendants("details")
            .Single(d => d.Attribute("id")?.Value == "deck")
            .Descendants("li")
            .Select(Markup.Clipboard)
            .ToList();

        // The same lines as the .md file writes, taken from between its deck heading
        // and the blank line that ends the list.
        var exported = MarkdownRenderer.Render(Sample(deck: SampleDeck()))
            .ReplaceLineEndings("\n").Split('\n')
            .SkipWhile(l => !l.StartsWith("## Your deck", StringComparison.Ordinal))
            .SkipWhile(l => !l.StartsWith("- ", StringComparison.Ordinal))
            .TakeWhile(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..])
            .ToList();

        Assert.That(copied, Is.EqualTo(exported));
        Assert.That(copied, Has.Count.EqualTo(3));
    }

    // ---------- Issue 30: selecting by hand ----------

    /// <summary>
    /// The clip-rect pattern hides text from the eye but not from a mouse, because
    /// clipping happens at paint time and selection does not. Without
    /// <c>user-select</c> a dragged selection copies both halves of every split
    /// notation, so a decklist pastes as "1×1 copy of Plains".
    /// </summary>
    /// <remarks>
    /// Asserted on both pages because each renderer carries its own copy of the rule,
    /// and a fix applied to one of them leaves the other doubling — which reads as
    /// fixed while half the site is not.
    /// </remarks>
    [Test]
    public void Neither_page_ships_hidden_text_that_a_mouse_can_still_select()
    {
        (string Page, string Html)[] pages =
        [
            ("game page", GameDeckHtml()),
            ("index", IndexRenderer.Render([IndexRenderer.Summarize(Sample())]))
        ];

        foreach (var (page, html) in pages)
        {
            var rules = Regex.Matches(html, @"\.vh\{[^}]*\}").Select(m => m.Value).ToList();
            Assert.That(rules, Is.Not.Empty, $"the {page} has no .vh rule at all");

            foreach (var rule in rules)
            {
                // Compared as whole declarations rather than as substrings of the rule:
                // "-webkit-user-select:none" contains "user-select:none", so a substring
                // check passes on a rule carrying only the prefixed form — which is the
                // one every browser but Safari ignores, and exactly the half-fix this
                // test exists to catch.
                var declarations = Declarations(rule);

                Assert.That(declarations, Does.Contain("user-select:none"), $"{page}: {rule}");

                // iOS Safari needs the prefix, and iOS is in the archive's own player
                // data, so it is not a browser this can be lax about.
                Assert.That(declarations, Does.Contain("-webkit-user-select:none"),
                    $"{page}: {rule}");
            }
        }
    }

    /// <summary>
    /// One CSS rule's declarations, as whole strings. The rule is wrapped across source
    /// lines, so each is trimmed and the space around its colon normalised.
    /// </summary>
    private static List<string> Declarations(string rule) =>
        rule[(rule.IndexOf('{', StringComparison.Ordinal) + 1)..]
            .TrimEnd('}')
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => Regex.Replace(d, @"\s*:\s*", ":"))
            .ToList();

    /// <summary>
    /// The markup fact that makes the doubling reachable: a row's visible half and its
    /// spoken half are separate elements sitting flush against each other.
    /// </summary>
    /// <remarks>
    /// This stays true after the fix, and is meant to — the halves have to be separate
    /// elements for a synthesiser to get one and the eye the other. What changed is
    /// that the spoken half is no longer selectable, which is why the fix belongs in
    /// the stylesheet rather than in a separator between the two.
    /// </remarks>
    [Test]
    public void A_decklist_row_keeps_its_visible_and_spoken_halves_in_separate_elements()
    {
        var row = Markup.Parse(GameDeckHtml()).Descendants("li").First();

        var glyph = row.Elements("span").First(s => s.Attribute("aria-hidden")?.Value == "true");
        var spoken = row.Elements("span").First(s => s.Attribute("class")?.Value == "vh");

        Assert.That(glyph.Value, Is.EqualTo("4×"));
        Assert.That(spoken.Value, Is.EqualTo("4 copies of"));

        // Nothing between them, not even a space. A browser handed this run of text
        // with no styling to stop it concatenates the two, which is the reported bug.
        Assert.That(glyph.NextNode, Is.SameAs(spoken));
        Assert.That(row.Value, Does.StartWith("4×4 copies of"));

        // Each half on its own is what the two audiences actually get.
        Assert.That(Markup.Spoken(row), Is.EqualTo("4 copies of Hare Apparent"));
        Assert.That(Markup.Clipboard(row), Is.EqualTo("4× Hare Apparent"));
    }

    // ---------- the commander ----------

    /// <summary>The same sample deck, registered with a commander beside it.</summary>
    private static Transcript BrawlSample(params string[] commanders) =>
        Sample(deck: SampleDeck(), commanders: commanders);

    /// <summary>
    /// The heading carries the commander's existence and the body carries its name.
    /// Both, not either: the heading is the collapsed disclosure's only visible line,
    /// so "(7 cards)" alone would describe an illegal Brawl deck — the very bug the
    /// commander section exists to fix — while a name in the heading would run long.
    /// </summary>
    [Test]
    public void Markdown_names_the_commander_between_the_deck_heading_and_its_cards()
    {
        var md = MarkdownRenderer.Render(BrawlSample("Lumra, Bellow of the Woods"))
            .ReplaceLineEndings("\n");

        Assert.That(md, Does.Contain(
            "## Your deck (7 cards and a commander)\n\nCommander: Lumra, Bellow of the Woods\n\n- "));
    }

    /// <summary>
    /// Arena's own deck constraints allow two commanders, so a partner pair renders
    /// both. Registration order, not alphabetical: the pair is one choice.
    /// </summary>
    [Test]
    public void Markdown_renders_both_partner_commanders()
    {
        var md = MarkdownRenderer.Render(
                BrawlSample("Rograkh, Son of Rohgahh", "Ardenn, Intrepid Archaeologist"))
            .ReplaceLineEndings("\n");

        Assert.That(md, Does.Contain("## Your deck (7 cards and 2 commanders)"));
        Assert.That(md, Does.Contain(
            "\nCommanders: Rograkh, Son of Rohgahh and Ardenn, Intrepid Archaeologist\n"));
    }

    /// <summary>
    /// The commander is a paragraph above the cards list, never a row in it. A row
    /// would imply a card that could be drawn, and would change what a screen reader
    /// announces on entering the list — "how many distinct cards the library holds".
    /// </summary>
    [Test]
    public void GamePage_keeps_the_commander_out_of_the_cards_list()
    {
        var deck = Markup.Parse(GamePageRenderer.Render(BrawlSample("Lumra, Bellow of the Woods")))
            .Descendants("details").Single(d => d.Attribute("id")?.Value == "deck");

        Assert.That(deck.Elements("summary").Single().Value,
            Is.EqualTo("Your deck (7 cards and a commander)"));
        Assert.That(deck.Descendants("p")
                .Single(p => p.Attribute("class")?.Value == "commander").Value,
            Is.EqualTo("Commander: Lumra, Bellow of the Woods"));
        Assert.That(deck.Descendants("li").Count(), Is.EqualTo(3),
            "the list is the library, and the commander is not in it");
    }

    /// <summary>
    /// The clipboard and the .md file are meant to be the same document, and the
    /// commander line is part of that document now.
    /// </summary>
    [Test]
    public void Copying_the_page_reproduces_the_markdown_export_of_the_commander()
    {
        var t = BrawlSample("Lumra, Bellow of the Woods");

        var copied = Markup.Parse(GamePageRenderer.Render(t)).Descendants("p")
            .Single(p => p.Attribute("class")?.Value == "commander");
        var exported = MarkdownRenderer.Render(t).ReplaceLineEndings("\n").Split('\n')
            .Single(l => l.StartsWith("Commander:", StringComparison.Ordinal));

        Assert.That(Markup.Clipboard(copied), Is.EqualTo(exported));
    }

    /// <summary>
    /// A match with no commander recorded renders exactly as it did before the field
    /// was parsed — most of the archive predates it, and every constructed match
    /// lacks it by nature.
    /// </summary>
    [Test]
    public void A_match_without_a_commander_renders_no_commander_anywhere()
    {
        var md = MarkdownRenderer.Render(Sample(deck: SampleDeck()));
        Assert.That(md, Does.Contain("## Your deck (7 cards)"));
        Assert.That(md, Does.Not.Contain("Commander"));

        Assert.That(Markup.Parse(GameDeckHtml()).Descendants("p")
            .Any(p => p.Attribute("class")?.Value == "commander"), Is.False);
    }

    [Test]
    public void Markdown_lists_the_deck_ahead_of_the_first_turn()
    {
        var md = MarkdownRenderer.Render(Sample(deck: SampleDeck())).ReplaceLineEndings("\n");

        Assert.That(md.IndexOf("## Your deck (7 cards)", StringComparison.Ordinal),
            Is.GreaterThan(0).And.LessThan(md.IndexOf("## Turn 1", StringComparison.Ordinal)),
            "the deck is what you check while reading, so it goes before the turns");
        Assert.That(md, Does.Contain("\n- 4× Hare Apparent\n"));
        Assert.That(md, Does.Contain("\n- 2× Split Up · not seen\n"));

        // A match with no deck message must not grow an empty heading.
        Assert.That(MarkdownRenderer.Render(Sample()), Does.Not.Contain("Your deck"));
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
        foreach (var html in new[] { IndexHtml(), IndexHtml(deck: true), GameHtml() })
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
        foreach (var html in new[] { IndexHtml(), IndexHtml(deck: true), GameHtml() })
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
    /// What the copy button would put on the clipboard for this element: the text with
    /// the screen-reader-only spans taken out, which is exactly what <c>textOf</c> does
    /// in the page's own script.
    /// </summary>
    internal static string Clipboard(XElement element) => Visible(element).Trim();

    // Trimmed once at the end, never per node: " · " inside a nested span is a real
    // separator, and trimming on the way up collapses it to "·".
    private static string Visible(XElement element) =>
        string.Concat(element.Nodes().Select(n => n switch
        {
            XText text => text.Value,
            XElement child when child.Attribute("class")?.Value == "vh" => "",
            XElement child => Visible(child),
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
