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
        var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "all";
        var open = cfg.OpenAfterBuild || args.Contains("--open");

        try
        {
            return command switch
            {
                "capture" => Capture(cfg),
                "build" => Build(cfg, open),
                "stats" => Stats(cfg),
                "watch" => Watch(cfg, args),
                "keep" => Favorite(cfg, Arg(args, 1), on: true),
                "unkeep" => Favorite(cfg, Arg(args, 1), on: false),
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

    private static string? Arg(string[] args, int index) =>
        args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Skip(index).FirstOrDefault();

    private static int Usage()
    {
        Console.WriteLine("""
            mtga-pbp                  capture new matches, then rebuild the site
            mtga-pbp --open           ... and open the report in your browser
            mtga-pbp capture          capture only
            mtga-pbp build            re-derive the whole site from the archive
            mtga-pbp build --rebuild  same as above (build never caches)
            mtga-pbp stats            unhandled annotations and unresolved cards
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

    private static int Capture(Config cfg)
    {
        var archive = new RawArchive(cfg.ArchiveDir);
        var stats = new ScanStats();
        var added = 0;
        var sawAnyLog = false;

        foreach (var log in cfg.LogPaths.Where(File.Exists))
        {
            sawAnyLog = true;
            foreach (var slice in MatchSlicer.Slice(LogScanner.Scan(log, stats)))
                if (archive.Write(slice)) added++;
        }

        if (!sawAnyLog)
        {
            Console.Error.WriteLine(
                "error: no Arena log found. Looked for:\n  " +
                string.Join("\n  ", cfg.LogPaths) +
                "\nSet \"LogPaths\" in mtga-pbp.json if Arena is installed elsewhere.");
            return 2;
        }

        Console.WriteLine(
            $"captured {added} new match(es); {stats.JsonLines:N0} json lines read, " +
            $"{stats.MalformedLines} malformed");

        Prune(cfg, archive);
        return 0;
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
    private static int Watch(Config cfg, string[] args)
    {
        var port = int.TryParse(Arg(args, 1), out var p) ? p : 8787;
        var interval = TimeSpan.FromSeconds(3);

        Directory.CreateDirectory(cfg.OutputDir);
        Capture(cfg);
        if (Build(cfg, open: false) is var code and not 0) return code;

        using var server = new LiveServer(cfg.OutputDir, port);
        server.OnFavorite = (id, on) =>
        {
            var ok = new RawArchive(cfg.ArchiveDir).SetFavorite(id, on);
            if (ok) { Build(cfg, open: false); server.NotifyChanged(); }
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
            var changed = false;
            foreach (var log in cfg.LogPaths.Where(File.Exists))
            {
                var length = new FileInfo(log).Length;
                if (sizes.TryGetValue(log, out var was) && was == length) continue;
                sizes[log] = length;
                changed = true;
            }
            if (!changed) continue;

            var archive = new RawArchive(cfg.ArchiveDir);
            var before = archive.MatchIds().Count();

            Capture(cfg);
            if (new RawArchive(cfg.ArchiveDir).MatchIds().Count() == before) continue;

            Build(cfg, open: false);
            server.NotifyChanged();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] new match captured — report updated");
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
    private static int Build(Config cfg, bool open)
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

            foreach (var c in transcript.CardsSeen.Where(
                         c => c.StartsWith("Card #", StringComparison.Ordinal)))
                unresolved.Add(c);
        }

        var indexPath = Path.GetFullPath(Path.Combine(cfg.OutputDir, "index.html"));
        File.WriteAllText(indexPath, IndexRenderer.Render(summaries));
        if (unresolved.Count > 0)
            File.WriteAllLines(Path.Combine(cfg.OutputDir, "unresolved.txt"), unresolved);

        Console.WriteLine($"built {summaries.Count} game(s)");
        Console.WriteLine();
        Console.WriteLine($"  report:  {indexPath}");
        Console.WriteLine($"  cards:   {dbPath}");
        Console.WriteLine();
        if (unresolved.Count > 0)
            Console.WriteLine($"{unresolved.Count} unresolved card id(s) — see unresolved.txt");

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
        var unresolved = new Dictionary<string, int>(StringComparer.Ordinal);
        var matches = 0;

        foreach (var matchId in archive.MatchIds())
        {
            var lines = archive.ReadLines(matchId);
            if (lines.Count == 0) continue;
            matches++;
            var t = extractor.Extract(matchId, lines);
            foreach (var (k, v) in t.UnknownAnnotations)
                unknown[k] = unknown.GetValueOrDefault(k) + v;
            foreach (var c in t.CardsSeen.Where(
                         c => c.StartsWith("Card #", StringComparison.Ordinal)))
                unresolved[c] = unresolved.GetValueOrDefault(c) + 1;
        }

        Console.WriteLine($"{matches} match(es) in archive\n");
        Console.WriteLine("unhandled annotation types:");
        foreach (var (k, v) in unknown.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {v,6}  {k}");
        if (unknown.Count == 0) Console.WriteLine("  (none)");

        Console.WriteLine("\nunresolved cards:");
        foreach (var (k, v) in unresolved.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {v,6}  {k}");
        if (unresolved.Count == 0) Console.WriteLine("  (none)");
        return 0;
    }
}
