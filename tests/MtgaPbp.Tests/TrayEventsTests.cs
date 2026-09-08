using MtgaPbp.Cli.Tray;
using NUnit.Framework;

namespace MtgaPbp.Tests;

/// <summary>
/// What each thing the shell can send the icon asks the watch to do — decided here,
/// with no shell in the room, because the part that talks to Windows cannot run in CI.
/// </summary>
public class TrayEventsTests
{
    private const uint TaskbarCreated = 0xC0FF;

    private static nint Pack(int low, int high) => (nint)(((high & 0xFFFF) << 16) | (low & 0xFFFF));

    private static TrayAction Icon(uint ev) =>
        TrayEvents.For(TrayEvents.WM_TRAY, Pack(10, 20), Pack((int)ev, 1), TaskbarCreated);

    [Test]
    public void A_left_click_opens_the_report() =>
        Assert.That(Icon(TrayEvents.NIN_SELECT), Is.EqualTo(TrayAction.OpenReport));

    [Test]
    public void Enter_on_the_focused_icon_opens_the_report_too() =>
        Assert.That(Icon(TrayEvents.NIN_KEYSELECT), Is.EqualTo(TrayAction.OpenReport));

    [Test]
    public void A_right_click_or_shift_f10_shows_the_menu() =>
        Assert.That(Icon(TrayEvents.WM_CONTEXTMENU), Is.EqualTo(TrayAction.ShowMenu));

    [Test]
    public void A_double_click_adds_nothing_to_the_click_it_contains() =>
        Assert.That(Icon(TrayEvents.WM_LBUTTONDBLCLK), Is.EqualTo(TrayAction.None));

    [Test]
    public void Mouse_movement_over_the_icon_is_nothing() =>
        Assert.That(Icon(0x0200 /* WM_MOUSEMOVE */), Is.EqualTo(TrayAction.None));

    [Test]
    public void The_menu_s_open_item_opens_the_report() =>
        Assert.That(TrayEvents.For(TrayEvents.WM_COMMAND, (nint)TrayEvents.MenuOpenReport, 0, TaskbarCreated),
                    Is.EqualTo(TrayAction.OpenReport));

    [Test]
    public void The_menu_s_quit_item_quits() =>
        Assert.That(TrayEvents.For(TrayEvents.WM_COMMAND, (nint)TrayEvents.MenuQuit, 0, TaskbarCreated),
                    Is.EqualTo(TrayAction.Quit));

    [Test]
    public void Logoff_quits() =>
        Assert.That(TrayEvents.For(TrayEvents.WM_QUERYENDSESSION, 0, 0, TaskbarCreated),
                    Is.EqualTo(TrayAction.Quit));

    [Test]
    public void An_explorer_restart_puts_the_icon_back() =>
        Assert.That(TrayEvents.For(TaskbarCreated, 0, 0, TaskbarCreated), Is.EqualTo(TrayAction.ReAddIcon));

    [Test]
    public void A_failed_registration_never_matches_a_message() =>
        Assert.That(TrayEvents.For(0, 0, 0, taskbarCreated: 0), Is.EqualTo(TrayAction.None));

    [Test]
    public void Anything_else_is_nothing() =>
        Assert.That(TrayEvents.For(0x000F /* WM_PAINT */, 0, 0, TaskbarCreated), Is.EqualTo(TrayAction.None));

    [Test]
    public void The_pointer_position_unpacks_with_its_sign()
    {
        // A second monitor to the left puts x below zero.
        Assert.That(TrayEvents.Point(Pack(-100, 50)), Is.EqualTo((-100, 50)));
        Assert.That(TrayEvents.Point(Pack(2184, 1528)), Is.EqualTo((2184, 1528)));
    }

    [Test]
    public void Mouse_messages_are_the_0x0200_block_and_nothing_else()
    {
        Assert.That(TrayEvents.IsMouseMessage(0x0200 /* WM_MOUSEMOVE */), Is.True);
        Assert.That(TrayEvents.IsMouseMessage(0x0205 /* WM_RBUTTONUP */), Is.True);
        Assert.That(TrayEvents.IsMouseMessage(0x0209 /* WM_MBUTTONDBLCLK */), Is.True);
        Assert.That(TrayEvents.IsMouseMessage(TrayEvents.WM_CONTEXTMENU), Is.False);
        Assert.That(TrayEvents.IsMouseMessage(TrayEvents.NIN_SELECT), Is.False);
        Assert.That(TrayEvents.IsMouseMessage(0x020A /* WM_MOUSEWHEEL */), Is.False);
    }

    [Test]
    public void A_menu_from_the_mouse_opens_at_the_pointer_and_one_from_the_keyboard_at_the_icon()
    {
        // The anchor the shell sends for an icon behind the chevron is the chevron itself,
        // which is where the menu opened until this test existed.
        Assert.That(TrayEvents.MenuAt(fromMouse: true, cursor: (900, 700), anchor: (2187, 1528)), Is.EqualTo((900, 700)));
        Assert.That(TrayEvents.MenuAt(fromMouse: false, cursor: (900, 700), anchor: (2187, 1528)), Is.EqualTo((2187, 1528)));
    }
}
