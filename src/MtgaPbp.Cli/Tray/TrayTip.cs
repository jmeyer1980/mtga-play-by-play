using MtgaPbp.Render;

namespace MtgaPbp.Cli.Tray;

/// <summary>
/// The icon's tooltip: the scoreboard's headline in one line.
/// </summary>
/// <remarks>
/// It is also what a screen reader announces at the icon, so it says what the thing is
/// before it says how the evening is going. <c>szTip</c> holds 128 characters including
/// the terminator; anything longer is clipped, the way the board clips.
/// </remarks>
public static class TrayTip
{
    /// <summary>What fits in <c>NOTIFYICONDATA.szTip</c> beside its terminator.</summary>
    public const int MaxLength = 127;

    /// <summary>What fits in <c>NOTIFYICONDATA.szInfo</c> beside its terminator.</summary>
    public const int MaxBalloonLength = 255;

    public static string Compose(SessionRow? session, DateTime updated)
    {
        var record = session is null
            ? "no matches yet"
            : (session.Drawn == 0
                ? $"{session.Won}-{session.Lost}"
                : $"{session.Won}-{session.Lost}-{session.Drawn}") + " tonight";
        return Clip($"mtga-pbp — watching · {record} · updated {updated:HH:mm}");
    }

    /// <summary>
    /// The balloon shown as the window lets go: where the report is, how to quit, and
    /// any flag on the command line that nothing acted on.
    /// </summary>
    /// <remarks>
    /// The flags are repeated here because this balloon is shown at the moment the
    /// console goes away. From a shortcut, the window the warning line landed in closes
    /// once the first build is up, so a typo in the shortcut's target — <c>---open</c>,
    /// say — would otherwise have been said only to a window nobody was reading. A
    /// message box was the other candidate and is not used: it is modal, and a watch
    /// started at logon would wait behind it, not serving, until someone clicked.
    /// </remarks>
    public static string Detached(string url, IReadOnlyList<string> ignored)
    {
        var text = $"Watching. The report is at {url} — right-click this icon to quit.";
        if (ignored.Count > 0)
            text += $" Ignoring unknown option{(ignored.Count == 1 ? "" : "s")} " +
                    $"{string.Join(", ", ignored)}.";
        return Clip(text, MaxBalloonLength);
    }

    public static string Clip(string s, int max = MaxLength) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
