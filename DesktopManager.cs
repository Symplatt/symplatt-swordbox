using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace SwordBox;

public sealed class DesktopManager : ApplicationContext
{
    public Storage Store { get; }
    public bool Stopping { get; private set; }
    public bool Verification { get; private set; }
    private readonly EventWaitHandle stop, show, create;
    private readonly EventWaitHandle settings;
    private readonly bool createInitially;
    private readonly bool allowStartupChanges;
    private readonly string? diagnostic;
    private readonly List<DesktopBox> boxes = new();
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly Control dispatch = new();
    private NotifyIcon? tray;
    private ContextMenuStrip? trayMenu;
    private ToolStripMenuItem? closedMenu;
    private Icon? trayIcon;
    private SettingsForm? settingsForm;
    private readonly Native.WinEventProc foregroundChanged;
    private IntPtr foregroundHook;
    public bool BoxesHidden { get; private set; }
    private int ticks;
    public DesktopManager(Storage store, EventWaitHandle stop, EventWaitHandle show, EventWaitHandle create, EventWaitHandle settings, bool createInitially, string? diagnostic, bool verification, bool allowStartupChanges)
    {
        Store = store;
        this.stop = stop;
        this.show = show;
        this.create = create;
        this.settings = settings;
        this.createInitially = createInitially;
        this.diagnostic = diagnostic;
        this.allowStartupChanges = allowStartupChanges;
        Verification = verification;
        foregroundChanged = (_, _, _, _, _, _, _) => UpdateDesktopVisibility();
    }
    public void Start()
    {
        _ = dispatch.Handle;
        foregroundHook = Native.WatchForeground(foregroundChanged);
        foreach (var box in Store.Config.Boxes)
            foreach (var path in box.Paths.ToArray().Where(path => File.Exists(Store.ItemPath(path)) || Directory.Exists(Store.ItemPath(path))))
                try { Store.StoreItem(path); } catch (Exception ex) { Error(Path.GetFileName(path) + "：" + ex.Message); }
        foreach (var box in Store.Config.Boxes.ToArray()) ShowBox(box);
        if (createInitially) NewBox();
        trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("新建收纳盒", null, (_, _) => NewBox());
        closedMenu = new ToolStripMenuItem("恢复已关闭的收纳盒");
        trayMenu.Items.Add(closedMenu);
        trayMenu.Opening += (_, _) => RefreshClosedMenu();
        trayMenu.Items.Add("刷新收纳盒", null, (_, _) => RefreshDesktopBoxes(force: true));
        var visibility = new ToolStripMenuItem("隐藏收纳盒");
        visibility.Click += (_, _) =>
        {
            BoxesHidden = !BoxesHidden;
            visibility.Text = BoxesHidden ? "显示收纳盒" : "隐藏收纳盒";
            UpdateDesktopVisibility();
        };
        trayMenu.Items.Add(visibility);
        trayMenu.Items.Add("设置", null, (_, _) => OpenSettings());
        trayMenu.Items.Add("软件信息", null, (_, _) =>
            MessageBox.Show(Program.Product + "\n版本 " + Program.Version + "\n版权所有 © 2026 Symplatt\nWindows 11 x64",
                "软件信息", MessageBoxButtons.OK, MessageBoxIcon.Information));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出并恢复桌面图标", null, (_, _) => Stop());
        tray = new NotifyIcon
        {
            Text = Program.Product,
            Icon = trayIcon ?? SystemIcons.Application,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => NewBox();
        foreach (var desktop in Store.Desktops.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(desktop) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            watcher.Renamed += (_, e) => dispatch.BeginInvoke(() => { if (!Stopping && Store.Tracks(e.OldFullPath)) { Store.TrackRename(e.OldFullPath, e.FullPath); RefreshAll(); } });
            watcher.EnableRaisingEvents = true; watchers.Add(watcher);
        }
        timer.Tick += (_, _) => Tick(); timer.Start();
    }
    private void ShowBox(Box box)
    {
        var form = new DesktopBox(this, box);
        try
        {
            _ = form.Handle;
            if (!Verification) Native.Attach(form, form.Left, form.Top);
            form.Show();
            form.Persist();
            boxes.Add(form);
            UpdateDesktopVisibility();
        }
        catch { form.Dispose(); throw; }
    }
    private void Tick()
    {
        if (Stopping) return;
        if (stop.WaitOne(0)) { Stop(); return; }
        if (create.WaitOne(0)) NewBox();
        if (settings.WaitOne(0)) OpenSettings();
        if (show.WaitOne(0)) { if (boxes.Count == 0) NewBox(); else foreach (var box in boxes) box.Invalidate(true); }
        UpdateDesktopVisibility();
        if (++ticks % 4 != 0) return;
        RefreshDesktopBoxes(force: false);
        if (diagnostic != null) WriteDiagnostic(diagnostic);
    }

    private void RefreshDesktopBoxes(bool force)
    {
        if (Native.DesktopHost() == IntPtr.Zero) return;
        Store.RefreshStoredPaths();
        foreach (var box in boxes.ToArray())
        {
            if (box.IsDisposed || !box.IsHandleCreated || !Native.IsWindow(box.Handle))
            {
                // Add the replacement before dropping the record, so a failed creation can retry.
                ShowBox(box.Box);
                boxes.Remove(box);
                box.Dispose();
                continue;
            }
            if (!Verification && (!box.Attached || force && !BoxesHidden))
                box.AttachToDesktop();
            box.RefreshFiles(force);
            if (force && !BoxesHidden)
            {
                box.SetDesktopVisibility(true);
                Native.RedrawWindow(box.Handle, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x100 | 0x80 | 0x400);
            }
        }
    }
    public void NewBox()
    {
        Native.GetCursorPos(out var point);
        var box = Store.CreateBox();
        box.Name = "收纳盒 " + (Store.Config.Boxes.Count + 1);
        box.X = point.X - 20;
        box.Y = point.Y - 20;
        Store.Config.Boxes.Add(box);
        try { ShowBox(box); Store.Save(); }
        catch { Store.Config.Boxes.Remove(box); throw; }
    }
    public void Dissolve(DesktopBox box)
    {
        try { foreach (string path in box.Box.Paths.ToArray()) Store.Remove(box.Box, path); }
        catch (Exception ex) { Error(ex.Message); return; }
        Store.Config.Boxes.Remove(box.Box); Store.Save(); boxes.Remove(box); box.Dispose();
    }
    public void CloseBox(DesktopBox box)
    {
        try
        {
            box.Persist();
            var errors = Store.CloseBox(box.Box);
            boxes.Remove(box);
            box.Dispose();
            if (errors.Count > 0) Error(string.Join("\n", errors));
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private void RefreshClosedMenu()
    {
        var menu = closedMenu!;
        foreach (var item in menu.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
        foreach (var box in Store.Config.ClosedBoxes.AsEnumerable().Reverse())
            menu.DropDownItems.Add(box.Name, null, (_, _) =>
            {
                try
                {
                    Store.ReopenBox(box);
                    try { ShowBox(box); }
                    catch { Store.CloseBox(box); throw; }
                }
                catch (Exception ex) { Error(ex.Message); }
            });
        menu.Enabled = menu.DropDownItems.Count != 0;
    }
    public void RefreshAll() { foreach (var box in boxes) box.RefreshFiles(true); }
    public void MoveBox(DesktopBox box, Point location)
    {
        var snapped = SnapBox(box, new Rectangle(location, box.Size));
        Native.MoveOnDesktop(box.Handle, snapped.X, snapped.Y, snapped.Width, snapped.Height);
    }

    internal Rectangle SnapBox(DesktopBox box, Rectangle proposed, BoxEdge resizing = BoxEdge.None)
    {
        var neighbors = new List<Rectangle>();
        foreach (var other in boxes)
            if (other != box && other.IsHandleCreated && Native.IsWindowVisible(other.Handle) &&
                Native.GetWindowRect(other.Handle, out var bounds))
                neighbors.Add(bounds.Bounds);
        return BoxSnap.Bounds(proposed, Screen.FromRectangle(proposed).WorkingArea, neighbors,
            box.DeviceDpi, resizing, box.MinimumSize);
    }
    private void UpdateDesktopVisibility()
    {
        if (Verification || Stopping) return;
        foreach (var box in boxes)
            if (!box.IsDisposed && box.IsHandleCreated) box.SetDesktopVisibility(!BoxesHidden);
    }
    public bool CanPreviewDrop(Point point)
    {
        IntPtr window = Native.WindowAt(point);
        return Native.IsDesktopDropTarget(window) ||
            boxes.Any(box => box.IsHandleCreated && box.Handle == window);
    }
    public void DropFiles(DesktopBox source, string[] paths, Point point)
    {
        if (paths.Length == 0 || Stopping) return;
        var window = Native.WindowAt(point);
        var target = boxes.FirstOrDefault(box => box.IsHandleCreated && box.Handle == window);
        if (target == source) return;
        if (target != null)
        {
            try { Store.Transfer(source.Box, target.Box, paths); RefreshAll(); }
            catch (Exception ex) { Error(ex.Message); }
        }
        else if (Native.IsDesktopDropTarget(window))
            source.RemoveFiles(paths);
    }
    public void OpenSettings()
    {
        if (settingsForm == null || settingsForm.IsDisposed)
        {
            settingsForm = new SettingsForm(allowStartupChanges, trayIcon, Store.Config.BoxOpacity, opacity =>
            {
                int previous = Store.Config.BoxOpacity;
                Store.Config.BoxOpacity = opacity;
                try { Store.Save(); }
                catch { Store.Config.BoxOpacity = previous; throw; }
                foreach (var box in boxes) box.ApplyOpacity();
            });
            settingsForm.Show();
        }
        if (settingsForm.WindowState == FormWindowState.Minimized)
            settingsForm.WindowState = FormWindowState.Normal;
        if (!Native.IsWindowVisible(settingsForm.Handle))
            Native.ShowWindow(settingsForm.Handle, 5); // SW_SHOW after a hidden launch.
        settingsForm.Activate();
    }
    public void EnterDesktopVerification(DesktopBox box)
    {
        // This diagnostic transition preserves the HWND so a UI test can keep
        // observing the exact same surface after it becomes a desktop tool window.
        Verification = false;
        box.AttachToDesktop();
        UpdateDesktopVisibility();
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type != null) { shell = Activator.CreateInstance(type); type.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null); }
        }
        finally { if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    public void Error(string message) => MessageBox.Show(message, Program.Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
    public void Stop()
    {
        if (Stopping) return; Stopping = true; timer.Stop();
        foreach (var box in boxes) box.Persist();
        var errors = Store.RestoreAll();
        if (errors.Count > 0) Error("恢复记录已保留：\n" + string.Join("\n", errors));
        foreach (var box in boxes) box.Dispose(); boxes.Clear(); tray?.Dispose(); ExitThread();
    }
    private void WriteDiagnostic(string path)
    {
        var topLevel = new List<long>();
        Native.EnumWindows((hwnd, _) => { Native.GetWindowThreadProcessId(hwnd, out var pid); if (pid == Environment.ProcessId && Native.IsWindowVisible(hwnd)) topLevel.Add(hwnd.ToInt64()); return true; }, IntPtr.Zero);
        var taskbarEligible = topLevel.Where(h => (Native.GetWindowLong(new IntPtr(h), -20) & Native.WS_EX_TOOLWINDOW) == 0 && Native.GetWindow(new IntPtr(h), 4) == IntPtr.Zero).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = Program.Version, Pid = Environment.ProcessId, DesktopHost = Native.DesktopHost().ToInt64(), VisibleTopLevelWindows = topLevel, TaskbarEligibleWindows = taskbarEligible, Boxes = boxes.Select(b => b.Diagnostics()).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        if (boxes.Count > 0 && !File.Exists(Path.ChangeExtension(path, ".png"))) boxes[0].CaptureNativeWindow(Path.ChangeExtension(path, ".png"));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
            tray?.Dispose();
            trayMenu?.Dispose();
            settingsForm?.Dispose();
            trayIcon?.Dispose();
            foreach (var watcher in watchers) watcher.Dispose();
            dispatch.Dispose();
            foreach (var box in boxes) box.Dispose();
        }
        base.Dispose(disposing);
    }
}
