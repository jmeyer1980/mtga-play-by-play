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
              <button id="density-toggle" type="button">Show verbose</button>
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
        .warn{border-left:3px solid #c80;padding-left:.8rem;opacity:.85}
        button{font:inherit;padding:.3rem .8rem;cursor:pointer}
        """;

    private const string Script = """
        (function () {
          var btn = document.getElementById('density-toggle');
          var beats = document.querySelector('[data-density="beats"]');
          var verbose = document.querySelector('[data-density="verbose"]');
          if (!btn || !beats || !verbose) return;
          btn.addEventListener('click', function () {
            var showVerbose = verbose.hidden;
            verbose.hidden = !showVerbose;
            beats.hidden = showVerbose;
            btn.textContent = showVerbose ? 'Show beats' : 'Show verbose';
          });
        })();
        """;
}
