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

    private static int Usage()
    {
        Console.WriteLine("""
            mtga-pbp                  capture new matches, then rebuild the site
            mtga-pbp --open           ... and open the report in your browser
            mtga-pbp capture          capture only
            mtga-pbp build            re-derive the whole site from the archive
            mtga-pbp build --rebuild  same as above (build never caches)
            mtga-pbp stats            unhandled annotations and unresolved cards

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
            summaries.Add(IndexRenderer.Summarize(transcript));

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
