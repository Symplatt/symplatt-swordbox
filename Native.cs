using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace SwordBox;

internal static class Native
{
    public const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_APPWINDOW = 0x40000, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public Rectangle Bounds => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)] public struct WindowPosition { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ShellInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [StructLayout(LayoutKind.Sequential)] struct FileInfo
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    public delegate bool EnumProc(IntPtr window, IntPtr arg);
    public delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    public static void RoundWindow(IntPtr window, int width, int height)
    {
        const int diameter = 16;
        IntPtr region = CreateRoundRectRgn(0, 0, width, height, diameter, diameter);
        // The regular resize paint presents the updated shape with the updated client area.
        if (region != IntPtr.Zero && SetWindowRgn(window, region, false) == 0) DeleteObject(region);
    }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr arg);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr arg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(uint eventId, uint flags, string? path, IntPtr second);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    private static readonly Dictionary<string, (string, Image?)> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string Identity(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        return !handle.IsInvalid && GetFileInformationByHandle(handle, out var info)
            ? $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}" : "";
    }
    public static void NotifyDesktopVisibility(string path, bool directory, bool visible)
    {
        // The item entered or left the desktop view, even though its file path is intact.
        uint change = directory ? (visible ? 0x8u : 0x10u) : (visible ? 0x2u : 0x4u);
        SHChangeNotify(change, 0x1005, path, IntPtr.Zero); // SHCNF_PATHW | SHCNF_FLUSH
    }
    public static void NotifyAssociation() => SHChangeNotify(0x08000000, 0x1000, null, IntPtr.Zero); // SHCNE_ASSOCCHANGED, SHCNF_FLUSH
    public static (string, Image?) FilePresentation(string path, bool folder)
    {
        string ext = Path.GetExtension(path);
        bool exact = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        string key = folder ? "<folder>" : exact ? path : ext.ToLowerInvariant();
        if (Cache.TryGetValue(key, out var found)) return found;
        SHGetFileInfo(path, folder ? 0x10u : 0x80u, out var info, (uint)Marshal.SizeOf<ShellInfo>(), 0x100 | 0x1 | 0x400 | (exact ? 0u : 0x10u));
        Image? image = null;
        if (info.Icon != IntPtr.Zero)
        {
            try { using var icon = Icon.FromHandle(info.Icon); image = icon.ToBitmap(); }
            finally { DestroyIcon(info.Icon); }
        }
        string type = string.IsNullOrEmpty(info.TypeName) ? folder ? "文件夹" : ext.TrimStart('.').ToUpperInvariant() + " 文件" : info.TypeName;
        return Cache[key] = (type, image);
    }
    public static IntPtr DesktopHost()
    {
        return GetShellWindow();
    }
    public static IntPtr WindowAt(System.Drawing.Point point) =>
        GetAncestor(WindowFromPoint(new Point { X = point.X, Y = point.Y }), 2); // GA_ROOT
    public static IntPtr WatchForeground(WinEventProc callback) => SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);
    public static bool IsKeyHeld(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public static bool IsDesktopDropTarget(IntPtr window)
    {
        IntPtr shell = DesktopHost();
        if (window == IntPtr.Zero || shell == IntPtr.Zero) return false;
        GetWindowThreadProcessId(shell, out uint shellProcess);
        GetWindowThreadProcessId(window, out uint targetProcess);
        return shellProcess == targetProcess && (window == shell || IsDesktopSurface(window));
    }
    private static string WindowClass(IntPtr window)
    {
        if (window == IntPtr.Zero) return "";
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    public static void Attach(DesktopBox form, int screenX, int screenY)
    {
        IntPtr host = DesktopHost();
        if (host == IntPtr.Zero) throw new InvalidOperationException("暂未找到 Windows 桌面，请稍后从桌面右键菜单新建收纳盒。");
        IntPtr hwnd = form.Handle;
        int style = GetWindowLong(hwnd, -16);
        // Explorer's wallpaper/icon windows are separate compositor surfaces.
        // Retain our own surface and place it above them, below ordinary apps.
        SetWindowLong(hwnd, -16, (style & ~WS_CHILD) | WS_POPUP);
        SetWindowLongPtr(hwnd, -8, IntPtr.Zero);
        SetWindowLong(hwnd, -20, (GetWindowLong(hwnd, -20) & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        SetBoxOpacity(hwnd, form.BoxOpacity);
        var after = DesktopInsertAfter(hwnd);
        SetWindowPos(hwnd, after, screenX, screenY, form.Width, form.Height, 0x20 | 0x10 | 0x200 | (after == hwnd ? 0x4u : 0));
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x100 | 0x80 | 0x400);
    }
    public static void SetBoxOpacity(IntPtr window, int percentage)
    {
        byte alpha = (byte)Math.Round(255 * percentage / 100.0);
        if (!SetLayeredWindowAttributes(window, 0, alpha, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置收纳盒透明度。");
    }
    public static bool IsDesktopBox(IntPtr hwnd) => GetWindow(hwnd, 4) == IntPtr.Zero &&
        (GetWindowLong(hwnd, -16) & WS_CHILD) == 0 &&
        (GetWindowLong(hwnd, -20) & (WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE)) == (WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
    public static IntPtr DesktopInsertAfter(IntPtr hwnd)
    {
        IntPtr shell = DesktopHost();
        if (shell == IntPtr.Zero) return hwnd;
        GetWindowThreadProcessId(shell, out uint shellProcess);
        var windows = new List<IntPtr>();
        EnumWindows((window, _) => { if (IsWindowVisible(window)) windows.Add(window); return true; }, IntPtr.Zero);
        int desktop = windows.FindIndex(window =>
        {
            if (window == shell) return true;
            GetWindowThreadProcessId(window, out uint process);
            return process == shellProcess && FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
        });
        if (desktop < 0) return hwnd;
        int current = windows.IndexOf(hwnd);
        if (current >= 0 && current < desktop)
        {
            bool appBelow = false;
            for (int i = current + 1; i < desktop; i++)
            {
                GetWindowThreadProcessId(windows[i], out uint pid);
                if (pid != Environment.ProcessId || !IsDesktopBox(windows[i])) { appBelow = true; break; }
            }
            if (!appBelow) return hwnd;
        }
        return desktop > 0 ? windows[desktop - 1] : IntPtr.Zero;
    }
    private static bool IsDesktopSurface(IntPtr hwnd)
    {
        string name = WindowClass(hwnd);
        if (name == "Progman") return true;
        if (name != "WorkerW") return false;
        if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return true;
        return GetWindowRect(hwnd, out var rect) && Screen.AllScreens.Any(s => rect.Bounds.Contains(s.Bounds));
    }
    public static void MoveOnDesktop(IntPtr hwnd, int screenX, int screenY, int width, int height)
    {
        var after = DesktopInsertAfter(hwnd);
        SetWindowPos(hwnd, after, screenX, screenY, width, height, 0x10 | 0x200 | (after == hwnd ? 0x4u : 0));
    }
    public static void SetDesktopBoxVisible(IntPtr hwnd, bool show)
    {
        bool visible = IsWindowVisible(hwnd);
        if (!show)
        {
            if (visible) ShowWindow(hwnd, 0);
            return;
        }
        if (IsIconic(hwnd)) ShowWindow(hwnd, 4);
        var after = DesktopInsertAfter(hwnd);
        if (!visible || after != hwnd)
            SetWindowPos(hwnd, after, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200 | (visible ? 0u : 0x40u) | (after == hwnd ? 0x4u : 0));
    }
}
