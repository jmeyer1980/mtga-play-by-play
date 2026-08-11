using System.Net;
using System.Text;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public sealed record MatchSummary(
    string MatchId, string Date, long SortKey, string EventName,
    string Opponent, string Result, int Turns, bool Incomplete,
    IReadOnlyList<string> Cards, bool Favorite = false);

public static class IndexRenderer
{
    public static MatchSummary Summarize(Transcript t) => new(
        t.MatchId,
        TranscriptSummary.Date(t).ToString("yyyy-MM-dd HH:mm"),
        t.StartedAtMs,
        t.EventName,
        t.Opponent?.ScreenName ?? "Opponent",
        TranscriptSummary.Result(t),
        TranscriptSummary.Turns(t),
        t.Incomplete,
        t.CardsSeen.OrderBy(c => c, StringComparer.Ordinal).ToList());

    /// <summary>
    /// Rows are rendered statically rather than built by script: the page then works
    /// with JavaScript disabled, the browser's own find-in-page sees every opponent
    /// and card name, and each link is a real anchor. Search is progressive
    /// enhancement over a data-search attribute — no fetch, which browsers block on
    /// file:// anyway.
    /// </summary>
    public static string Render(IEnumerable<MatchSummary> rows)
    {
        var ordered = rows.OrderByDescending(r => r.SortKey).ToList();

        var body = new StringBuilder();
        if (ordered.Count == 0)
        {
            body.Append(
                "<p class=\"empty\">No games archived yet. Play a match, then run " +
                "<code>mtga-pbp</code>.</p>");
        }
        else
        {
            body.Append("""
                <table id="rows"><thead><tr><th></th><th>Date</th><th>Event</th><th>Opponent</th>
                <th>Result</th><th>Turns</th></tr></thead><tbody id="data">
                """);
            foreach (var r in ordered)
            {
                var cls = r.Result.StartsWith("Won", StringComparison.Ordinal) ? "win" : "loss";
                var haystack = string.Join(' ',
                    r.Opponent, r.EventName, r.Result, r.Date, string.Join(' ', r.Cards))
                    .ToLowerInvariant();

                body.Append($"""
                    <tr data-search="{E(haystack)}">
                    <td><button class="star{(r.Favorite ? " on" : "")}" type="button"
                        data-id="{E(r.MatchId)}" title="Kept matches are never pruned"
                        >{(r.Favorite ? "★" : "☆")}</button></td>
                    <td><a href="games/{E(Uri.EscapeDataString(r.MatchId))}.html">{E(r.Date)}</a></td>
                    <td>{E(r.EventName)}</td><td>{E(r.Opponent)}</td>
                    <td class="{cls}">{E(r.Result)}{(r.Incomplete ? " *" : "")}</td>
                    <td>{r.Turns}</td></tr>
                    """);
            }
            body.Append("</tbody></table>");
        }

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MTGA Play-by-Play</title>
            <style>{{Css}}</style></head><body>
            <h1>MTGA Play-by-Play</h1>
            <p class="sub">{{ordered.Count}} game{{(ordered.Count == 1 ? "" : "s")}} archived<span id="live">· live</span></p>
            <input id="q" type="search" placeholder="Search opponent, event, result, or card…"
                   autocomplete="off">
            <p id="count" class="sub"></p>
            {{body}}
            <script>{{Script}}</script>
            </body></html>
            """;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        :root{color-scheme:light dark}
        body{font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;
             max-width:64rem;margin:0 auto;padding:2rem 1rem}
        h1{font-size:1.5rem;margin:0 0 .2rem}
        .sub{opacity:.65;margin:.2rem 0 1rem}
        #q{width:100%;font:inherit;padding:.55rem .7rem;margin-bottom:1rem;
           border:1px solid currentColor;border-radius:.4rem;background:transparent;color:inherit}
        table{width:100%;border-collapse:collapse}
        th,td{text-align:left;padding:.45rem .6rem;border-bottom:1px solid rgba(128,128,128,.3)}
        th{font-size:.8rem;text-transform:uppercase;letter-spacing:.04em;opacity:.6}
        tbody tr:hover{background:rgba(128,128,128,.12)}
        a{color:inherit}
        .win{color:#2a2}.loss{opacity:.7}
        .empty{opacity:.7}
        .star{background:none;border:0;cursor:default;font-size:1rem;padding:0 .2rem;
              color:inherit;opacity:.35}
        .star.on{opacity:1;color:#e8b923}
        body.live .star{cursor:pointer}
        body.live .star:hover{opacity:.8}
        #live{display:none;font-size:.8rem;opacity:.6;margin-left:.5rem}
        body.live #live{display:inline}
        code{font-family:ui-monospace,Menlo,Consolas,monospace}
        """;

    private const string Script = """
        (function () {
          var tbody = document.getElementById('data');
          var q = document.getElementById('q');
          var count = document.getElementById('count');
          if (!tbody || !q || !count) return;

          let rows = Array.prototype.slice.call(tbody.querySelectorAll('tr'));

          function apply() {
            var terms = q.value.toLowerCase().split(/\s+/).filter(Boolean);
            var shown = 0;
            rows.forEach(function (tr) {
              var hay = tr.getAttribute('data-search') || '';
              var match = terms.every(function (t) { return hay.indexOf(t) !== -1; });
              tr.hidden = !match;
              if (match) shown++;
            });
            count.textContent = shown + ' of ' + rows.length + ' shown';
          }

          q.addEventListener('input', apply);
          apply();

          // Everything below only works when the page is served by `mtga-pbp watch`.
          // Opened from disk it stays a plain static report, which is the point.
          if (location.protocol.indexOf('http') !== 0) return;
          document.body.classList.add('live');

          function wireStars() {
            tbody.querySelectorAll('.star').forEach(function (b) {
              b.addEventListener('click', function () {
                var on = !b.classList.contains('on');
                fetch('/api/favorite/' + encodeURIComponent(b.dataset.id) + '?on=' + on,
                      { method: 'POST' })
                  .then(function (r) { if (r.ok) { b.classList.toggle('on', on);
                                                   b.textContent = on ? '★' : '☆'; } });
              });
            });
          }
          wireStars();

          // Re-read the freshly written index and swap in its rows, so the search box
          // and scroll position survive what would otherwise be a reload.
          function refresh() {
            fetch('/', { cache: 'no-store' })
              .then(function (r) { return r.text(); })
              .then(function (html) {
                var next = new DOMParser().parseFromString(html, 'text/html')
                                          .querySelector('#data');
                if (!next) return;
                tbody.innerHTML = next.innerHTML;
                rows = Array.prototype.slice.call(tbody.querySelectorAll('tr'));
                wireStars();
                apply();
              });
          }

          var es = new EventSource('/api/events');
          es.addEventListener('changed', refresh);
        })();
        """;
}
