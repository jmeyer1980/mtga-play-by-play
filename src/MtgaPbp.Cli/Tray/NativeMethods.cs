using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MtgaPbp.Cli.Tray;

/// <summary>
/// The Win32 surface the icon needs, and nothing else.
/// </summary>
/// <remarks>
/// Every entry point that has a <c>W</c> variant is named with it and marked
/// <c>ExactSpelling</c>: the investigation's spike bound <c>DefWindowProc</c> without
/// either, got the ANSI variant for a Unicode window class, and read its own title back
/// as garbage. Here there is no guess for the runtime to make.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd; public uint message; public nint wParam, lParam;
        public uint time; public POINT pt; public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIIF_INFO = 1;
    public const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
    public const uint MB_OK = 0x0000, MB_ICONWARNING = 0x0030;
    public const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint GetConsoleProcessList(uint[] list, uint count);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    public static extern nint DefWindowProc(nint h, uint msg, nint w, nint l);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool DestroyWindow(nint h);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true)]
    public static extern int GetMessage(out MSG msg, nint h, uint min, uint max);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    public static extern nint DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    public static extern bool PostMessage(nint h, uint msg, nint w, nint l);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string s);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint wnd, nint tpm);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool SetForegroundWindow(nint h);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool DestroyIcon(nint h);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MessageBox(nint h, string text, string caption, uint type);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[]? small, uint n);
}
