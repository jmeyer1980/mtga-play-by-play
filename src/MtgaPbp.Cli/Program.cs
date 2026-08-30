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
        // Opt-in for a prune large enough to look like a mistake. Deliberately a flag
        // and not a config key — see Prune.
        var prune = args.Contains("--prune");
        // Ignore the build cache and re-derive every match. The flag has always named
        // this guarantee; until #122 there was no cache for it to bypass.
        var rebuild = args.Contains("--rebuild");

        // Identity first, on every command that a person reads.
        if (command is not ("keep" or "unkeep")) Banner.Write();

        try
        {
            return command switch
            {
                "capture" => Capture(cfg, prune),
                "build" => Build(cfg, open, rebuild: rebuild),
                "stats" => Stats(cfg),
                "watch" => Watch(cfg, operands, prune, rebuild),
                "collection" => ImportCollection(cfg, operands.FirstOrDefault()),
                "why" => Why.Run(cfg, operands.FirstOrDefault(), operands.Skip(1).ToArray()),
                "keep" => Favorite(cfg, operands.FirstOrDefault(), on: true),
                "unkeep" => Favorite(cfg, operands.FirstOrDefault(), on: false),
                "all" => Capture(cfg, prune) is var c && c != 0 ? c : Build(cfg, open, rebuild: rebuild),
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

    private static readonly string[] Options = ["--open", "--rebuild", "--prune"];

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
            mtga-pbp build --rebuild  rebuild every match, ignoring the build cache
                                      (on `watch`, applies to its first build only)
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
            the cap. It defaults to 0, meaning no limit. A cap that would delete more
            than a tenth of the archive at once is not applied on its own — it says what
            it would do and waits for `mtga-pbp capture --prune`, because the archive is
            the only copy and there is no undo.

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

    private static int Capture(Config cfg, bool prune, RawArchive? shared = null) =>
        CaptureCore(cfg, quiet: false, prune, shared).Exit;

    /// <summary>
    /// A sentence naming an archive that holds matches when the configured one does
    /// not, or null when there is nothing worth pointing at.
    /// </summary>
    /// <remarks>
    /// Counted by reading the raw directory rather than by opening a
    /// <see cref="RawArchive"/>: that constructor creates its directories, so probing
    /// a location with one would conjure the very archive it was asked about.
    /// <para>
    /// This does NOT catch the case that motivated #134, and should not be read as
    /// doing so. When an upgrade overwrote the user's config, what it forgot was a
    /// custom <c>ArchiveDir</c> — so the run that follows is pointed at the default,
    /// and the path worth naming is the one the replaced file was the only record of.
    /// Nothing can name it. That is why the fix for #134 is that the shipped config no
    /// longer uses the user's filename at all, and why this is a second, smaller net:
    /// it catches a configured directory that is empty while the default is not.
    /// </para>
    /// </remarks>
    private static string? Misplaced(Config cfg)
    {
        // Every failure here is silence, never a crash. This is a hint printed beside
        // a capture that has already succeeded, and there is no version of "could not
        // read a directory I was only going to count" that is worth costing someone
        // the run. ArchiveDir is user-supplied and reaches Path.GetFullPath, which
        // rejects malformed paths; the enumeration below can fail part-way through on
        // a permission or IO error even after Directory.Exists said yes.
        try
        {
            var fallback = Config.Default().ArchiveDir;
            if (string.IsNullOrWhiteSpace(cfg.ArchiveDir) || Same(fallback, cfg.ArchiveDir)) return null;

            var raw = Path.Combine(fallback, "raw");
            if (!Directory.Exists(raw)) return null;

            var held = Directory.EnumerateFiles(raw, "*.json.gz").Count();
            if (held == 0) return null;

            return $"the archive at {cfg.ArchiveDir} is empty, but {fallback} holds " +
                   $"{held} match{(held == 1 ? "" : "es")}. If that is where your history " +
                   $"is, set \"ArchiveDir\" in {Config.UserFile} to point at it.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            return null;
        }

        static bool Same(string a, string b) =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the logs into the archive and reports how many matches were written.
    /// </summary>
    /// <returns>
    /// <c>Written</c> counts matches that were <em>added or updated</em>. A match
    /// first seen mid-game is archived incomplete and rewritten when it ends, which
    /// leaves the archive the same size — so callers must not use the match count to
    /// decide whether anything happened. <c>Drift</c> is <see cref="DriftCanary"/>'s
    /// warning — non-null when the logs carried match-shaped volume that produced no
    /// matches at all — returned rather than printed so `watch` can route it through
    /// the board instead of corrupting the pinned block with a bare write.
    /// <c>Prune</c> is the retention cap's account of itself and travels for the same
    /// reason — null when the cap is off or had nothing to do, and otherwise carrying
    /// both what to say and whether it is reporting a deletion or refusing one.
    /// </returns>
    private static (int Exit, int Written, string? Drift, PruneReport? Prune) CaptureCore(
        Config cfg, bool quiet, bool prune, RawArchive? shared = null)
    {
        // One ledger per archive, when the caller has one to lend. `watch` runs a poll
        // loop, a rebuild and a web request against the same files at the same time,
        // and three instances means three copies of index.json that overwrite each
        // other wholesale — see RawArchive's lock (#146). Standalone commands pass
        // nothing and get their own, which is correct: they are the only writer.
        var archive = shared ?? new RawArchive(cfg.ArchiveDir);
        // Asked before the logs are read, because the question is whether this archive
        // was already empty when the run began and capture is about to make it not be.
        // Count rather than MatchIds().Any(): the ids are a materialized snapshot now
        // (see RawArchive.MatchIds), so asking for all of them to find out whether there
        // are none would copy every id in the archive on every poll under `watch`.
        var startedEmpty = archive.Count == 0;
        var ledger = new InventoryLedger(cfg.ArchiveDir);
        var stats = new ScanStats();
        var written = 0;
        var slices = 0;
        var sawAnyLog = false;

        foreach (var log in cfg.LogPaths.Where(File.Exists))
        {
            sawAnyLog = true;
            foreach (var slice in MatchSlicer.Slice(Offering(LogScanner.Scan(log, stats), ledger)))
            {
                slices++;
                if (archive.Write(slice)) written++;
            }
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
            return (2, 0, null, null);
        }

        if (!quiet)
            Console.WriteLine(
                $"captured {written} new match{(written == 1 ? "" : "es")} " +
                $"({stats.JsonLines:N0} records read)");

        if (!quiet && startedEmpty && Misplaced(cfg) is { } elsewhere)
            Console.WriteLine($"note: {elsewhere}");

        // Returned rather than printed so `watch` can route it through the board —
        // a bare WriteLine under quiet mode would land inside the pinned block and
        // corrupt the repaint, which is why quiet mode exists.
        var drift = DriftCanary.Warn(stats, slices);
        if (!quiet && drift is not null) Console.WriteLine($"warning: {drift}");

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

        var pruned = Prune(cfg, archive, prune);
        if (!quiet && pruned is { } report) Console.WriteLine(report.Message);
        return (0, written, drift, pruned);
    }

    /// <summary>
    /// Enforces the retention cap, deleting the rendered output for anything dropped so
    /// the report does not link to pages that no longer exist — unless the cap would
    /// take a large enough bite to look like a mistake, in which case it says what it
    /// would have done and does nothing.
    /// </summary>
    /// <returns>
    /// What happened, or null when nothing did and nothing was withheld. Returned rather
    /// than printed for <see cref="DriftCanary"/>'s reason: under <c>watch</c> a bare
    /// write lands inside the pinned block and corrupts the repaint, and this notice in
    /// particular must not simply be silenced there — it is the only account of matches
    /// being deleted, or of a deletion being refused (#133).
    /// </returns>
    /// <remarks>
    /// The guard exists because the cap is applied to an archive whose size it was never
    /// checked against. Someone who reads the README's capping note and sets 50 meaning
    /// "tidy the report" is asking, without knowing it, for eleven hundred matches to be
    /// deleted from the only copy that exists — no recycle bin, no undo — and the whole
    /// thing happens on the next double-click.
    /// <para>
    /// That is also why the way through is a command-line flag rather than another
    /// config key. The dangerous path is the double-click, which passes no arguments at
    /// all, so a flag cannot be reached by it: a large prune becomes something that only
    /// happens when somebody opens a terminal and asks for it by name. A second config
    /// key would sit in the same file as the mistake and be typed in the same sitting.
    /// </para>
    /// </remarks>
    private static PruneReport? Prune(Config cfg, RawArchive archive, bool confirmed)
    {
        if (cfg.MaxArchivedMatches <= 0) return null;

        var doomed = archive.Prunable(cfg.MaxArchivedMatches);
        if (doomed.Count == 0) return null;

        if (!confirmed && RetentionGuard.WouldBeLarge(doomed.Count, archive.Count))
        {
            return new PruneReport(
                $"nothing was deleted. \"MaxArchivedMatches\" is {cfg.MaxArchivedMatches}, " +
                $"and applying it would delete {doomed.Count} of the {archive.Count} " +
                "archived matches — more than this tool will do without being asked " +
                "twice. The archive is the only copy and there is no undo. If you meant " +
                $"it, run `mtga-pbp capture --prune`; if not, change the cap in " +
                $"{Config.UserFile}.",
                Held: true);
        }

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

        return removed.Count == 0
            ? null
            : new PruneReport(
                $"pruned {removed.Count} match(es) past the {cfg.MaxArchivedMatches} cap " +
                "(favourites kept)",
                Held: false);
    }

    /// <summary>
    /// What the retention cap did, or refused to do.
    /// </summary>
    /// <param name="Message">What to tell the reader.</param>
    /// <param name="Held">
    /// True when nothing was deleted because the prune was larger than
    /// <see cref="RetentionGuard"/> allows unasked; false when matches actually left.
    /// </param>
    /// <remarks>
    /// A flag beside the sentence rather than a caller reading the sentence. `watch`
    /// has to tell a standing condition from an event — it repeats a refusal once per
    /// session and reports real deletions every time — and deciding that by matching
    /// the words the message happens to start with makes the wording load-bearing:
    /// rewrite the sentence and the board silently starts repeating it every poll.
    /// </remarks>
    private readonly record struct PruneReport(string Message, bool Held);

    /// <summary>
    /// Serves the report and keeps it current: polls the log, re-captures when it
    /// grows, and tells any open page to refresh itself.
    /// </summary>
    /// <remarks>
    /// Polling rather than FileSystemWatcher — Arena writes to Player.log constantly,
    /// so change notifications fire continuously and tell us nothing useful. Length is
    /// the honest signal, and a shrink means Arena restarted and truncated the log.
    /// </remarks>
    private static int Watch(Config cfg, string[] operands, bool prune, bool rebuild)
    {
        var port = int.TryParse(operands.FirstOrDefault(), out var p) ? p : 8787;
        var interval = TimeSpan.FromSeconds(3);

        // ONE ledger for the whole session, lent to everything below. The poll loop,
        // the rebuilds and the web request that toggles a star all touch index.json,
        // and each of them used to hold its own copy loaded at a different moment —
        // so a star saved while a capture was in flight was reverted the moment that
        // capture wrote back the copy it had loaded before the click (#146). The
        // instance is safe to share; see RawArchive's lock.
        var archive = new RawArchive(cfg.ArchiveDir);

        Directory.CreateDirectory(cfg.OutputDir);

        // The server comes up before the first capture ever runs: the previous run's
        // report is already sitting in the output directory, and a port that answers
        // immediately beats half a minute of connection-refused that SUPPORT.md had
        // to explain away as not-a-crash (#123). The page refreshes itself over the
        // change stream once the fresh build lands, so nobody reads stale rows for
        // longer than the build takes — which is exactly what happens on every later
        // capture too.
        using var server = new LiveServer(cfg.OutputDir, port);
        var rebuilds = new RebuildGate();
        server.OnFavorite = (id, on) =>
        {
            var ok = archive.SetFavorite(id, on);
            // The response goes back the moment the flag is written. The rebuild only
            // repaints what is already true, and at a thousand matches it takes long
            // enough that a star waiting on it reads as a broken button (#113) — so it
            // runs behind the gate instead, where it also cannot overlap the poll
            // loop's own rebuild.
            //
            // Observed with a no-op rather than left unobserved: keeping a match changes
            // the archive and not the night's record, so there is nothing to repaint —
            // but an unobserved build prints the nudge itself, straight into the middle
            // of the pinned block, bypassing the erase that keeps it intact.
            if (ok) _ = RebuildThenNotify();
            return ok;

            async Task RebuildThenNotify()
            {
                // The refresh nudge waits for the rebuild but happens outside the
                // gate: NotifyChanged writes to every subscribed page, and one
                // stalled socket must not stretch the section rebuilds queue behind.
                await rebuilds.RunInBackground(() =>
                    Build(cfg, open: false, quiet: true, observed: (_, _, _) => { },
                          shared: archive));
                server.NotifyChanged();
            }
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

        // On anything but a first run there is a report to show right now. A first
        // run has nothing yet — opening the browser onto a 404 with no script in it
        // would strand the user there, because a 404 cannot subscribe to the change
        // stream that would have refreshed it — so the open waits for the build.
        var hadReport = File.Exists(Path.Combine(cfg.OutputDir, "index.html"));
        if (hadReport) OpenInBrowser(server.Url);

        Capture(cfg, prune, archive);

        // The first build's state is kept rather than discarded: without it the board
        // could not be drawn until a match finished, so `watch` started on an existing
        // archive showed the plain build text and nothing else — sometimes for hours.
        IReadOnlyList<MatchSummary> firstRows = [];
        IndexStats? firstStats = null;
        Nudge? firstNudge = null;
        var code = 0;
        // Behind the gate: the server is already answering, so a star clicked during
        // startup queues its rebuild behind this one instead of racing it.
        // --rebuild applies to this build and no other. A watch that re-derived the
        // whole archive on every poll would be the behaviour #122 exists to remove, and
        // at a thousand matches it would spend half a minute of every three seconds
        // rebuilding pages nothing had touched. Asking for a rebuild means "start from
        // a clean slate", and once the slate is clean it stays clean.
        rebuilds.Run(() => code = Build(cfg, open: false, observed: (rows, st, nudge) =>
        {
            firstRows = rows; firstStats = st; firstNudge = nudge;
        }, shared: archive, rebuild: rebuild));
        if (code != 0) return code;

        // Any page that connected during the build gets the fresh rows now — and a
        // first run finally has something worth opening a browser onto.
        server.NotifyChanged();
        if (!hadReport) OpenInBrowser(server.Url);

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
            var (exit, written, drift, pruned) = CaptureCore(cfg, quiet: true, prune, archive);

            // Said through the board so it scrolls above the pinned block and stays.
            // Once per watch session: the counts in the message grow every poll, so
            // deduplicating on the text itself would say it every three seconds —
            // and a drift that persists is one fact, not a stream of them.
            if (drift is not null && said.Add("format-drift"))
                board.Say($"[{DateTime.Now:HH:mm:ss}] ** {drift}");

            // Through the board for the same reason, and deduplicated for the same
            // reason: a cap that is too small is one standing fact, and a watch polling
            // every three seconds would otherwise repeat it forever. Matches actually
            // leaving is an event rather than a state, so only the refusal is held to
            // once — under the guard an automatic prune is small and rare anyway.
            if (pruned is { } pruneReport && (!pruneReport.Held || said.Add("prune-held")))
                board.Say($"[{DateTime.Now:HH:mm:ss}] ** {pruneReport.Message}");

            if (exit != 0 || written == 0) continue;

            // Notified outside the gate for the favorite handler's reason: a page
            // that has stopped reading must not hold the next rebuild hostage.
            rebuilds.Run(() => Build(cfg, open: false, quiet: true, observed: Repaint,
                                     shared: archive));
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
    /// The card database as a single comparable string — its path and when it was last
    /// written.
    /// </summary>
    /// <remarks>
    /// Every card name, face and line of ability text on a page is looked up in it, so a
    /// database Arena has updated can change a page whose match has not moved at all.
    /// Path as well as time, because pointing "CardDbPath" at a different file is the
    /// same kind of change and leaves the timestamp saying nothing.
    /// </remarks>
    /// <param name="path">Where the card database was found this run.</param>
    private static string CardDbStamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var at = file.Exists
                ? new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds()
                : 0;
            return $"{path}|{at}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            // Unknowable, so nothing matches it and everything re-renders.
            return $"{path}|?{Guid.NewGuid()}";
        }
    }

    /// <summary>
    /// Re-derives the archived matches that have moved since the last build, and leaves
    /// the rest alone. <c>--rebuild</c> ignores the cache and re-derives everything,
    /// which is what it has always claimed to do and now actually does.
    /// </summary>
    /// <param name="observed">
    /// Handed the state this build worked out, for a caller that wants to draw it. The
    /// alternative was recomputing the sessions and the coach's verdict in `watch`,
    /// which would have been the second implementation of both and free to disagree
    /// with the page it sits beside.
    /// </param>
    /// <param name="rebuild">
    /// Ignore what the last build recorded and re-derive every match.
    /// </param>
    private static int Build(Config cfg, bool open, bool quiet = false,
        Action<IReadOnlyList<MatchSummary>, IndexStats, Nudge?>? observed = null,
        RawArchive? shared = null, bool rebuild = false)
    {
        // Lent one under `watch` for CaptureCore's reason, and for one of its own: a
        // build reads Meta().Favorite to draw each row's star, so a build holding its
        // own copy of the ledger can render a star that was toggled after it started.
        var archive = shared ?? new RawArchive(cfg.ArchiveDir);
        using var cards = OpenCards(cfg, out var dbPath);

        var gamesDir = Path.Combine(cfg.OutputDir, "games");
        var textDir = Path.Combine(cfg.OutputDir, "text");
        Directory.CreateDirectory(gamesDir);
        Directory.CreateDirectory(textDir);

        var extractor = new EventExtractor(cards);
        var summaries = new List<MatchSummary>();
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        // What the last build already worked out. The card database is part of the
        // question because every name, face and ability on a page comes out of it.
        var cache = BuildCache.Load(cfg.OutputDir, ignore: rebuild);
        var cardStamp = CardDbStamp(dbPath);
        var reused = 0;

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
            var textPath = Path.Combine(textDir, $"{matchId}.md");

            // Read once. Every Meta call takes the ledger lock now (#146), and this
            // loop wanted the same entry twice — for the row's star and for the file
            // times below.
            var meta = archive.Meta(matchId);
            var neighbours = NeighboursOf(matchId);

            // The star is applied here rather than cached, because it is the one thing
            // about a match that changes without the match changing.
            if (archive.RawStamp(matchId) is { } raw &&
                cache.Reusable(matchId, raw.Size, raw.ModifiedMs,
                               neighbours, gamePath, textPath, cardStamp) is { } hit)
            {
                summaries.Add(hit.Summary with { Favorite = meta?.Favorite ?? false });
                foreach (var c in hit.Unresolved) unresolved.Add(c);
                cache.Keep(matchId, hit);
                reused++;
                continue;
            }

            var lines = archive.ReadLines(matchId);
            if (lines.Count == 0) continue;

            var transcript = extractor.Extract(matchId, lines);

            // Every name the deck section will print, resolved to a face where the
            // database has one. Per transcript for the renderer's sake, but the
            // lookups are cached in CardDb, so a name costs one query per build.
            var faces = new Dictionary<string, CardFace>(StringComparer.Ordinal);
            foreach (var name in transcript.Deck.Select(d => d.Name)
                         .Concat(transcript.Commanders)
                         .Concat(transcript.OpponentCommanders)
                         .Concat(transcript.OpponentCards))
                if (!faces.ContainsKey(name) && cards.FaceForName(name) is { } face)
                    faces[name] = face;

            File.WriteAllText(gamePath,
                GamePageRenderer.Render(transcript, neighbours, faces));
            File.WriteAllText(textPath, MarkdownRenderer.Render(transcript));

            // Both files carry the match's time rather than the build's, so that a
            // directory of them sorts the way the report does — see OutputStamp (#147).
            OutputStamp.MatchTime(meta?.StartedAtMs ?? 0, gamePath, textPath);

            var summary = IndexRenderer.Summarize(transcript);
            summaries.Add(summary with { Favorite = meta?.Favorite ?? false });

            var names = transcript.UnresolvedNames.Keys.ToList();
            foreach (var c in names) unresolved.Add(c);

            // Stored without the star, for the reason above.
            if (archive.RawStamp(matchId) is { } stamp)
                cache.Keep(matchId, new CachedMatch(
                    stamp.Size, stamp.ModifiedMs,
                    neighbours.NewerId, neighbours.NewerWhen,
                    neighbours.OlderId, neighbours.OlderWhen,
                    summary, names));
        }

        cache.Save(cfg.OutputDir, cardStamp);

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
            Console.WriteLine(
                $"built {summaries.Count} game(s)"
                + (reused > 0 ? $" ({reused} unchanged, left alone)" : ""));
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
