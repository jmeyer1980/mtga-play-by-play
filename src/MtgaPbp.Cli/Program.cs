using System.Net.Sockets;
using MtgaPbp.Core;
using MtgaPbp.Render;

namespace MtgaPbp.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        // Answered before anything is loaded or written. Neither was a known option, so
        // both fell through to "do everything": asking the tool its version re-scanned a
        // 35 MB log, rewrote every page, and ran the pruner.
        if (args.Any(a => a is "--version" or "-V"))
        {
            Banner.Write(art: false);
            return 0;
        }
        if (args.Any(a => a is "--help" or "-h" or "/?" or "help"))
        {
            Banner.Write(art: false);
            Usage();
            return 0;
        }

        var exeDir = AppContext.BaseDirectory;
        var cfg = Config.Load(exeDir);
        var (command, operands) = Parse(args);
        var open = cfg.OpenAfterBuild || args.Contains("--open");

        // Identity first, on every command that a person reads.
        if (command is not ("keep" or "unkeep")) Banner.Write();

        try
        {
            return command switch
            {
                "capture" => Capture(cfg),
                "build" => Build(cfg, open),
                "stats" => Stats(cfg),
                "watch" => Watch(cfg, operands),
                "collection" => ImportCollection(cfg, operands.FirstOrDefault()),
                "why" => Why.Run(cfg, operands.FirstOrDefault(), operands.Skip(1).ToArray()),
                "keep" => Favorite(cfg, operands.FirstOrDefault(), on: true),
                "unkeep" => Favorite(cfg, operands.FirstOrDefault(), on: false),
                "all" => Capture(cfg) is var c && c != 0 ? c : Build(cfg, open),
                _ => Usage()
            };
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static readonly string[] Commands =
        ["capture", "build", "stats", "watch", "keep", "unkeep", "collection", "why"];

    private static readonly string[] Options = ["--open", "--rebuild"];

    /// <summary>
    /// Splits the arguments into a command and its operands.
    /// </summary>
    /// <remarks>
    /// Tolerates <c>--watch</c> for <c>watch</c>: the dashed form is a natural thing
    /// to type, and it used to be discarded as an unknown option, which ran a plain
    /// capture-and-build instead and looked exactly like watch starting and exiting.
    /// The command and its operands have to be worked out together, because with
    /// <c>--watch 8793</c> the port is the first positional argument rather than the
    /// second.
    /// </remarks>
    private static (string Command, string[] Operands) Parse(string[] args)
    {
        var positional = args
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        if (positional.Length > 0 && Commands.Contains(positional[0], StringComparer.Ordinal))
            return (positional[0], positional[1..]);

        var dashed = args.Select(a => a.TrimStart('-'))
                         .FirstOrDefault(a => Commands.Contains(a, StringComparer.Ordinal));
        if (dashed is not null) return (dashed, positional);

        // An unrecognised word still goes to the switch, which answers with usage.
        if (positional.Length > 0) return (positional[0], positional[1..]);

        foreach (var unknown in args.Where(a =>
                     a.StartsWith("--", StringComparison.Ordinal) &&
                     !Options.Contains(a, StringComparer.Ordinal)))
            Console.Error.WriteLine($"warning: ignoring unknown option {unknown}");

        return ("all", []);
    }

    private static int Usage()
    {
        Console.WriteLine("""
            mtga-pbp                  capture new matches, then rebuild the site
            mtga-pbp --open           ... and open the report in your browser
            mtga-pbp capture          capture only
            mtga-pbp build            re-derive the whole site from the archive
            mtga-pbp build --rebuild  same as above (build never caches)
            mtga-pbp stats            unhandled annotations and unresolved cards
            mtga-pbp watch [port]     serve the report and keep it live (default 8787)
            mtga-pbp collection <file> import a collection exported from elsewhere
            mtga-pbp why <matchId> [turns] show turns beside the log behind them,
                                           one (13), several (13 14, or 13,14)
                                           or a range (13-15)
            mtga-pbp keep <matchId>   never prune this match
            mtga-pbp unkeep <matchId> allow it to be pruned again

            Set "MaxArchivedMatches" in mtga-pbp.json to cap how many matches are kept;
            the oldest are dropped as new ones arrive. Kept matches never count against
            the cap. It defaults to 0, meaning no limit.

            Set "OpenAfterBuild": true in mtga-pbp.json to always open the report —
            useful when launching by double-click, where this window closes too fast
            to read the path below.
            """);
        return 1;
    }

    /// <summary>
    /// Passes the scan through untouched, showing each envelope to the ledger on its way.
    /// </summary>
    /// <remarks>
    /// A side channel rather than a second scan, because the log is 28 MB and reading it
    /// twice to answer two questions would double the cost of every capture. The slicer
    /// keeps only what falls between a match's start and end, and an inventory snapshot
    /// falls outside every match — so without this it reaches nothing (#51).
    /// </remarks>
    private static IEnumerable<LogEnvelope> Offering(
        IEnumerable<LogEnvelope> envelopes, InventoryLedger ledger)
    {
        foreach (var envelope in envelopes)
        {
            ledger.Observe(envelope.Root);
            yield return envelope;
        }
    }

    private static int Capture(Config cfg) => CaptureCore(cfg, quiet: false).Exit;

    /// <summary>
    /// Reads the logs into the archive and reports how many matches were written.
    /// </summary>
    /// <returns>
    /// <c>Written</c> counts matches that were <em>added or updated</em>. A match
    /// first seen mid-game is archived incomplete and rewritten when it ends, which
    /// leaves the archive the same size — so callers must not use the match count to
    /// decide whether anything happened.
    /// </returns>
    private static (int Exit, int Written) CaptureCore(Config cfg, bool quiet)
    {
        var archive = new RawArchive(cfg.ArchiveDir);
        var ledger = new InventoryLedger(cfg.ArchiveDir);
        var stats = new ScanStats();
        var written = 0;
        var sawAnyLog = false;

        foreach (var log in cfg.LogPaths.Where(File.Exists))
        {
            sawAnyLog = true;
            foreach (var slice in MatchSlicer.Slice(Offering(LogScanner.Scan(log, stats), ledger)))
                if (archive.Write(slice)) written++;
        }

        // After the logs, because the ledger records where the last of them left the
        // player. Its own no-op cases are cheap, so this is called even on a run that
        // captured nothing.
        ledger.Commit();

        if (!sawAnyLog)
        {
            Console.Error.WriteLine(
                "error: no Arena log found. Looked for:\n  " +
                string.Join("\n  ", cfg.LogPaths) +
                "\nSet \"LogPaths\" in mtga-pbp.json if Arena is installed elsewhere.");
            return (2, 0);
        }

        if (!quiet)
            Console.WriteLine(
                $"captured {written} new match{(written == 1 ? "" : "es")} " +
                $"({stats.JsonLines:N0} records read)");

        // Said out loud even when nothing new was captured: it is the one condition
        // under which a transcript that looks finished is not, and a count buried in a
        // stats subcommand nobody runs is the same as no warning at all.
        var withheld = stats.SummarizedMessages + stats.TornEnvelopes;
        if (!quiet && withheld > 0)
        {
            var causes = new List<string>();
            if (stats.SummarizedMessages > 0)
                causes.Add($"{stats.SummarizedMessages} message(s) the log summarized instead of recording");
            if (stats.TornEnvelopes > 0)
                causes.Add($"{stats.TornEnvelopes} line(s) that ended mid-message");
            Console.WriteLine(
                $"warning: {string.Join(" and ", causes)}. The matches involved are " +
                "marked as missing data in the report.");
        }

        Prune(cfg, archive);
        return (0, written);
    }

    /// <summary>
    /// Enforces the retention cap, deleting the rendered output for anything dropped
    /// so the report does not link to pages that no longer exist.
    /// </summary>
    private static void Prune(Config cfg, RawArchive archive)
    {
        if (cfg.MaxArchivedMatches <= 0) return;

        var removed = archive.Prune(cfg.MaxArchivedMatches);
        foreach (var id in removed)
        {
            foreach (var path in new[]
            {
                Path.Combine(cfg.OutputDir, "games", $"{id}.html"),
                Path.Combine(cfg.OutputDir, "text", $"{id}.md"),
            })
                if (File.Exists(path)) File.Delete(path);
        }

        if (removed.Count > 0)
            Console.WriteLine(
                $"pruned {removed.Count} match(es) past the {cfg.MaxArchivedMatches} cap " +
                "(favourites kept)");
    }

    /// <summary>
    /// Serves the report and keeps it current: polls the log, re-captures when it
    /// grows, and tells any open page to refresh itself.
    /// </summary>
    /// <remarks>
    /// Polling rather than FileSystemWatcher — Arena writes to Player.log constantly,
    /// so change notifications fire continuously and tell us nothing useful. Length is
    /// the honest signal, and a shrink means Arena restarted and truncated the log.
    /// </remarks>
    private static int Watch(Config cfg, string[] operands)
    {
        var port = int.TryParse(operands.FirstOrDefault(), out var p) ? p : 8787;
        var interval = TimeSpan.FromSeconds(3);

        Directory.CreateDirectory(cfg.OutputDir);
        Capture(cfg);

        // The first build's state is kept rather than discarded: without it the board
        // could not be drawn until a match finished, so `watch` started on an existing
        // archive showed the plain build text and nothing else — sometimes for hours.
        IReadOnlyList<MatchSummary> firstRows = [];
        IndexStats? firstStats = null;
        Nudge? firstNudge = null;
        var code = Build(cfg, open: false, observed: (rows, st, nudge) =>
        {
            firstRows = rows; firstStats = st; firstNudge = nudge;
        });
        if (code != 0) return code;

        using var server = new LiveServer(cfg.OutputDir, port);
        server.OnFavorite = (id, on) =>
        {
            var ok = new RawArchive(cfg.ArchiveDir).SetFavorite(id, on);
            // Observed with a no-op rather than left unobserved: keeping a match changes
            // the archive and not the night's record, so there is nothing to repaint —
            // but an unobserved build prints the nudge itself, straight into the middle
            // of the pinned block, bypassing the erase that keeps it intact.
            if (ok) { Build(cfg, open: false, quiet: true, observed: (_, _, _) => { }); server.NotifyChanged(); }
            return ok;
        };

        try { server.Start(); }
        catch (SocketException ex)
        {
            Console.Error.WriteLine(
                $"could not listen on port {port} ({ex.Message}). " +
                "Pass a different one: mtga-pbp watch 9000");
            return 2;
        }

        Console.WriteLine($"watching {cfg.LogPaths.FirstOrDefault()}");
        OpenInBrowser(server.Url);

        // The standing state is drawn once and repainted; only the notable lines scroll.
        // See LiveBoard for why that split exists — 41 lines an evening saying "report
        // updated" is how the one line that mattered came to be 38 scrolls out of sight.
        var board = new LiveBoard();
        var beats = new List<Beat>();
        var said = new HashSet<string>(StringComparer.Ordinal);

        void Repaint(IReadOnlyList<MatchSummary> rows, IndexStats st, Nudge? nudge)
        {
            var tonight = st.Sessions.FirstOrDefault();
            var byId = rows.GroupBy(r => r.MatchId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // Rebuilt from the session every time rather than appended to. Several
            // matches can finish inside one poll, and a match first seen mid-game is
            // archived unfinished and rewritten when it ends — so an append-only tail
            // both drops the earlier ones and lists the rewritten one twice.
            beats.Clear();
            foreach (var id in tonight?.MatchIds.AsEnumerable().Reverse().Take(Scoreboard.Recent)
                               ?? Enumerable.Empty<string>())
            {
                if (!byId.TryGetValue(id, out var m)) continue;
                beats.Add(new Beat(
                    m.Date.Length >= 16 ? m.Date[11..16] : m.Date,
                    Deck(st, m) ?? m.EventName,
                    m.Incomplete ? "unfinished" : m.Result));
            }

            var newest = tonight?.MatchIds.Count > 0 && byId.TryGetValue(tonight.MatchIds[^1], out var last)
                ? last
                : null;
            var playing = newest is null ? null : Deck(st, newest);

            // Above the block, where it scrolls and stays — but only the first time it
            // is said. `watch` rebuilds on a favourite toggle too, and a nudge repeated
            // on every rebuild is the noise this whole change is about removing.
            if (nudge is not null && said.Add(nudge.Text))
                board.Say($"[{DateTime.Now:HH:mm:ss}] ** {nudge.Text}");

            // The same recommendation the nudge would make, standing rather than said
            // once: the answer to "which one next" has to be there when the question is
            // asked, not only at the moment a rule happened to trip.
            var slug = newest is null ? null : st.DeckOf.GetValueOrDefault(newest.MatchId);
            board.Draw(Scoreboard.Lines(
                tonight, beats, playing, SessionCoach.NextUp(st, slug),
                server.Url, DateTime.Now, board.Width, board.Height));
        }

        // Read from the session's own deck list rather than from the by-deck records,
        // which keep only matches that reached a result: a deck whose first game is
        // still unfinished has a cluster and a label but no row there, and looking it up
        // that way left the deck in play unmarked and its result line named after the
        // event instead.
        static string? Deck(IndexStats st, MatchSummary m) =>
            st.DeckOf.TryGetValue(m.MatchId, out var slug)
                ? st.ByDeck.FirstOrDefault(d => d.Slug == slug)?.Name ?? st.LabelOf.GetValueOrDefault(slug)
                : null;

        // Drawn before the first match of the evening, so the window says what it is
        // watching from the moment it opens.
        if (firstStats is not null) Repaint(firstRows, firstStats, firstNudge);

        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        var sizes = new Dictionary<string, long>();
        while (!stop.Wait(interval))
        {
            var grew = false;
            foreach (var log in cfg.LogPaths.Where(File.Exists))
            {
                var length = new FileInfo(log).Length;
                if (sizes.TryGetValue(log, out var was) && was == length) continue;
                sizes[log] = length;
                grew = true;
            }
            if (!grew) continue;

            // Rebuild when the archive was written to, not when it got bigger. A match
            // that started mid-poll is archived incomplete and rewritten once it ends,
            // and that rewrite leaves the count unchanged — which is exactly the update
            // worth showing, since only then does the transcript know how it finished.
            var (exit, written) = CaptureCore(cfg, quiet: true);
            if (exit != 0 || written == 0) continue;

            Build(cfg, open: false, quiet: true, observed: Repaint);
            server.NotifyChanged();
        }

        Console.WriteLine();
        Console.WriteLine("stopped.");
        return 0;
    }

    /// <summary>
    /// Imports a collection exported from somewhere else and checks it against the card
    /// database, so a file that half-resolves is caught here rather than quietly
    /// answering "you do not own that" later.
    /// </summary>
    /// <remarks>
    /// Arena no longer writes the collection to its log — verified against a clean login,
    /// a craft and a full played session — so there is nothing to extract and this is the
    /// supported route in. Any source works: a tracker's copy button, a memory-scanning
    /// exporter, a hand-written list. The tool stays a program that only reads files.
    /// </remarks>
    private static int ImportCollection(Config cfg, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("""
                usage: mtga-pbp collection <file>

                The file is Arena's own decklist text, one card per line:

                  4 Hare Apparent
                  2 Ethereal Armor (DSK)

                Export one from any tracker's collection view, or paste from Arena.
                """);
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"error: no such file: {path}");
            return 2;
        }

        var owned = CollectionFile.Parse(File.ReadAllLines(path), out var unreadable);
        if (owned.Count == 0)
        {
            Console.Error.WriteLine(
                $"error: {path} held no card entries. Expected lines like \"4 Hare Apparent\".");
            return 2;
        }

        using var cards = OpenCards(cfg, out _);
        var known = new HashSet<string>(cards.AllNames(), StringComparer.OrdinalIgnoreCase);
        var unmatched = owned.Where(o => !known.Contains(o.Name)).ToList();

        var target = Path.Combine(cfg.OutputDir, "collection.txt");
        Directory.CreateDirectory(cfg.OutputDir);
        File.WriteAllLines(target, owned.Select(o => $"{o.Count} {o.Name}"));

        Console.WriteLine($"{owned.Count:N0} distinct cards, {owned.Sum(o => o.Count):N0} copies");
        Console.WriteLine($"  matched against the card database: {owned.Count - unmatched.Count:N0}");
        Console.WriteLine($"  stored: {target}");

        // Said out loud, and never as a total on its own. A name the database does not
        // know is a card this tool will report you do not own, which is the one wrong
        // answer a collection can give.
        if (unmatched.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{unmatched.Count:N0} name(s) the card database does not know:");
            foreach (var u in unmatched.Take(10)) Console.WriteLine($"    {u.Count} {u.Name}");
            if (unmatched.Count > 10) Console.WriteLine($"    ... and {unmatched.Count - 10:N0} more");
        }

        if (unreadable.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{unreadable.Count:N0} line(s) that began with a count and could not be read:");
            foreach (var u in unreadable.Take(5)) Console.WriteLine($"    {u}");
        }

        return 0;
    }

    private static int Favorite(Config cfg, string? matchId, bool on)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            Console.Error.WriteLine($"usage: mtga-pbp {(on ? "keep" : "unkeep")} <matchId>");
            return 1;
        }

        var archive = new RawArchive(cfg.ArchiveDir);
        if (!archive.SetFavorite(matchId, on))
        {
            Console.Error.WriteLine($"no archived match with id {matchId}");
            return 1;
        }

        Console.WriteLine($"{matchId} {(on ? "kept — it will never be pruned" : "no longer kept")}");
        return 0;
    }

    private static CardDb OpenCards(Config cfg, out string path)
    {
        path = CardDb.FindDatabase(cfg.CardDbPath)
            ?? throw new FileNotFoundException(
                "Card database not found. Looked for Raw_CardDatabase_*.mtga under the Arena " +
                "install directories. Set \"CardDbPath\" in mtga-pbp.json to point at it.");
        return new CardDb(path);
    }

    /// <summary>
    /// Always re-derives every archived match — parsing 24 matches takes well under a
    /// second, so there is no cache to invalidate. <c>--rebuild</c> is accepted and
    /// documented because that is the guarantee it names, but it changes nothing today.
    /// </summary>
    /// <param name="observed">
    /// Handed the state this build worked out, for a caller that wants to draw it. The
    /// alternative was recomputing the sessions and the coach's verdict in `watch`,
    /// which would have been the second implementation of both and free to disagree
    /// with the page it sits beside.
    /// </param>
    private static int Build(Config cfg, bool open, bool quiet = false,
        Action<IReadOnlyList<MatchSummary>, IndexStats, Nudge?>? observed = null)
    {
        var archive = new RawArchive(cfg.ArchiveDir);
        using var cards = OpenCards(cfg, out var dbPath);

        var gamesDir = Path.Combine(cfg.OutputDir, "games");
        var textDir = Path.Combine(cfg.OutputDir, "text");
        Directory.CreateDirectory(gamesDir);
        Directory.CreateDirectory(textDir);

        var extractor = new EventExtractor(cards);
        var summaries = new List<MatchSummary>();
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        // Every match in the order they were played, worked out before anything is
        // extracted. The ledger already knows when each match started, so a page can be
        // told its neighbours on the single pass that renders it — no second pass over
        // the archive, and no holding two hundred transcripts in memory to sort them.
        var chronological = archive.MatchIds()
            .Select(id => (Id: id, At: archive.Meta(id)?.StartedAtMs ?? 0))
            .OrderBy(m => m.At)
            .ToList();
        var position = chronological
            .Select((m, i) => (m.Id, i))
            .ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);

        Neighbours NeighboursOf(string id)
        {
            var i = position[id];
            var newer = i + 1 < chronological.Count ? chronological[i + 1] : default;
            var older = i > 0 ? chronological[i - 1] : default;
            static string When(long at) =>
                TranscriptSummary.Date(at).ToString("yyyy-MM-dd HH:mm");
            return new Neighbours(
                newer.Id, newer.Id is null ? null : When(newer.At),
                older.Id, older.Id is null ? null : When(older.At));
        }

        foreach (var matchId in archive.MatchIds())
        {
            var gamePath = Path.Combine(gamesDir, $"{matchId}.html");
            var lines = archive.ReadLines(matchId);
            if (lines.Count == 0) continue;

            var transcript = extractor.Extract(matchId, lines);
            File.WriteAllText(gamePath,
                GamePageRenderer.Render(transcript, NeighboursOf(matchId)));
            File.WriteAllText(Path.Combine(textDir, $"{matchId}.md"),
                MarkdownRenderer.Render(transcript));
            summaries.Add(IndexRenderer.Summarize(transcript) with
            {
                Favorite = archive.Meta(matchId)?.Favorite ?? false
            });

            foreach (var c in transcript.UnresolvedNames.Keys)
                unresolved.Add(c);
        }

        // Asked with the clock, so it answers about the sitting in progress and stays
        // quiet on a build run the next morning — see SessionCoach.Check. A one-shot
        // build after an evening's play gets the same nudge `watch` would have shown,
        // which is the point: the report is the report either way.
        var stats = IndexStats.From(summaries);
        var nudge = SessionCoach.Check(
            summaries, stats,
            silenced: null, nowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var indexPath = Path.GetFullPath(Path.Combine(cfg.OutputDir, "index.html"));
        // Read rather than carried through from capture: `build` re-derives the whole
        // site from the archive without reading a log at all, and the ledger is part of
        // the archive.
        var inventory = new InventoryLedger(cfg.ArchiveDir).Entries;
        File.WriteAllText(indexPath, IndexRenderer.Render(summaries, nudge, inventory));

        // A caller that draws its own screen takes the state and says it its own way;
        // everyone else gets the plain line. `watch` is the former, so the nudge does
        // not get printed twice.
        if (observed is not null) observed(summaries, stats, nudge);
        else if (nudge is not null && !quiet) Console.WriteLine($"  ** {nudge.Text}");
        if (unresolved.Count > 0)
            File.WriteAllLines(Path.Combine(cfg.OutputDir, "unresolved.txt"), unresolved);

        if (!quiet)
        {
            Console.WriteLine($"built {summaries.Count} game(s)");
            Console.WriteLine();
            Console.WriteLine($"  report:  {indexPath}");
            Console.WriteLine($"  cards:   {dbPath}");
            Console.WriteLine();
            if (unresolved.Count > 0)
                Console.WriteLine($"{unresolved.Count} unresolved card id(s) — see unresolved.txt");
        }

        if (open) OpenInBrowser(indexPath);
        return 0;
    }

    /// <summary>
    /// Opens the report with the shell's default handler. A failure here is a
    /// convenience not working, never a reason to fail a build that already
    /// succeeded — the path is printed above either way.
    /// </summary>
    private static void OpenInBrowser(string path)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not open the report ({ex.Message}); open it yourself at:");
            Console.Error.WriteLine($"  {path}");
        }
    }

    private static int Stats(Config cfg)
    {
        var archive = new RawArchive(cfg.ArchiveDir);
        using var cards = OpenCards(cfg, out _);

        var extractor = new EventExtractor(cards);
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        var persistent = new Dictionary<string, int>(StringComparer.Ordinal);
        var unresolved = new Dictionary<string, int>(StringComparer.Ordinal);
        var matches = 0;
        var gaps = new List<LogGap>();
        var matchesWithGaps = 0;

        foreach (var matchId in archive.MatchIds())
        {
            var lines = archive.ReadLines(matchId);
            if (lines.Count == 0) continue;
            matches++;
            var t = extractor.Extract(matchId, lines);
            foreach (var (k, v) in t.UnknownAnnotations)
                unknown[k] = unknown.GetValueOrDefault(k) + v;
            foreach (var (k, v) in t.UnknownPersistentAnnotations)
                persistent[k] = persistent.GetValueOrDefault(k) + v;
            foreach (var (c, n) in t.UnresolvedNames)
                unresolved[c] = unresolved.GetValueOrDefault(c) + n;
            if (t.Gaps.Count > 0) matchesWithGaps++;
            gaps.AddRange(t.Gaps);
        }

        Console.WriteLine($"{matches} match(es) in archive\n");
        ReportGaps(gaps, matchesWithGaps, matches);
        Console.WriteLine("unhandled annotation types:");
        foreach (var (k, v) in unknown.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {v,6}  {k}");
        if (unknown.Count == 0) Console.WriteLine("  (none)");

        // A second list rather than more rows in the first, because these come from a
        // different array and mean something different: a streamed type nobody handles
        // is a hole in the narration, while a persistent one is a standing fact nobody
        // has mined yet. Types that are read, and types examined and deliberately
        // dropped, are both absent — EventExtractor holds the two sets and the reasons.
        Console.WriteLine("\nunmined persistent annotation types (diagnostic, not narrated):");
        foreach (var (k, v) in persistent.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {v,6}  {k}");
        if (persistent.Count == 0) Console.WriteLine("  (none)");

        Console.WriteLine("\nunresolved cards:");
        foreach (var (k, v) in unresolved.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {v,6}  {k}");
        if (unresolved.Count == 0) Console.WriteLine("  (none)");
        return 0;
    }

    /// <summary>
    /// Reports what the log did not account for. Printed first and phrased as a
    /// fraction of the archive, because this is the only number here that says a
    /// transcript may be wrong rather than merely thin — the two lists below are
    /// things the renderer handled imperfectly, this is something it never saw.
    /// </summary>
    private static void ReportGaps(List<LogGap> gaps, int affected, int matches)
    {
        Console.WriteLine($"matches missing data: {affected} of {matches}");
        if (gaps.Count == 0)
        {
            Console.WriteLine("  (none — every archived match is accounted for)\n");
            return;
        }

        var summarized = gaps.Where(g => g.Kind == LogGapKind.Summarized).ToList();
        var torn = gaps.Count - summarized.Count;
        if (summarized.Count > 0)
            Console.WriteLine(
                $"  {summarized.Count,6}  message(s) summarized by Arena instead of logged " +
                $"({summarized.Sum(g => g.GameObjects)} game objects, " +
                $"{summarized.Sum(g => g.Annotations)} annotations withheld)");
        if (torn > 0)
            Console.WriteLine($"  {torn,6}  envelope(s) that ended mid-message");
        Console.WriteLine();
    }
}
