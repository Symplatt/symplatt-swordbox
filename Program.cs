using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SwordBox;

internal static class Program
{
    public const string Product = "辛普莱特的万剑匣";
    public const string Version = "1.8.1";
    private const string MenuKey = @"Software\Classes\DesktopBackground\Shell\Symplatt.SwordBox";
    private const string LegacyMenuKey = @"Software\Classes\DesktopBackground\Shell\Simplicit.SwordBox";
    private const string OldMenuKey = @"Software\Classes\Directory\Background\shell\Simplicit.SwordBox";
    private const string ExtensionKey = @"Software\Classes\.swordbox";
    private const string BoxType = "Symplatt.SwordBox.DesktopBox";
    private const string LegacyBoxType = "Simplicit.SwordBox.DesktopBox";
    private const string ClassesKey = @"Software\Classes\";
    private const string CapabilitiesKey = @"Software\Symplatt\SwordBox\Capabilities";
    private const string RegisteredAppsKey = @"Software\RegisteredApplications";
    private const string RegisteredName = "Symplatt.SwordBox";
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.FirstOrDefault() == StartupSetting.HelperArgument)
        {
            Environment.ExitCode = StartupSetting.RunHelper(args);
            return;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string? sandbox = Value(args, "--sandbox");
        bool startup = args.Contains("--startup");
        bool newBox = args.Contains("--new-box");
        bool openSettings = args.Contains("--settings");
        // Keep the existing storage location and mutex so upgrades retain boxes and recovery records.
        string root = sandbox ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Simplicit", "SwordBox");
        string[] desktops = sandbox != null ? new[] { Path.Combine(root, "Desktop") } : new[]
        { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(root);
        if (sandbox != null) Directory.CreateDirectory(desktops[0]);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..20];
        string mutexName = "Local\\SwordBox-" + key;
        if (Value(args, "--guard") is string parent)
        {
            try { Process.GetProcessById(int.Parse(parent)).WaitForExit(); } catch { }
            using var guardLock = new Mutex(false, mutexName);
            if (Acquire(guardLock, 5000))
            {
                try { new Storage(root, desktops).RestoreAll(); } catch (Exception ex) { Log(root, ex); }
                finally { guardLock.ReleaseMutex(); }
            }
            return;
        }
        if (args.Contains("--unregister")) { UnregisterMenu(); return; }
        if (args.Contains("--register-menu")) { RegisterMenu(); return; }
        if (args.Contains("--self-test")) { SelfTest.Run(root, desktops); return; }
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, mutexName + "-stop");
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, mutexName + "-show");
        using var create = new EventWaitHandle(false, EventResetMode.AutoReset, mutexName + "-new");
        using var settings = new EventWaitHandle(false, EventResetMode.AutoReset, mutexName + "-settings");
        using var mutex = new Mutex(false, mutexName);
        if (args.Contains("--restore"))
        {
            stop.Set();
            if (!Acquire(mutex, 15000)) { MessageBox.Show("请从托盘退出万剑匣后重试。", Product); return; }
            try { var errors = new Storage(root, desktops).RestoreAll(); if (errors.Count > 0) MessageBox.Show(string.Join("\n", errors), Product); }
            finally { mutex.ReleaseMutex(); }
            return;
        }
        if (!Acquire(mutex, 0))
        {
            if (newBox) create.Set();
            else if (openSettings) settings.Set();
            else if (!startup) show.Set();
            return;
        }
        stop.Reset();
        DesktopManager? manager = null;
        try
        {
            var store = new Storage(root, desktops);
            var errors = store.RestoreAll(keepCollected: true);
            if (errors.Count > 0) throw new IOException(string.Join("\n", errors));
            if (args.Contains("--demo")) SeedDemo(store);
            if (store.Config.Boxes.Count == 0 && !newBox && !startup && !openSettings && !args.Contains("--observe-desktop")) store.Config.Boxes.Add(store.CreateBox());
            if (sandbox == null) RegisterMenu();
            StartGuard(sandbox);
            bool verification = args.Contains("--verification-window");
            if (verification && sandbox == null) throw new InvalidOperationException("界面检查模式仅允许隔离测试目录。");
            manager = new DesktopManager(store, stop, show, create, settings, newBox, Value(args, "--diagnostic"), verification, sandbox == null);
            Application.ThreadException += (_, e) => { Log(root, e.Exception); manager.Error(e.Exception.Message); };
            manager.Start();
            if (openSettings) manager.OpenSettings();
            if (args.Contains("--observe-desktop"))
            {
                if (sandbox == null) throw new InvalidOperationException("桌面观察仅允许隔离测试目录。");
                new DesktopObserver().Show();
            }
            Application.Run(manager);
        }
        catch (Exception ex) { Log(root, ex); MessageBox.Show(ex.Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            manager?.Dispose();
            try { new Storage(root, desktops).RestoreAll(); } catch (Exception ex) { Log(root, ex); }
            mutex.ReleaseMutex();
        }
    }
    public static void RegisterMenu()
    {
        Registry.CurrentUser.DeleteSubKeyTree(OldMenuKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(LegacyMenuKey, false);
        string executable = Environment.ProcessPath!;
        string launch = "\"" + executable + "\" --new-box";
        using (var key = Registry.CurrentUser.CreateSubKey(MenuKey))
        {
            key.SetValue("", "新建收纳盒"); key.SetValue("MUIVerb", "新建收纳盒");
            key.SetValue("Icon", "\"" + executable + "\",0"); key.SetValue("Position", "Top");
            using var command = key.CreateSubKey("command");
            command.SetValue("", launch);
        }
        using (var type = Registry.CurrentUser.CreateSubKey(ClassesKey + BoxType))
        {
            type.SetValue("", "收纳盒");
            type.SetValue("FriendlyTypeName", "收纳盒");
            using var icon = type.CreateSubKey("DefaultIcon");
            icon.SetValue("", "\"" + executable + "\",0");
            using var open = type.CreateSubKey(@"shell\open\command");
            open.SetValue("", launch + " \"%1\"");
        }
        // Explorer can read the label directly from ShellNew when building the New submenu.
        using (var extension = Registry.CurrentUser.CreateSubKey(ExtensionKey))
        {
            extension.SetValue("", BoxType);
            using var openWith = extension.CreateSubKey("OpenWithProgids");
            openWith.SetValue(BoxType, "");
            // Explorer supports both the extension-wide and association-specific locations.
            foreach (string path in new[] { "ShellNew", BoxType + @"\ShellNew" })
            {
                using var shellNew = extension.CreateSubKey(path);
                shellNew.SetValue("ItemName", "收纳盒");
                shellNew.SetValue("MenuText", "收纳盒");
                shellNew.SetValue("Command", launch);
                shellNew.SetValue("IconPath", "\"" + executable + "\",0");
            }
        }
        // Advertise the file type to Windows' application association catalog.
        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", Product);
            capabilities.SetValue("ApplicationDescription", "创建和管理桌面收纳盒");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue(".swordbox", BoxType);
        }
        using (var applications = Registry.CurrentUser.CreateSubKey(RegisteredAppsKey))
            applications.SetValue(RegisteredName, CapabilitiesKey);
        Registry.CurrentUser.DeleteSubKeyTree(ClassesKey + LegacyBoxType, false);
        Native.NotifyAssociation();
    }
    private static void UnregisterMenu()
    {
        Registry.CurrentUser.DeleteSubKeyTree(MenuKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(LegacyMenuKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(OldMenuKey, false);
        using var extension = Registry.CurrentUser.OpenSubKey(ExtensionKey);
        if (extension?.GetValue("") is BoxType or LegacyBoxType)
            Registry.CurrentUser.DeleteSubKeyTree(ExtensionKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(ClassesKey + BoxType, false);
        Registry.CurrentUser.DeleteSubKeyTree(ClassesKey + LegacyBoxType, false);
        using (var applications = Registry.CurrentUser.OpenSubKey(RegisteredAppsKey, writable: true))
            applications?.DeleteValue(RegisteredName, false);
        Registry.CurrentUser.DeleteSubKeyTree(CapabilitiesKey, false);
        Native.NotifyAssociation();
    }
    private static bool Acquire(Mutex mutex, int wait) { try { return mutex.WaitOne(wait); } catch (AbandonedMutexException) { return true; } }
    private static string? Value(string[] args, string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    public static void Log(string root, Exception ex) => File.AppendAllText(Path.Combine(root, "error.log"), DateTime.Now + " " + ex + "\n");
    private static void StartGuard(string? sandbox)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--guard"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        if (sandbox != null) { start.ArgumentList.Add("--sandbox"); start.ArgumentList.Add(sandbox); }
        Process.Start(start);
    }
    private static void SeedDemo(Storage store)
    {
        if (!store.Root.Contains("sandbox", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("演示仅允许在隔离目录运行。");
        if (store.Config.Boxes.Count == 0) store.Config.Boxes.Add(new Box());
        var box = store.Config.Boxes[0]; box.Name = "工作文件"; box.Width = 970; box.Height = 388;
        string[] names = { "项目资料", "2026 年第四季度项目计划与交付安排.docx", "会议记录 — 10 月 27 日.txt", "产品界面参考与设计说明（完整文件名称换行示例）.pdf", "预算明细与采购清单.xlsx" };
        for (int i = 0; i < names.Length; i++)
        {
            string path = Path.Combine(store.Desktops[0], names[i]);
            if (i == 0) Directory.CreateDirectory(path); else if (!File.Exists(path)) File.WriteAllText(path, "SwordBox isolated fixture");
            FileSystemInfo info = i == 0 ? new DirectoryInfo(path) : new FileInfo(path);
            info.CreationTime = new DateTime(2026, 10, 20 + i, 9, 10 + i, 0);
            info.LastWriteTime = new DateTime(2026, 10, 27, 10 + i, 20 + i, 0);
            if (!box.Paths.Contains(path)) box.Paths.Add(path);
        }
        store.Save();
    }
}
