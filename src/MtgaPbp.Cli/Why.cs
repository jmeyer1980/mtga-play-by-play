using MtgaPbp.Core;
using MtgaPbp.Render;

namespace MtgaPbp.Cli;

/// <summary>
/// Shows a turn's rendered lines beside the raw annotations that produced them.
/// </summary>
/// <remarks>
/// Every output bug found so far came from reading a transcript, thinking "that line
/// looks wrong", and then hand-writing a script to walk the gzipped archive and print
/// what the log actually said. That loop is the most productive tool in the project and
/// the slowest part of it is the script. This is that script, kept.
/// <para>
/// It resolves instance ids to card names, because the raw log is a wall of integers and
/// the whole question is usually "which permanent is 405". Ids are shown as well as
/// names: a name that looks wrong is the bug, and the id is what you search the archive
/// for next.
/// </para>
/// </remarks>
public static class Why
{
    public static int Run(Config cfg, string? matchId, string? turnArg)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            Console.Error.WriteLine("""
                usage: mtga-pbp why <matchId> [turn]

                With no turn, lists the turns of the match. With one, shows that turn's
                transcript lines above the raw annotations behind them, ids resolved.
                """);
            return 2;
        }

        var archive = new RawArchive(cfg.ArchiveDir);
        if (!archive.Contains(matchId))
        {
            Console.Error.WriteLine($"no archived match with id {matchId}");
            Console.Error.WriteLine($"searched {Path.GetFullPath(cfg.ArchiveDir)}");
            return 4;
        }

        var raw = archive.ReadLines(matchId);
        using var cards = OpenCards(cfg);
        var transcript = new EventExtractor(cards).Extract(matchId, raw);
        var lines = Narrator.Narrate(transcript, Density.Verbose);

        if (!int.TryParse(turnArg, out var turn))
        {
            Console.WriteLine(TranscriptSummary.Title(transcript));
            Console.WriteLine(TranscriptSummary.Subtitle(transcript));
            Console.WriteLine();
            foreach (var header in lines.Where(l => l.IsTurnHeader && l.Turn > 0))
                Console.WriteLine($"  {header.Text}");
            Console.WriteLine();
            Console.WriteLine($"mtga-pbp why {matchId} <turn>");
            return 0;
        }

        foreach (var game in transcript.Games.Select(g => g.Number).DefaultIfEmpty(1))
        {
            var of = transcript.Games.Count > 1 ? $" of game {game}" : "";
            Console.WriteLine($"=== turn {turn}{of}: what the transcript says ===");
            foreach (var l in lines.Where(l => l.Turn == turn && l.Game == game))
                Console.WriteLine($"  {(l.IsTurnHeader ? "" : "- ")}{l.Text}");

            Console.WriteLine();
            Console.WriteLine($"=== turn {turn}{of}: what the log says ===");
            foreach (var l in AnnotationDump.ForTurn(raw, cards, turn, game))
                Console.WriteLine($"  {l}");
            Console.WriteLine();
        }

        return 0;
    }

    private static CardDb OpenCards(Config cfg)
    {
        var path = CardDb.FindDatabase(cfg.CardDbPath)
                   ?? throw new FileNotFoundException("Card database not found.");
        return new CardDb(path);
    }
}
