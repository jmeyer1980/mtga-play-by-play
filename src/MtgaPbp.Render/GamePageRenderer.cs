using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public static partial class GamePageRenderer
{
    public static string Render(Transcript t, Neighbours? nav = null,
        IReadOnlyDictionary<string, CardFace>? faces = null)
    {
        var sb = new StringBuilder();
        sb.Append($"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{E(TranscriptSummary.Title(t))}</title>
            <style>{Css}</style></head><body>
            <main>
            <header>
              <p class="back"><a href="../index.html"><span aria-hidden="true">←</span> All games</a></p>
              <h1 id="top" tabindex="-1">{E(TranscriptSummary.Title(t))}</h1>
              <p class="sub">{Speech(TranscriptSummary.Subtitle(t))}</p>
              <div class="controls">
                <button id="density-toggle" type="button"
                        aria-controls="beats verbose">Show verbose detail</button>
                <button id="copy-button" type="button">Copy transcript</button>
                <button id="copy-anon" type="button"
                        data-title="{E(TranscriptSummary.AnonymousTitle(t))}"
                        >Copy without names</button>
                <button id="copy-id" type="button"
                        data-id="{E(t.MatchId)}">Copy game ID</button>
                <span id="status" class="status" role="status"></span>
              </div>
            </header>
            """);

        if (t.Incomplete)
            sb.Append("""<p class="warn">This match is incomplete — the log was rotated before it finished.</p>""");

        // Shares `.warn` with the banner above: both say "do not read this as the whole
        // story", and that rule's colours are the ones measured against 1.4.11. What
        // separates them is the sentence and the id, not a second palette entry nobody
        // checked. Both can appear at once, and a match can genuinely have both faults.
        if (TranscriptSummary.GapWarning(t) is { } gap)
            sb.Append($"""<p class="warn" id="gap-warning">{E(gap)}</p>""");

        // Not a `.warn`: nothing is wrong with the match, only with the reading a
        // duration invites. It sits above the transcript so the one place that explains
        // the annotation is findable from any turn carrying one.
        if (TranscriptSummary.TimingNote(t) is { } timing)
            sb.Append($"""<p class="note" id="timing-note">{E(timing)}</p>""");

        AppendDeck(sb, t, faces);
        AppendSection(sb, t, Density.Beats);
        AppendSection(sb, t, Density.Verbose);

        sb.Append($"""
            </main>
            {Nav(nav)}
            <footer class="build">{E(BuildInfo.Line)}</footer>
            <script>{Script}</script></body></html>
            """);
        return sb.ToString();
    }

    /// <summary>
    /// The deck you registered, collapsed. A disclosure widget rather than a section:
    /// the transcript is what the page is for, and a 20-line list wedged above it
    /// pushes turn one off the screen — but a decklist is also the thing you reach for
    /// mid-read, so it sits where you are rather than at the bottom. <c>details</c>
    /// does that with no script, which matters on a page opened from a file.
    /// </summary>
    /// <remarks>
    /// A real list, because it is one: a screen reader announces how many distinct
    /// cards the deck holds on entering it, and each line is reachable with the list
    /// quick keys. <c>list-style:none</c> costs the role in Safari, so the role is
    /// stated, exactly as the turn lists do.
    /// </remarks>
    private static void AppendDeck(StringBuilder sb, Transcript t,
        IReadOnlyDictionary<string, CardFace>? faces)
    {
        if (t.Deck.Count == 0) return;

        sb.Append($"""
            <details class="deck" id="deck">
            <summary>{E(TranscriptSummary.DeckHeading(t))}</summary>
            """);

        // A paragraph above the list, never a row in it: the list is the library, and
        // a screen reader entering it announces how many distinct cards it holds — the
        // commander is not one of them and could never be drawn from it. With a face
        // to show, the paragraph becomes the summary of a peek — same words, same
        // place, one disclosure deeper.
        if (TranscriptSummary.CommanderLine(t) is { } commander)
        {
            var commanderFaces = t.Commanders
                .Select(n => faces?.GetValueOrDefault(n))
                .OfType<CardFace>()
                .ToList();
            if (commanderFaces.Count > 0)
            {
                sb.Append($"""<details class="peek"><summary><span class="commander">{E(commander)}</span></summary>""");
                foreach (var f in commanderFaces) AppendFace(sb, f);
                sb.Append("</details>");
            }
            else
            {
                sb.Append($"""<p class="commander">{E(commander)}</p>""");
            }
        }

        sb.Append("""<ul class="cards" role="list">""");

        foreach (var card in t.Deck)
        {
            // "4×" is read as "4" by synthesisers that skip U+00D7, which next to a
            // card name is indistinguishable from part of the name. The glyph stays
            // for the eye and the words go to speech, as everywhere else on the page.
            // Built on one line: the clipboard reads these as text, and a newline in
            // the markup would land in the pasted markdown as one.
            var copies = card.Count == 1 ? "copy" : "copies";
            var seen = card.Seen ? "" : $"""{Spoken(" · ", ", ")}not seen""";
            var entry = $"""<span aria-hidden="true">{card.Count}×</span><span class="vh">{card.Count} {copies} of</span> {E(card.Name)}{seen}""";

            // The entry line is the summary, so a closed peek reads — and drag-copies
            // — exactly as the plain list item did; the face only exists once opened.
            // No face, no peek: the line renders as it always has, which is also what
            // keeps a build without a card database byte-identical to before.
            if (faces?.GetValueOrDefault(card.Name) is { } face)
            {
                sb.Append($"""<li class="{(card.Seen ? "seen" : "unseen")}"><details class="peek"><summary>{entry}</summary>""");
                AppendFace(sb, face);
                sb.Append("</details></li>");
            }
            else
            {
                sb.Append($"""<li class="{(card.Seen ? "seen" : "unseen")}">{entry}</li>""");
            }
        }

        sb.Append($"""
            </ul>
            <p class="note">{E(TranscriptSummary.DeckNote)}</p>
            </details>
            """);
    }

    /// <summary>
    /// The card itself, as text: cost, type line, rules, statline — everything the
    /// database knows, and nothing it does not. A planeswalker shows no statline
    /// rather than an invented one.
    /// </summary>
    /// <remarks>
    /// The one external link on the page lives here, behind two disclosures and a
    /// click: Scryfall by exact name, because the Alchemy "A-" rebalances make any
    /// id-based mapping lie occasionally, and a name search never does. The page
    /// itself still makes no request — the link is the reader's to follow.
    /// </remarks>
    private static void AppendFace(StringBuilder sb, CardFace f)
    {
        sb.Append("""<div class="face">""");
        var cost = f.ManaCost.Length > 0
            ? $""" <span class="face-cost">{E(f.ManaCost)}</span>"""
            : "";
        sb.Append($"""<p class="face-title"><b>{E(f.Name)}</b>{cost}</p>""");
        if (f.TypeLine.Length > 0)
            sb.Append($"""<p class="face-type">{E(f.TypeLine)}</p>""");
        foreach (var line in f.RulesText)
            sb.Append($"""<p class="face-text">{E(line)}</p>""");
        if (f.Power is not null && f.Toughness is not null)
            sb.Append($"""<p class="face-pt">{E(f.Power)}/{E(f.Toughness)}</p>""");
        sb.Append($"""<p class="face-link"><a href="https://scryfall.com/search?q={Uri.EscapeDataString($"!\"{f.Name}\"")}" target="_blank" rel="noopener">Scryfall<span class="vh">, opens in a new tab</span> <span aria-hidden="true">&#8599;</span></a></p>""");
        sb.Append("</div>");
    }

    /// <summary>
    /// Each turn heading is followed by an ordered list of that turn's lines rather
    /// than a run of paragraphs. A turn genuinely is an ordered sequence of events, and
    /// the list is what a screen reader can act on: entering one announces how many
    /// things happened this turn before reading any of them, each line is announced
    /// with its position, and NVDA's and JAWS's list-item quick keys become a way to
    /// step through a turn. Paragraphs offer none of that. The end-of-turn board
    /// summary stays inside the list — it is the last thing that happened — so a turn
    /// is always exactly one list, whatever order the extractor emits.
    /// </summary>
    private static void AppendSection(StringBuilder sb, Transcript t, Density density)
    {
        var beats = density == Density.Beats;
        var slug = beats ? "beats" : "verbose";
        var label = beats ? "Readable transcript" : "Verbose transcript";

        // Both sections carry every heading, so only one may own the bare anchors or the
        // page would have duplicate ids. The readable section is the one that is
        // visible by default, so it keeps the short names. The rest of each anchor comes
        // from the narrator, which is the only layer that knows whether a turn number
        // needs a game to tell it from the same turn number in the next game.
        var prefix = beats ? "" : "v-";

        sb.Append($"""
            <section id="{slug}" data-density="{slug}" aria-label="{label}"{(beats ? "" : " hidden=\"hidden\"")}>
            """);

        var open = false;
        foreach (var line in Narrator.Narrate(t, density))
        {
            if (line.IsTurnHeader)
            {
                if (open) { sb.Append("</ol>"); open = false; }

                // On a multi-game page the narrator demotes openings and turns to h3, so
                // an h2 there is the game heading and nothing else. Single-game pages keep
                // every heading at h2 and get no class, which is what leaves them untouched.
                var game = line.Level == 2 && t.Games.Count > 1 ? " class=\"game\"" : "";
                sb.Append($"""
                    <h{line.Level} id="{prefix}{line.Anchor}"{game}>{Speech(line.Text)}</h{line.Level}>
                    """);
                continue;
            }

            // `list-style:none` makes Safari drop the list role, and the markers would
            // be noise here, so the role is stated rather than inferred.
            if (!open) { sb.Append("""<ol class="turn" role="list">"""); open = true; }
            sb.Append($"""<li class="{(line.IsBoard ? "board" : "beat")}">{Speech(line.Text)}</li>""");
        }
        if (open) sb.Append("</ol>");

        sb.Append("</section>");
    }

    /// <summary>
    /// Renders one narrated line so it reads correctly aloud as well as on screen.
    /// Four notations need help. The narrator folds repeats into a trailing "×3", and
    /// screen readers at their default punctuation level either skip U+00D7 or read it
    /// as "times" depending on the synthesiser, so "triggers ×3" can arrive as
    /// "triggers 3" — indistinguishable from a turn number. Fields are separated with
    /// "·", which is skipped just as readily, running "You 20 · Opponent 20" together
    /// into one number-soup. A buff reads "1/1 → 6/6", and an arrow that is dropped
    /// leaves two statlines with nothing between them. And the statline itself reads
    /// "2 slash 2", which is the notation spelled out rather than what it means. Every
    /// glyph stays for sighted readers and is taken out of the accessibility tree;
    /// speech gets words and a comma instead, which is what actually makes a
    /// synthesiser pause.
    /// </summary>
    private static string Speech(string text)
    {
        // A leading count means the run was a crowd rather than a repetition — see
        // Narrator.Collapse. The glyph is hidden for the same reason the decklist hides
        // its own: a synthesiser that skips U+00D7 reads "24×" as "24" and runs it into
        // the card name. The count is said at the end instead, where it cannot be
        // mistaken for part of the sentence.
        var crowd = System.Text.RegularExpressions.Regex.Match(text, @"^(\d+)× ");
        if (crowd.Success)
        {
            var n = crowd.Groups[1].Value;
            return $"""<span class="run" aria-hidden="true">{E(n)}× </span>""" +
                   Separated(text[crowd.Length..]) +
                   $"""<span class="vh">, {E(n)} of them, one each</span>""";
        }

        var i = text.LastIndexOf(" ×", StringComparison.Ordinal);
        if (i > 0 &&
            int.TryParse(text.AsSpan(i + 2), NumberStyles.None, CultureInfo.InvariantCulture,
                out var run) && run > 1)
        {
            return Separated(text[..i]) +
                   $"""<span class="run" aria-hidden="true"> ×{run}</span>""" +
                   $"""<span class="vh">, {run} times in a row</span>""";
        }
        return Separated(text);
    }

    /// <summary>
    /// Splits on each glyph before encoding rather than after: <see cref="E"/> replaces
    /// "·" with a numeric reference, so it is no longer there to find by the time the
    /// text is safe to emit.
    /// </summary>
    private static string Separated(string text) =>
        string.Join(Spoken(" · ", ", "), text.Split(" · ").Select(Became));

    private static string Became(string text) =>
        string.Join(Spoken(" → ", " becomes "), text.Split(" → ").Select(Statlines));

    private static string Spoken(string glyph, string words) =>
        $"""<span aria-hidden="true">{glyph}</span><span class="vh">{words}</span>""";

    /// <summary>
    /// Gives every statline a spoken twin: "2/2" is read as "2 power 2 toughness".
    /// </summary>
    /// <remarks>
    /// Runs after encoding rather than before, which is safe in the one way that
    /// matters here. <see cref="E"/> emits three kinds of output, checked rather than
    /// assumed: named entities for the markup characters (<c>&amp;lt;</c>,
    /// <c>&amp;amp;</c>, <c>&amp;quot;</c>), numeric references for the apostrophe and
    /// for Latin-1 (<c>&amp;#39;</c>, <c>&amp;#215;</c>), and the character itself for
    /// everything above U+00FF — "→" and "—" come through untouched. No form of any of
    /// them contains a solidus, so nothing encoding produces can be read as a statline.
    /// <para>
    /// One thing keeps its slash: a counter's <em>name</em>. "+1/+1" is what the counter
    /// is called, and the line that puts one on a creature already carries a count —
    /// spelling the name out turns "gets 1 +1/+1 counter" into "gets 1 plus 1 power plus
    /// 1 toughness counter", burying the number that says how many under the number that
    /// says which kind. A counter is recognised by the word that follows it, which is
    /// exact: across the archive 4,818 signed pairs are followed by "counter" and no
    /// unsigned pair ever is.
    /// </para>
    /// <para>
    /// A signed pair with no counter after it is a pump — "gets -2/-2", "gets +1/+0", and
    /// the "+1/+1" inside a card's own rules text — and it is spoken like any other pair.
    /// That is 1,372 of the 6,190 signed pairs in the archive; the other 4,818 name a
    /// counter and keep their slash.
    /// </para>
    /// <para>
    /// A size can still carry one sign, and that is why the test is both sides rather
    /// than either. Power goes negative under enough shrinking, and the archive holds
    /// 13 of them — "Hare Apparent -3/2", "Mischievous Mystic 0/-1". An earlier pattern
    /// here required a digit straight after the slash, which happened to exclude
    /// "+1/+1" for the right reason and "-3/2" for no reason at all: it matched the
    /// "3/2" inside it and announced "3 power 2 toughness", stating a number that is
    /// not the creature's. Dropping a sign is worse than reading a slash aloud, so the
    /// sign is now part of the match and is spoken as a word.
    /// </para>
    /// <para>
    /// Nothing left is ambiguous by shape alone: a size negative on both sides used to
    /// read as a modifier and stay silent, and now speaks, because the counter test asks
    /// what follows rather than only what the pair looks like.
    /// </para>
    /// <para>
    /// Measured across the whole rendered archive rather than reasoned about: 239
    /// distinct unsigned pairs over 45,011 occurrences, every one a real size and some
    /// of them sizes no card prints (0/30, 434/436); 13 sizes carrying one sign; and 72
    /// modifiers, which the both-sides rule sorts the other way. Mana cannot stray into
    /// range because a hybrid symbol never has digits on both sides — "{2/W}" does not
    /// match, and neither does the "//" of a split card, which has no digits at all.
    /// </para>
    /// </remarks>
    // A sign is part of the pair, never a stray character before it: "-3/2" is matched
    // whole. The lookbehind refuses a sign it did not consume so that a hyphen already
    // joined to a word — the "A-" of a rebalanced card — cannot leave the digits after
    // it looking like a bare size.
    [GeneratedRegex(@"(?<![\w/+-])([+-]?\d+)/([+-]?\d+)(?![\w/])")]
    private static partial Regex Statline();

    private static string Statlines(string text)
    {
        var encoded = E(text);

        return Statline().Replace(encoded, m =>
        {
            var power = m.Groups[1].Value;
            var toughness = m.Groups[2].Value;

            return Signed(power) && Signed(toughness) && NamesACounter(encoded, m.Index + m.Length)
                ? m.Value
                : Spoken(m.Value, $"{Said(power)} power {Said(toughness)} toughness");
        });
    }

    private static bool Signed(string number) => number[0] is '+' or '-';

    /// <summary>Whether the word "counter" follows, which makes the pair a name.</summary>
    private static bool NamesACounter(string text, int after) =>
        text.AsSpan(after).TrimStart().StartsWith("counter", StringComparison.Ordinal);

    // "minus 3" and "plus 3", not "-3" and "+3": a synthesiser reads a leading sign as a
    // word, as "dash", or as nothing at all depending on its punctuation level, and two
    // of those three lose it.
    private static string Said(string number) => number[0] switch
    {
        '-' => $"minus {number[1..]}",
        '+' => $"plus {number[1..]}",
        _ => number
    };


    /// <summary>
    /// The floating links to the matches either side of this one, and back to the top.
    /// </summary>
    /// <remarks>
    /// Plain anchors, rendered by the build. Not buttons and no script: a game page has
    /// to work opened straight off disk, where nothing can fetch, and links additionally
    /// give middle-click, open-in-new-tab, the browser's own history, and link semantics
    /// to a screen reader — none of which a scripted button has.
    /// <para>
    /// It sits at the end of <c>main</c> rather than the start. Keyboard users then reach
    /// it after the transcript, which is exactly when "older match" and "back to top" are
    /// wanted, instead of paying three tab stops before every read; and with styling off
    /// it linearises where it belongs.
    /// </para>
    /// <para>
    /// A link is omitted at the ends of the archive rather than rendered disabled. A
    /// focusable control that does nothing is worse than one that is not there. The
    /// destination is named in the accessible name, because "Older" alone tells a screen
    /// reader user nothing about where they would land.
    /// </para>
    /// <para>
    /// The direction is the visible word and the destination follows it in the hidden
    /// half, so the two halves are one sentence read from the same starting word:
    /// "Older" to the eye, "Older match, 2026-08-12 10:00" to a synthesiser. Naming the
    /// neighbour's date alone, as this did until issue #27, left the reader who most
    /// needs the word as the only one who never got it — an arrow cannot say which way
    /// through an archive it points when the index is sorted newest first. It also means
    /// that with styling off, where the hidden half stops hiding, the two run together
    /// into that same sentence rather than into a stutter.
    /// </para>
    /// </remarks>
    private static string Nav(Neighbours? n)
    {
        if (n is null) return "";

        var links = new List<string>();
        if (n.NewerId is { } newer)
            links.Add($"""
                <a href="{E(newer)}.html"><span aria-hidden="true">&#8592;</span>Newer<span
                class="vh"> match{When(n.NewerWhen)}</span></a>
                """);

        links.Add("""<a href="#top" class="top">Top</a>""");

        if (n.OlderId is { } older)
            links.Add($"""
                <a href="{E(older)}.html">Older<span
                class="vh"> match{When(n.OlderWhen)}</span><span aria-hidden="true">&#8594;</span></a>
                """);

        return $"""<nav class="pager" aria-label="Match navigation">{string.Join("", links)}</nav>""";
    }

    /// <summary>
    /// The neighbour's date, ready to follow "match" in the accessible name, or nothing
    /// when that neighbour has no timestamp — "Newer match" is a whole phrase, and it is
    /// what the fallback has to leave behind rather than a dangling comma.
    /// </summary>
    private static string When(string? when) =>
        string.IsNullOrWhiteSpace(when) ? "" : $", {E(when)}";

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    // Contrast, measured against both backdrops `color-scheme: light dark` produces
    // (#000 on #fff, and white on a #121212–#1e1e1e canvas): the `opacity` dimming
    // here all clears 4.5:1 because it sits on a 21:1 base, so it stays. What did not
    // clear was the warning rule at #c80 (2.96:1 in light), which is now per-scheme.
    // `header{opacity:.95}` is gone: it multiplied with every child's own opacity for
    // no visible gain, and dimmed the focus ring on the controls inside it.
    private const string Css = """
        :root{color-scheme:light dark}
        body{font:16px/1.6 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
             max-width:52rem;margin:0 auto;padding:2rem 1rem}
        header{border-bottom:1px solid currentColor;padding-bottom:1rem;margin-bottom:1rem}
        h1{font-size:1.4rem;margin:.2rem 0}
        .sub{opacity:.7;margin:.2rem 0 .8rem}
        .back a{opacity:.7;text-underline-offset:.2em}
        h2,h3{font-size:1rem;margin:1.6rem 0 .4rem;padding-top:.6rem;
           border-top:1px dashed currentColor;opacity:.85}
        h2.game{font-size:1.2rem;margin-top:2.4rem;border-top-style:solid;opacity:1}
        .turn{list-style:none;margin:.15rem 0;padding:0 0 0 1.5rem}
        .beat{margin:.15rem 0}
        .board{margin:.15rem 0;opacity:.6;font-style:italic;font-size:.92em}
        /* Clipping hides this from the eye but not from a mouse — selection is not a
           paint-time effect — so without user-select a dragged selection pastes both
           halves: "1×1 copy of Plains". It is a selection property and leaves the
           accessibility tree alone, so a synthesiser still reads these. */
        .vh{position:absolute;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;
            clip:rect(0 0 0 0);clip-path:inset(50%);white-space:nowrap;border:0;
            -webkit-user-select:none;user-select:none}
        .warn{border-left:3px solid #a35b00;padding-left:.8rem;opacity:.85}
        .deck{margin:.6rem 0}
        .deck summary{cursor:pointer}
        .deck .commander{margin:.4rem 0 0 1.5rem}
        .deck .cards{list-style:none;margin:.4rem 0;padding:0 0 0 1.5rem}
        .deck .cards li{margin:.1rem 0}
        .deck .unseen{opacity:.65}
        .peek summary{cursor:pointer}
        .face{margin:.4rem 0 .6rem 1.1rem;padding:.5rem .7rem;max-width:24rem;
              border:1px solid rgba(128,128,128,.45);border-radius:.4rem;
              background:rgba(128,128,128,.08)}
        .face p{margin:.25rem 0}
        .face-title{display:flex;justify-content:space-between;gap:.6rem}
        .face-cost{white-space:nowrap}
        .face-type{font-style:italic;font-size:.92em;opacity:.85}
        .face-text{font-size:.92em}
        .face-pt{text-align:right;font-weight:600}
        .face-link{font-size:.85em}
        .note{font-size:.9rem;opacity:.75;margin:.4rem 0 0 1.5rem}
        .build{margin-top:2rem;padding-top:.8rem;font-size:.8rem;opacity:.55;
               border-top:1px solid currentColor}
        .controls{display:flex;gap:.5rem;flex-wrap:wrap;align-items:center;margin:.4rem 0 0}
        button{font:inherit;padding:.3rem .8rem;cursor:pointer;min-height:1.75rem}
        .status{font-size:.85rem;opacity:.75}
        :focus-visible{outline:2px solid currentColor;outline-offset:2px}
        /* Room for the pager, plus the strip iOS Safari reserves for its own chrome —
           without it a fixed bar sits on top of the last lines of the transcript. */
        body{padding-bottom:5rem}
        .pager{position:fixed;left:0;right:0;bottom:0;display:flex;gap:.5rem;
               justify-content:center;align-items:center;flex-wrap:wrap;
               padding:.5rem .5rem calc(.5rem + env(safe-area-inset-bottom));
               background:Canvas;border-top:1px solid rgba(128,128,128,.45)}
        /* 2.75rem, not the 1.75rem the header buttons use. Both clear WCAG 2.2 AA's
           24px floor, but a floating overlay is the worst place to sit at the floor. */
        .pager a{display:inline-flex;align-items:center;gap:.35rem;
                 min-height:2.75rem;padding:0 .9rem;border-radius:.4rem;
                 border:1px solid rgba(128,128,128,.45);text-decoration:none;
                 font-size:.9rem}
        .pager a:hover{background:rgba(128,128,128,.15)}
        .pager .top{opacity:.75}
        /* Never hidden on scroll: a control that leaves the tab order mid-session is
           worse for a keyboard user than one that is always there. */
        @media (prefers-reduced-motion: no-preference){html{scroll-behavior:smooth}}
        @media print{
          .controls,.back,.pager{display:none}
          body{padding-bottom:0}
          details{display:block}
          details>summary{list-style:none}
          h2,h3{break-after:avoid}
          li{break-inside:avoid}
        }
        @media (prefers-color-scheme:dark){.warn{border-left-color:#e0a33a}}
        @media (forced-colors:active){
          .sub,.board,.warn,.status,.back a,h2,h3,.note,.deck .unseen,.build,.pager .top{opacity:1}
        }
        """;

    private const string Script = """
        (function () {
          var status = document.getElementById('status');

          // Assigning a live region the string it already holds is a no-op, so copying
          // twice would say nothing the second time. Clearing first, in its own task,
          // makes a repeated message announce again.
          function say(message) {
            if (!status) return;
            status.textContent = '';
            setTimeout(function () { status.textContent = message; }, 60);
          }

          var toggle = document.getElementById('density-toggle');
          var beats = document.querySelector('[data-density="beats"]');
          var verbose = document.querySelector('[data-density="verbose"]');
          if (toggle && beats && verbose) {
            // The label names the next action instead of carrying aria-pressed —
            // ARIA's toggle-button guidance is to do one or the other, never both.
            // What a label cannot do is report that the page changed under you, so
            // the status region says which view is now showing.
            toggle.addEventListener('click', function () {
              var showVerbose = verbose.hidden;
              verbose.hidden = !showVerbose;
              beats.hidden = showVerbose;
              toggle.textContent = showVerbose ? 'Show readable beats' : 'Show verbose detail';
              say(showVerbose ? 'Verbose transcript shown.' : 'Readable transcript shown.');
            });
          }

          var copy = document.getElementById('copy-button');
          var copyId = document.getElementById('copy-id');
          if (!copy && !copyId && !document.getElementById('copy-anon')) return;

          function visibleSection() {
            var sections = document.querySelectorAll('section[data-density]');
            for (var i = 0; i < sections.length; i++) {
              if (!sections[i].hidden) return sections[i];
            }
            return null;
          }

          // Spelled-out repeat counts exist for screen readers; the clipboard keeps the
          // "×3" the glyph shows, so pasted text matches the markdown export.
          function textOf(node) {
            var clone = node.cloneNode(true);
            var spoken = clone.querySelectorAll('.vh');
            for (var i = 0; i < spoken.length; i++) {
              spoken[i].parentNode.removeChild(spoken[i]);
            }
            return clone.textContent.trim();
          }

          // Gathered from the transcript sections and the title only, so the button
          // labels never end up in the clipboard.
          function asMarkdown(heading) {
            var section = visibleSection();
            if (!section) return '';

            var title = document.querySelector('header h1');
            var sub = document.querySelector('header .sub');
            // All of them, not the first: a match can be both cut short and missing
            // messages from its middle, and a copied transcript that mentions only one
            // of those is a copied transcript that misleads.
            var warns = document.querySelectorAll('.warn');
            var out = [];

            // The only line of a transcript that names either player. The body says
            // "You" and "Opponent" already — checked across 400 archived transcripts,
            // where a screen name appears 796 times in a heading and never once in a
            // beat. So sanitizing replaces this line rather than sweeping the text for
            // names, which would also rewrite any card whose name contains one.
            if (title) out.push('# ' + (heading || textOf(title)), '');
            if (sub) out.push('*' + textOf(sub) + '*', '');
            for (var w = 0; w < warns.length; w++) out.push('> ' + textOf(warns[w]), '');

            // In the same place the markdown export puts it, and in the same style: a
            // pasted transcript that carries turn durations has to carry what they mean.
            var timing = document.getElementById('timing-note');
            if (timing) out.push('*' + textOf(timing) + '*', '');

            // Copied whether or not it is expanded, and in the same place the markdown
            // export puts it: the two are meant to be the same document, and a reader
            // who collapsed a list did not ask to leave it out of the paste.
            var deck = document.getElementById('deck');
            if (deck) {
              out.push('## ' + textOf(deck.querySelector('summary')), '');
              // The commander travels with the deck it commands, as a plain sentence
              // rather than a list line — the same shape the markdown export writes.
              var commander = deck.querySelector('.commander');
              if (commander) out.push(textOf(commander), '');
              var cards = deck.querySelectorAll('li');
              for (var c = 0; c < cards.length; c++) out.push('- ' + textOf(cards[c]));
              out.push('', '*' + textOf(deck.querySelector('.note')) + '*', '');
            }

            // h3 as well as h2. A multi-game page puts its games at h2 and demotes the
            // openings and turns beneath them to h3, so selecting only h2 copied the
            // three game headings and dropped all twenty-five turn boundaries — a
            // Bo3 transcript arrived in chat as one unbroken run of beats.
            var nodes = section.querySelectorAll('h2, h3, li.beat, li.board');
            for (var i = 0; i < nodes.length; i++) {
              var node = nodes[i];
              var text = textOf(node);
              if (node.tagName === 'H2') out.push('', '## ' + text);
              else if (node.tagName === 'H3') out.push('', '### ' + text);
              else if (node.className === 'board') out.push('  *' + text + '*');
              else out.push('- ' + text);
            }
            return out.join('\n').replace(/\n{3,}/g, '\n\n') + '\n';
          }

          // navigator.clipboard needs a secure context, and file:// does not qualify
          // in every browser, so fall back rather than fail silently. Focus returns to
          // the button that asked, which is not always the transcript one.
          function legacyCopy(text, button, copied) {
            var area = document.createElement('textarea');
            area.value = text;
            area.setAttribute('readonly', '');
            area.style.position = 'fixed';
            area.style.top = '-1000px';
            document.body.appendChild(area);
            area.select();
            var ok = false;
            try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
            document.body.removeChild(area);
            button.focus();
            say(ok ? copied : 'Copy failed.');
          }

          function copyText(text, button, copied) {
            if (navigator.clipboard && navigator.clipboard.writeText) {
              navigator.clipboard.writeText(text).then(
                function () { say(copied); },
                function () { legacyCopy(text, button, copied); });
            } else {
              legacyCopy(text, button, copied);
            }
          }

          if (copy) {
            copy.addEventListener('click', function () {
              copyText(asMarkdown(), copy, 'Transcript copied.');
            });
          }

          // A second button rather than a toggle on the first. A toggle would have to
          // report its state and would leave "Copy transcript" meaning two things
          // depending on a setting the reader has to remember; a button that says what
          // it does says it every time.
          var anon = document.getElementById('copy-anon');
          if (anon) {
            anon.addEventListener('click', function () {
              copyText(asMarkdown(anon.dataset.title), anon, 'Transcript copied without names.');
            });
          }

          // The id is what `mtga-pbp why <matchId>` wants and what identifies a match
          // in a bug report, and it appears nowhere on the page as text — it is the
          // file name. Copying it beats reading a GUID out of the address bar.
          if (copyId) {
            copyId.addEventListener('click', function () {
              copyText(copyId.dataset.id || '', copyId, 'Game ID copied.');
            });
          }
        })();
        """;
}
