using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SwordBox;

internal static class StartupSetting
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "Symplatt.SwordBox";
    internal const string HelperArgument = "--set-startup";

    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue(StartupName) is string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return false; }
    }

    public static async Task SetAsync(bool enabled)
    {
        try { Write(enabled); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var request = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            request.ArgumentList.Add(HelperArgument);
            request.ArgumentList.Add(enabled ? "on" : "off");
            request.ArgumentList.Add(identity.User!.Value);
            try
            {
                using var helper = Process.Start(request) ?? throw new IOException("无法打开管理员授权请求。");
                await helper.WaitForExitAsync();
                if (helper.ExitCode == 2) throw new IOException("请使用当前 Windows 账户授权开机自启。");
                if (helper.ExitCode != 0) throw new IOException("Windows 拒绝保存开机自启设置。");
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                throw new IOException("已取消管理员授权，开机自启设置未保存。", error);
            }
        }
    }

    public static int RunHelper(string[] args)
    {
        if (args.Length != 3 || args[1] is not ("on" or "off")) return 1;
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != args[2]) return 2;
        try { Write(args[1] == "on"); return 0; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { return 1; }
    }

    private static void Write(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) run.SetValue(StartupName, "\"" + Environment.ProcessPath! + "\" --startup");
        else run.DeleteValue(StartupName, false);
    }
}
