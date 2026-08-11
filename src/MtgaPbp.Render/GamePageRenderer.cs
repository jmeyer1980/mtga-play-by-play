using System.Globalization;
using System.Net;
using System.Text;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public static class GamePageRenderer
{
    public static string Render(Transcript t)
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
              <h1>{E(TranscriptSummary.Title(t))}</h1>
              <p class="sub">{Speech(TranscriptSummary.Subtitle(t))}</p>
              <div class="controls">
                <button id="density-toggle" type="button"
                        aria-controls="beats verbose">Show verbose detail</button>
                <button id="copy-button" type="button">Copy transcript</button>
                <span id="status" class="status" role="status"></span>
              </div>
            </header>
            """);

        if (t.Incomplete)
            sb.Append("""<p class="warn">This match is incomplete — the log was rotated before it finished.</p>""");

        AppendSection(sb, t, Density.Beats);
        AppendSection(sb, t, Density.Verbose);

        sb.Append($"""
            </main>
            <script>{Script}</script></body></html>
            """);
        return sb.ToString();
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

        // Both sections carry every turn, so only one may own the `t{n}` anchors or the
        // page would have duplicate ids. The readable section is the one that is
        // visible by default, so it keeps the short names.
        var prefix = beats ? "t" : "v-t";

        sb.Append($"""
            <section id="{slug}" data-density="{slug}" aria-label="{label}"{(beats ? "" : " hidden=\"hidden\"")}>
            """);

        var open = false;
        foreach (var line in Narrator.Narrate(t, density))
        {
            if (line.IsTurnHeader)
            {
                if (open) { sb.Append("</ol>"); open = false; }
                sb.Append($"""<h2 id="{prefix}{line.Turn}">{Speech(line.Text)}</h2>""");
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
    /// Two notations need help. The narrator folds repeats into a trailing "×3", and
    /// screen readers at their default punctuation level either skip U+00D7 or read it
    /// as "times" depending on the synthesiser, so "triggers ×3" can arrive as
    /// "triggers 3" — indistinguishable from a turn number. Fields are separated with
    /// "·", which is skipped just as readily, running "You 20 · Opponent 20" together
    /// into one number-soup. Both glyphs stay for sighted readers and are taken out of
    /// the accessibility tree; speech gets words and a comma instead, which is what
    /// actually makes a synthesiser pause.
    /// </summary>
    private static string Speech(string text)
    {
        var i = text.LastIndexOf(" ×", StringComparison.Ordinal);
        if (i > 0 &&
            int.TryParse(text.AsSpan(i + 2), NumberStyles.None, CultureInfo.InvariantCulture,
                out var n) && n > 1)
        {
            return Separated(text[..i]) +
                   $"""<span class="run" aria-hidden="true"> ×{n}</span>""" +
                   $"""<span class="vh">, {n} times in a row</span>""";
        }
        return Separated(text);
    }

    private static string Separated(string text) => string.Join(
        """<span aria-hidden="true"> · </span><span class="vh">, </span>""",
        text.Split(" · ").Select(E));

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
        h2{font-size:1rem;margin:1.6rem 0 .4rem;padding-top:.6rem;
           border-top:1px dashed currentColor;opacity:.85}
        .turn{list-style:none;margin:.15rem 0;padding:0 0 0 1.5rem}
        .beat{margin:.15rem 0}
        .board{margin:.15rem 0;opacity:.6;font-style:italic;font-size:.92em}
        .vh{position:absolute;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;
            clip:rect(0 0 0 0);clip-path:inset(50%);white-space:nowrap;border:0}
        .warn{border-left:3px solid #a35b00;padding-left:.8rem;opacity:.85}
        .controls{display:flex;gap:.5rem;flex-wrap:wrap;align-items:center;margin:.4rem 0 0}
        button{font:inherit;padding:.3rem .8rem;cursor:pointer;min-height:1.75rem}
        .status{font-size:.85rem;opacity:.75}
        :focus-visible{outline:2px solid currentColor;outline-offset:2px}
        @media (prefers-color-scheme:dark){.warn{border-left-color:#e0a33a}}
        @media (forced-colors:active){
          .sub,.board,.warn,.status,.back a,h2{opacity:1}
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
          if (!copy) return;

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
          function asMarkdown() {
            var section = visibleSection();
            if (!section) return '';

            var title = document.querySelector('header h1');
            var sub = document.querySelector('header .sub');
            var warn = document.querySelector('.warn');
            var out = [];

            if (title) out.push('# ' + textOf(title), '');
            if (sub) out.push('*' + textOf(sub) + '*', '');
            if (warn) out.push('> ' + textOf(warn), '');

            var nodes = section.querySelectorAll('h2, li.beat, li.board');
            for (var i = 0; i < nodes.length; i++) {
              var node = nodes[i];
              var text = textOf(node);
              if (node.tagName === 'H2') out.push('', '## ' + text);
              else if (node.className === 'board') out.push('  *' + text + '*');
              else out.push('- ' + text);
            }
            return out.join('\n').replace(/\n{3,}/g, '\n\n') + '\n';
          }

          // navigator.clipboard needs a secure context, and file:// does not qualify
          // in every browser, so fall back rather than fail silently.
          function legacyCopy(text) {
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
            copy.focus();
            say(ok ? 'Transcript copied.' : 'Copy failed.');
          }

          copy.addEventListener('click', function () {
            var text = asMarkdown();
            if (navigator.clipboard && navigator.clipboard.writeText) {
              navigator.clipboard.writeText(text).then(
                function () { say('Transcript copied.'); },
                function () { legacyCopy(text); });
            } else {
              legacyCopy(text);
            }
          });
        })();
        """;
}
