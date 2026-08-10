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
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(TranscriptSummary.Title(t))}</title>
            <style>{Css}</style></head><body>
            <header>
              <p class="back"><a href="../index.html">&larr; All games</a></p>
              <h1>{E(TranscriptSummary.Title(t))}</h1>
              <p class="sub">{E(TranscriptSummary.Subtitle(t))}</p>
              <p class="controls">
                <button id="density-toggle" type="button">Show verbose</button>
                <button id="copy-button" type="button">Copy transcript</button>
              </p>
            </header>
            """);

        if (t.Incomplete)
            sb.Append("""<p class="warn">This match is incomplete — the log was rotated before it finished.</p>""");

        foreach (var density in new[] { Density.Beats, Density.Verbose })
        {
            var slug = density == Density.Beats ? "beats" : "verbose";
            sb.Append($"""<section data-density="{slug}"{(density == Density.Verbose ? " hidden" : "")}>""");
            foreach (var line in Narrator.Narrate(t, density))
            {
                if (line.IsTurnHeader)
                    sb.Append($"""<h2 id="t{line.Turn}">{E(line.Text)}</h2>""");
                else if (line.IsBoard)
                    sb.Append($"""<p class="board">{E(line.Text)}</p>""");
                else
                    sb.Append($"""<p class="beat">{E(line.Text)}</p>""");
            }
            sb.Append("</section>");
        }

        sb.Append($"""
            <script>{Script}</script></body></html>
            """);
        return sb.ToString();
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        :root{color-scheme:light dark}
        body{font:16px/1.6 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
             max-width:52rem;margin:0 auto;padding:2rem 1rem}
        header{border-bottom:1px solid currentColor;padding-bottom:1rem;margin-bottom:1rem;opacity:.95}
        h1{font-size:1.4rem;margin:.2rem 0}
        .sub{opacity:.7;margin:.2rem 0 .8rem}
        .back a{text-decoration:none;opacity:.7}
        h2{font-size:1rem;margin:1.6rem 0 .4rem;padding-top:.6rem;border-top:1px dashed currentColor;opacity:.85}
        .beat{margin:.15rem 0 .15rem 1.5rem}
        .board{margin:.15rem 0 .15rem 1.5rem;opacity:.6;font-style:italic;font-size:.92em}
        .warn{border-left:3px solid #c80;padding-left:.8rem;opacity:.85}
        .controls{display:flex;gap:.5rem;flex-wrap:wrap;margin:.4rem 0 0}
        button{font:inherit;padding:.3rem .8rem;cursor:pointer}
        """;

    private const string Script = """
        (function () {
          var toggle = document.getElementById('density-toggle');
          var beats = document.querySelector('[data-density="beats"]');
          var verbose = document.querySelector('[data-density="verbose"]');
          if (toggle && beats && verbose) {
            toggle.addEventListener('click', function () {
              var showVerbose = verbose.hidden;
              verbose.hidden = !showVerbose;
              beats.hidden = showVerbose;
              toggle.textContent = showVerbose ? 'Show beats' : 'Show verbose';
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

          // Gathered from the transcript sections and the title only, so the button
          // labels never end up in the clipboard.
          function asMarkdown() {
            var section = visibleSection();
            if (!section) return '';

            var title = document.querySelector('header h1');
            var sub = document.querySelector('header .sub');
            var warn = document.querySelector('.warn');
            var out = [];

            if (title) out.push('# ' + title.textContent.trim(), '');
            if (sub) out.push('*' + sub.textContent.trim() + '*', '');
            if (warn) out.push('> ' + warn.textContent.trim(), '');

            var nodes = section.querySelectorAll('h2, p.beat, p.board');
            for (var i = 0; i < nodes.length; i++) {
              var node = nodes[i];
              var text = node.textContent.trim();
              if (node.tagName === 'H2') out.push('', '## ' + text);
              else if (node.className === 'board') out.push('  *' + text + '*');
              else out.push('- ' + text);
            }
            return out.join('\n').replace(/\n{3,}/g, '\n\n') + '\n';
          }

          function flash(message) {
            var original = copy.getAttribute('data-label') || copy.textContent;
            copy.setAttribute('data-label', original);
            copy.textContent = message;
            setTimeout(function () { copy.textContent = original; }, 1400);
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
            flash(ok ? 'Copied' : 'Copy failed');
          }

          copy.addEventListener('click', function () {
            var text = asMarkdown();
            if (navigator.clipboard && navigator.clipboard.writeText) {
              navigator.clipboard.writeText(text).then(
                function () { flash('Copied'); },
                function () { legacyCopy(text); });
            } else {
              legacyCopy(text);
            }
          });
        })();
        """;
}
