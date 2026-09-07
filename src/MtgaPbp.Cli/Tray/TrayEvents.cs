namespace MtgaPbp.Cli.Tray;

/// <summary>What the icon asks the watch to do.</summary>
public enum TrayAction { None, OpenReport, ShowMenu, Quit, ReAddIcon }

/// <summary>
/// Turns the messages the shell sends a notification-area icon into actions, with no
/// shell in the room.
/// </summary>
/// <remarks>
/// With <c>NOTIFYICON_VERSION_4</c> the shell packs the event into the low word of
/// <c>lParam</c> and the icon id into the high word, and the pointer position into
/// <c>wParam</c>. A left click arrives as <c>NIN_SELECT</c>, Enter on a focused icon
/// (Win+B, arrows) as <c>NIN_KEYSELECT</c>, and a right click, Shift+F10 or the Apps
/// key as <c>WM_CONTEXTMENU</c>. A double-click is delivered <em>after</em> the click it
/// contains, which is why it maps to nothing: whatever it did would happen on top of
/// what the single click already did (#213, Q2).
/// </remarks>
public static class TrayEvents
{
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_LBUTTONDBLCLK = 0x0203;

    /// <summary>The callback message the icon is registered with: <c>WM_APP + 1</c>.</summary>
    public const uint WM_TRAY = 0x8000 + 1;

    public const uint NIN_SELECT = 0x0400;
    public const uint NIN_KEYSELECT = 0x0401;

    public const uint MenuOpenReport = 1;
    public const uint MenuQuit = 2;

    /// <param name="taskbarCreated">
    /// The registered <c>TaskbarCreated</c> message, or 0 when registering it failed —
    /// in which case no message can be it.
    /// </param>
    public static TrayAction For(uint msg, nint wParam, nint lParam, uint taskbarCreated)
    {
        if (msg == WM_TRAY)
        {
            var ev = (uint)(lParam & 0xFFFF);
            return ev switch
            {
                NIN_SELECT or NIN_KEYSELECT => TrayAction.OpenReport,
                WM_CONTEXTMENU => TrayAction.ShowMenu,
                _ => TrayAction.None,
            };
        }
        if (msg == WM_COMMAND)
        {
            var id = (uint)(wParam & 0xFFFF);
            return id switch
            {
                MenuOpenReport => TrayAction.OpenReport,
                MenuQuit => TrayAction.Quit,
                _ => TrayAction.None,
            };
        }
        if (msg == WM_QUERYENDSESSION) return TrayAction.Quit;
        if (taskbarCreated != 0 && msg == taskbarCreated) return TrayAction.ReAddIcon;
        return TrayAction.None;
    }

    /// <summary>The pointer position the shell packed into <c>wParam</c>: x low, y high, each signed.</summary>
    public static (int X, int Y) Point(nint wParam) =>
        ((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
}
