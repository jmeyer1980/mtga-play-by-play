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

    public static string Compose(SessionRow? session, DateTime updated)
    {
        var record = session is null
            ? "no matches yet"
            : (session.Drawn == 0
                ? $"{session.Won}-{session.Lost}"
                : $"{session.Won}-{session.Lost}-{session.Drawn}") + " tonight";
        return Clip($"mtga-pbp — watching · {record} · updated {updated:HH:mm}");
    }

    public static string Clip(string s) =>
        s.Length <= MaxLength ? s : s[..(MaxLength - 1)] + "…";
}
