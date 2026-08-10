using System.Text;
using MtgaPbp.Core;

namespace MtgaPbp.Render;

public static class MarkdownRenderer
{
    public static string Render(Transcript t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {TranscriptSummary.Title(t)}");
        sb.AppendLine();
        sb.AppendLine($"*{TranscriptSummary.Subtitle(t)}*");
        sb.AppendLine();
        if (t.Incomplete)
            sb.AppendLine("> This match is incomplete — the log was rotated before it finished.")
              .AppendLine();

        foreach (var line in Narrator.Narrate(t, Density.Beats))
        {
            if (line.IsTurnHeader) sb.AppendLine().AppendLine($"## {line.Text}");
            else sb.AppendLine($"- {line.Text}");
        }
        return sb.ToString();
    }
}

public static class TranscriptSummary
{
    public static string Title(Transcript t) =>
        $"{t.You?.ScreenName ?? "You"} vs {t.Opponent?.ScreenName ?? "Opponent"}";

    public static string Result(Transcript t)
    {
        if (t.Incomplete && t.WinningTeamId is null) return "Unfinished";
        var won = t.WinningTeamId is not null && t.WinningTeamId == t.You?.Seat;
        return $"{(won ? "Won" : "Lost")} {t.GamesWon}-{t.GamesLost}";
    }

    public static string Subtitle(Transcript t) =>
        $"{t.EventName} · {Date(t):yyyy-MM-dd HH:mm} · {Result(t)} · {Turns(t)} turns";

    public static DateTimeOffset Date(Transcript t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t.StartedAtMs == 0 ? 0 : t.StartedAtMs).ToLocalTime();

    public static int Turns(Transcript t) => t.Events.Count == 0 ? 0 : t.Events.Max(e => e.Turn);
}
