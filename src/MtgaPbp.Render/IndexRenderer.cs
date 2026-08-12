using System.Net;
using System.Text;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public sealed record MatchSummary(
    string MatchId, string Date, long SortKey, string EventName,
    string Opponent, string Result, int Turns, bool Incomplete,
    IReadOnlyList<string> Cards, bool Favorite = false, bool HasGaps = false,

    /// <summary>
    /// How long the match ran, or null when the log does not know — which is every
    /// incomplete match. Carried as a <see cref="TimeSpan"/> rather than pre-formatted
    /// because the table needs it twice, abbreviated for the column and spelled out for
    /// a synthesiser, and a single string could only ever be one of those.
    /// </summary>
    TimeSpan? Length = null);

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
        t.CardsSeen.OrderBy(c => c, StringComparer.Ordinal).ToList(),
        HasGaps: t.Gaps.Count > 0,
        Length: TurnClock.MatchLength(t));

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
            // No rows means nothing to search, so the field, the counter and the
            // footnotes are all omitted rather than rendered inert.
            body.Append(
                "<p class=\"empty\">No games archived yet. Play a match, then run " +
                "<code>mtga-pbp</code>.</p>");
        }
        else
        {
            // The counter is server-rendered rather than filled in by script: it is a
            // live region, and a region that gains its first text after load announces
            // that text. Starting it correct means the first announcement is a real
            // change, and the count is also right with JavaScript turned off.
            body.Append($"""
                <label for="q">Search matches</label>
                <input id="q" type="search" placeholder="opponent, event, result, or card"
                       autocomplete="off" aria-describedby="count" />
                <p id="count" class="sub" role="status">{ordered.Count} of {ordered.Count} shown</p>
                <table id="rows">
                <caption class="vh">Archived matches, most recent first</caption>
                <thead><tr><th scope="col">Keep</th><th scope="col">Date</th>
                <th scope="col">Event</th><th scope="col">Opponent</th>
                <th scope="col">Result</th><th scope="col">Turns</th>
                <th scope="col">Length</th></tr></thead><tbody id="data">
                """);
            foreach (var r in ordered)
            {
                var cls = r.Result.StartsWith("Won", StringComparison.Ordinal) ? "win" : "loss";
                var haystack = string.Join(' ',
                    r.Opponent, r.EventName, r.Result, r.Date, string.Join(' ', r.Cards))
                    .ToLowerInvariant();

                // The star is a toggle button, so its state rides on aria-pressed and
                // its name stays constant; the glyph is decoration and is hidden from
                // assistive technology, which would otherwise read it as "white star".
                // It ships disabled because keeping a match needs the local server —
                // an unavailable control is better than one that silently does nothing.
                body.Append($"""
                    <tr data-search="{E(haystack)}">
                    <td><button class="star{(r.Favorite ? " on" : "")}" type="button" disabled="disabled"
                        aria-pressed="{(r.Favorite ? "true" : "false")}"
                        aria-describedby="keep-note" data-id="{E(r.MatchId)}"
                        aria-label="Keep the {E(r.Date)} match against {E(r.Opponent)}"
                        ><span aria-hidden="true">{(r.Favorite ? "★" : "☆")}</span></button></td>
                    <th scope="row"><a href="games/{E(Uri.EscapeDataString(r.MatchId))}.html">{E(r.Date)}</a></th>
                    <td>{E(r.EventName)}</td><td>{E(r.Opponent)}</td>
                    <td class="{cls}">{E(r.Result)}{Incomplete(r)}{Gaps(r)}</td>
                    <td>{r.Turns}</td><td>{Length(r)}</td></tr>
                    """);
            }
            body.Append("</tbody></table>");

            if (ordered.Any(r => r.Incomplete))
            {
                body.Append("""
                    <p class="note" id="incomplete-note"><span aria-hidden="true">*</span>
                    Incomplete — the log was rotated before the match finished.</p>
                    """);
            }

            // A separate mark and a separate footnote, because this is a separate
            // failure: the match above ran out of log, this one ran out of truth.
            if (ordered.Any(r => r.HasGaps))
            {
                body.Append("""
                    <p class="note" id="gaps-note"><span aria-hidden="true">†</span>
                    Missing data — the log left out part of the match, so the transcript
                    is not a complete account of it.</p>
                    """);
            }

            body.Append("""
                <p class="note" id="keep-note">Keeping a match protects it from pruning.
                It works while the report is served by <code>mtga-pbp watch</code>; opened
                from a file this page is read-only, so the Keep buttons are disabled.</p>
                <p id="status" class="vh" role="status"></p>
                """);
        }

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>MTGA Play-by-Play</title>
            <style>{{Css}}</style></head><body>
            <main>
            <h1>MTGA Play-by-Play</h1>
            <p class="sub">{{ordered.Count}} game{{(ordered.Count == 1 ? "" : "s")}} archived<span
                id="live"> <span aria-hidden="true">· </span>live updating</span></p>
            {{body}}
            </main>
            <footer class="build">{{E(BuildInfo.Line)}}</footer>
            <script>{{Script}}</script>
            </body></html>
            """;
    }

    /// <summary>
    /// The asterisk is a sighted-reader shorthand explained by the footnote below the
    /// table; screen readers get the word instead, because "star" next to "Lost 0-1"
    /// means nothing on its own.
    /// </summary>
    private static string Incomplete(MatchSummary r) => r.Incomplete
        ? """<span aria-hidden="true"> *</span><span class="vh"> (incomplete)</span>"""
        : "";

    /// <summary>
    /// A dagger rather than a second asterisk, so the two footnotes stay tellable
    /// apart at a glance; and the spoken form says what it means, for the same reason
    /// the asterisk above does.
    /// </summary>
    private static string Gaps(MatchSummary r) => r.HasGaps
        ? """<span aria-hidden="true"> †</span><span class="vh"> (missing data)</span>"""
        : "";

    /// <summary>
    /// How long the match ran. The column shows "12m 4s" because a table column has to
    /// stay narrow; speech gets "12 minutes 4 seconds", because the abbreviation is a
    /// run of letters and digits sitting immediately after a turn count, and a
    /// synthesiser running the two together produces a number that is in the table
    /// nowhere. Same split as the decklist's "4×".
    /// <para>
    /// An incomplete match leaves the cell empty rather than showing how much of it was
    /// captured. That figure is a real one, but it is not the match's length, and in a
    /// column headed "Length" it would be read as one.
    /// </para>
    /// </summary>
    private static string Length(MatchSummary r) => r.Length is not { } d
        ? ""
        : $"""<span aria-hidden="true">{E(TurnClock.Format(d))}</span>""" +
          $"""<span class="vh">{E(TurnClock.Spoken(d))}</span>""";

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    // Contrast is measured against the two backdrops `color-scheme: light dark` can
    // produce: #fff/#000 in light, and a #121212–#1e1e1e canvas with white text in
    // dark. Dimming with `opacity` is kept where the result still clears 4.5:1 — over
    // a 21:1 base it does — but the win colour and both star colours did not, so they
    // are stated per scheme instead. `prefers-color-scheme` rather than `light-dark()`
    // so the fix is not conditional on a recent engine.
    private const string Css = """
        :root{color-scheme:light dark}
        body{font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;
             max-width:64rem;margin:0 auto;padding:2rem 1rem}
        h1{font-size:1.5rem;margin:0 0 .2rem}
        .sub{opacity:.65;margin:.2rem 0 1rem}
        .vh{position:absolute;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;
            clip:rect(0 0 0 0);clip-path:inset(50%);white-space:nowrap;border:0}
        label{display:block;font-size:.85rem;margin-bottom:.25rem}
        #q{width:100%;font:inherit;padding:.55rem .7rem;margin-bottom:1rem;
           border:1px solid currentColor;border-radius:.4rem;background:transparent;color:inherit}
        table{width:100%;border-collapse:collapse}
        caption{text-align:left}
        th,td{text-align:left;padding:.45rem .6rem;border-bottom:1px solid rgba(128,128,128,.35)}
        thead th{font-size:.8rem;text-transform:uppercase;letter-spacing:.04em;opacity:.6}
        tbody th{font-weight:400}
        tbody tr:hover{background:rgba(128,128,128,.12)}
        a{color:inherit}
        tbody a{display:inline-block;padding-block:.15rem}
        .win{color:#137333}
        .loss{opacity:.7}
        .empty{opacity:.7}
        .note{opacity:.7;font-size:.85rem;max-width:44rem}
        body.live #keep-note{display:none}
        .star{background:none;border:0;cursor:default;font-size:1rem;padding:0;color:#666666;
              display:inline-flex;align-items:center;justify-content:center;
              min-width:1.75rem;min-height:1.75rem;border-radius:.3rem}
        .star.on{color:#8a6100}
        .star:enabled{cursor:pointer}
        .star:enabled:hover{background:rgba(128,128,128,.2)}
        :focus-visible{outline:2px solid currentColor;outline-offset:2px}
        #live{display:none;font-size:.8rem;opacity:.6}
        body.live #live{display:inline}
        code{font-family:ui-monospace,Menlo,Consolas,monospace}
        .build{margin-top:2rem;padding-top:.8rem;font-size:.8rem;opacity:.55;
               border-top:1px solid rgba(128,128,128,.35)}
        @media (prefers-color-scheme:dark){
          .win{color:#4ade80}
          .star{color:#9a9a9a}
          .star.on{color:#f2c14a}
        }
        @media (forced-colors:active){
          .sub,.loss,.empty,.note,th,#live,.build{opacity:1}
          .star{color:ButtonText}
          .star.on{color:Highlight}
        }
        """;

    private const string Script = """
        (function () {
          var tbody = document.getElementById('data');
          var q = document.getElementById('q');
          var count = document.getElementById('count');
          var status = document.getElementById('status');
          if (!tbody || !q || !count) return;

          let rows = Array.prototype.slice.call(tbody.querySelectorAll('tr'));
          var counting;

          // Assigning a live region the string it already holds is a no-op, so a
          // second identical message would be silent. Clearing first, in its own
          // task, makes it announce again.
          function announce(message) {
            if (!status) return;
            status.textContent = '';
            setTimeout(function () { status.textContent = message; }, 60);
          }

          function apply() {
            var terms = q.value.toLowerCase().split(/\s+/).filter(Boolean);
            var shown = 0;
            rows.forEach(function (tr) {
              var hay = tr.getAttribute('data-search') || '';
              var match = terms.every(function (t) { return hay.indexOf(t) !== -1; });
              tr.hidden = !match;
              if (match) shown++;
            });

            // The counter is a live region, so writing it on every keystroke would
            // queue one announcement per character typed. Filtering stays immediate;
            // only the announcement waits for a pause. And writing the same string
            // back still replaces the text node, which a live region would announce,
            // so it is only written when it actually changed.
            var text = shown + ' of ' + rows.length + ' shown';
            clearTimeout(counting);
            counting = setTimeout(function () {
              if (count.textContent !== text) count.textContent = text;
            }, 300);
          }

          q.addEventListener('input', apply);
          apply();

          // Everything below only works when the page is served by `mtga-pbp watch`.
          // Opened from disk it stays a plain static report, which is the point.
          if (location.protocol.indexOf('http') !== 0) return;
          document.body.classList.add('live');

          function setStar(b, on) {
            b.classList.toggle('on', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
            (b.firstElementChild || b).textContent = on ? '★' : '☆';
          }

          function wireStars() {
            tbody.querySelectorAll('.star').forEach(function (b) {
              // Served, so the control works: drop both the disabled state and the
              // note explaining why it did not, which would otherwise still be read.
              b.disabled = false;
              b.removeAttribute('aria-describedby');
              b.addEventListener('click', function () {
                var on = b.getAttribute('aria-pressed') !== 'true';
                fetch('/api/favorite/' + encodeURIComponent(b.dataset.id) + '?on=' + on,
                      { method: 'POST' })
                  .then(function (r) {
                    if (r.ok) setStar(b, on);
                    else announce('Could not change the keep state.');
                  }, function () { announce('Could not change the keep state.'); });
              });
            });
          }
          wireStars();

          // Re-read the freshly written index and swap in its rows, so the search box
          // and scroll position survive what would otherwise be a reload.
          function refresh() {
            var active = document.activeElement;
            var focused = active && active.classList && active.classList.contains('star')
              ? active.dataset.id : null;

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
                // Replacing the rows destroys the node that had focus, which would
                // drop a keyboard or screen-reader user back to the top of the page.
                if (focused) {
                  var again = tbody.querySelector('.star[data-id="' + focused + '"]');
                  if (again) again.focus();
                }
                announce('Match list updated.');
              });
          }

          var es = new EventSource('/api/events');
          es.addEventListener('changed', refresh);
        })();
        """;
}
