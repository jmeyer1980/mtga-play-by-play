using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MtgaPbp.Cli.Tray.NativeMethods;

namespace MtgaPbp.Cli.Tray;

/// <summary>
/// The notification-area icon <c>watch --tray</c> lives behind: one hidden window on
/// its own thread, one icon, one menu.
/// </summary>
/// <remarks>
/// A hidden top-level window rather than a message-only one: the shell announces a
/// recreated taskbar by broadcast, broadcasts do not reach message-only windows, and an
/// icon whose window missed it is gone for good when Explorer restarts. The message loop
/// runs on a background thread so it can never be what keeps the process alive — the
/// watch's own loop is the foreground.
/// <para>
/// Every message that means something is turned into a <see cref="TrayAction"/> by
/// <see cref="TrayEvents"/>, which is the half with tests; this is the half that talks to
/// Windows, and is checked by hand (the checklist on the pull request). Menu choices are
/// posted back to the window as <c>WM_COMMAND</c> so that they, too, go through the
/// tested mapping.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private const string ClassName = "MtgaPbpTray";
    private const uint IconId = 1;

    /// <summary><c>WM_APP + 2</c>; <c>WM_APP + 1</c> is the icon's own callback.</summary>
    private const uint WM_APP_QUIT = 0x8000 + 2;

    private readonly Action _openReport;
    private readonly Action _quit;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private WndProc? _proc;          // held so the collector cannot take the callback from under the window
    private nint _hwnd, _icon;
    private uint _taskbarCreated;
    private string _tip;
    private volatile bool _added;
    private Exception? _failure;

    private TrayIcon(string tip, Action openReport, Action quit)
    {
        _tip = tip;
        _openReport = openReport;
        _quit = quit;
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
    }

    /// <summary>
    /// Puts the icon up and returns once it is there. Throws when the shell refused it,
    /// so the caller can stay in the console it still has.
    /// </summary>
    public static TrayIcon Start(string tip, Action openReport, Action quit)
    {
        var tray = new TrayIcon(tip, openReport, quit);
        tray._thread.Start();
        // Bounded: a shell that never answers must leave the caller in its window, not
        // hanging at startup. The event is left for the thread on that path, since the
        // thread may still reach it; on the failure path the thread has already exited.
        if (!tray._ready.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("the notification-area icon did not come up in time");
        if (tray._failure is not null)
        {
            tray._ready.Dispose();
            throw tray._failure;
        }
        return tray;
    }

    /// <summary>Replaces the tooltip. Safe from any thread: the shell does the marshalling.</summary>
    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        data.szTip = tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>A notification balloon — a toast, on Windows 10 and later.</summary>
    public void Balloon(string title, string text)
    {
        if (!_added) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Takes the icon down and ends the message loop. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_hwnd != 0 && _thread.IsAlive)
        {
            PostMessage(_hwnd, WM_APP_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _proc = WindowProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _proc,
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed (error {Marshal.GetLastWin32Error()})");
            _hwnd = CreateWindowEx(0, ClassName, "mtga-pbp", 0, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
            if (_hwnd == 0)
                throw new InvalidOperationException($"CreateWindowEx failed (error {Marshal.GetLastWin32Error()})");
            _icon = OwnIcon();
            if (!Add())
                throw new InvalidOperationException("the shell refused the notification-area icon");
        }
        catch (Exception e)
        {
            _failure = e;
            Remove();
            _ready.Set();
            return;
        }
        _ready.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private bool Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = TrayEvents.WM_TRAY;
        data.hIcon = _icon;
        data.szTip = _tip;
        if (!Shell_NotifyIcon(NIM_ADD, ref data)) return false;
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        _added = true;
        return true;
    }

    private void Remove()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>The exe's own icon, which #66 put there for the taskbar.</summary>
    private static nint OwnIcon()
    {
        var small = new nint[1];
        var path = Environment.ProcessPath;
        if (path is not null && ExtractIconEx(path, 0, null, small, 1) > 0) return small[0];
        return 0;   // the shell draws a blank; still something to quit from
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_APP_QUIT)
        {
            Remove();
            DestroyWindow(hwnd);
            return 0;
        }
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        switch (TrayEvents.For(msg, wParam, lParam, _taskbarCreated))
        {
            case TrayAction.OpenReport:
                _openReport();
                return 0;
            case TrayAction.ShowMenu:
                ShowMenu(TrayEvents.Point(wParam));
                return 0;
            case TrayAction.Quit:
                _quit();
                // WM_QUERYENDSESSION wants TRUE for "go ahead"; a menu command ignores it.
                return msg == TrayEvents.WM_QUERYENDSESSION ? 1 : 0;
            case TrayAction.ReAddIcon:
                _added = false;
                Add();
                return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu((int X, int Y) at)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, TrayEvents.MenuOpenReport, "&Open report");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, TrayEvents.MenuQuit, "&Quit");
            SetMenuDefaultItem(menu, TrayEvents.MenuOpenReport, 0);
            // Without the foreground call the menu stays up after the pointer leaves it
            // (Microsoft KB 135788); the WM_NULL afterwards is the same article's other half.
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, at.X, at.Y, _hwnd, 0);
            PostMessage(_hwnd, WM_NULL, 0, 0);
            if (chosen != 0) PostMessage(_hwnd, TrayEvents.WM_COMMAND, chosen, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
