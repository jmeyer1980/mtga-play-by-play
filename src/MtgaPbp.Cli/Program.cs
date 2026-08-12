using System.Net.Sockets;
using MtgaPbp.Core;
using MtgaPbp.Render;

namespace MtgaPbp.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var exeDir = AppContext.BaseDirectory;
        var cfg = Config.Load(exeDir);
        var (command, operands) = Parse(args);
        var open = cfg.OpenAfterBuild || args.Contains("--open");

        try
        {
            return command switch
            {
                "capture" => Capture(cfg),
                "build" => Build(cfg, open),
                "stats" => Stats(cfg),
                "watch" => Watch(cfg, operands),
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
        ["capture", "build", "stats", "watch", "keep", "unkeep"];

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
        var stats = new ScanStats();
        var written = 0;
        var sawAnyLog = false;

        foreach (var log in cfg.LogPaths.Where(File.Exists))
        {
            sawAnyLog = true;
            foreach (var slice in MatchSlicer.Slice(LogScanner.Scan(log, stats)))
                if (archive.Write(slice)) written++;
        }

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
                $"captured {written} new match(es); {stats.JsonLines:N0} json lines read, " +
                $"{stats.MalformedLines} malformed");

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
        if (Build(cfg, open: false) is var code and not 0) return code;

        using var server = new LiveServer(cfg.OutputDir, port);
        server.OnFavorite = (id, on) =>
        {
            var ok = new RawArchive(cfg.ArchiveDir).SetFavorite(id, on);
            if (ok) { Build(cfg, open: false, quiet: true); server.NotifyChanged(); }
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
        Console.WriteLine($"report is live at {server.Url}");
        Console.WriteLine("leave this window open; press Ctrl+C to stop.");
        OpenInBrowser(server.Url);

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

            Build(cfg, open: false, quiet: true);
            server.NotifyChanged();
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] {written} match(es) captured or completed — report updated");
        }

        Console.WriteLine("stopped.");
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
    private static int Build(Config cfg, bool open, bool quiet = false)
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

        foreach (var matchId in archive.MatchIds())
        {
            var gamePath = Path.Combine(gamesDir, $"{matchId}.html");
            var lines = archive.ReadLines(matchId);
            if (lines.Count == 0) continue;

            var transcript = extractor.Extract(matchId, lines);
            File.WriteAllText(gamePath, GamePageRenderer.Render(transcript));
            File.WriteAllText(Path.Combine(textDir, $"{matchId}.md"),
                MarkdownRenderer.Render(transcript));
            summaries.Add(IndexRenderer.Summarize(transcript) with
            {
                Favorite = archive.Meta(matchId)?.Favorite ?? false
            });

            foreach (var c in transcript.UnresolvedNames.Keys)
                unresolved.Add(c);
        }

        var indexPath = Path.GetFullPath(Path.Combine(cfg.OutputDir, "index.html"));
        File.WriteAllText(indexPath, IndexRenderer.Render(summaries));
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
