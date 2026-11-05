using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace SwordBox;

internal sealed class DesktopIcons
{
    private readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);

    public void Sync(IEnumerable<string> paths)
    {
        var collected = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (collected.Count == 0 && removed.Count == 0) return;
        WithView((view, handle) =>
        {
            if (!removed.IsSubsetOf(collected))
            {
                if (view.Refresh() < 0) return;
                removed.Clear();
            }
            if (collected.Count == 0) return;
            IntPtr list = Native.FindWindowEx(handle, IntPtr.Zero, "SysListView32", null);
            var items = (IFolderView)view;
            if (list == IntPtr.Zero || items.ItemCount(2, out int count) != 0) return;

            var buffer = new StringBuilder(32768);
            for (int index = count - 1; index >= 0; index--)
            {
                string? path = ItemPath(items, index, buffer);
                if (path == null || !collected.Contains(path)) continue;
                // The list and the shell view must still agree before using a list index.
                if (!Send(list, 0x1004, 0, out var listCount) || listCount.ToInt64() != count ||
                    !string.Equals(path, ItemPath(items, index, buffer), StringComparison.OrdinalIgnoreCase))
                    return;
                if (!Send(list, 0x1008, index, out var result) || result == IntPtr.Zero) return;
                removed.Add(path);
                count--;
            }
        });
    }

    public void Restore()
    {
        WithView((view, _) => { if (view.Refresh() >= 0) removed.Clear(); });
    }

    private static string? ItemPath(IFolderView view, int index, StringBuilder buffer)
    {
        if (view.Item(index, out IntPtr id) != 0 || id == IntPtr.Zero) return null;
        try
        {
            buffer.Clear();
            return SHGetPathFromIDListEx(id, buffer, (uint)buffer.Capacity, 0) ? buffer.ToString() : null;
        }
        finally { Marshal.FreeCoTaskMem(id); }
    }

    private static bool Send(IntPtr list, uint message, int index, out IntPtr result) =>
        SendMessageTimeout(list, message, new IntPtr(index), IntPtr.Zero, 2, 200, out result) != IntPtr.Zero;

    private static void WithView(Action<IShellView, IntPtr> action)
    {
        object? shell = null, windows = null, desktop = null, browser = null, view = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            windows = ((dynamic)shell!).Windows();
            int desktopHandle = 0;
            object location = 0, root = 0;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out desktopHandle, 1);
            if (desktop is not IServiceProvider services) return;
            var browserId = new Guid("000214E2-0000-0000-C000-000000000046");
            if (services.QueryService(new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"), browserId, out browser) != 0 ||
                browser is not IShellBrowser shellBrowser || shellBrowser.QueryActiveShellView(out view) != 0 ||
                view is not IShellView shellView || shellView.GetWindow(out var handle) != 0)
                return;
            action(shellView, handle);
        }
        catch (COMException) { } // Explorer can restart while its desktop view is being queried.
        catch (RuntimeBinderException) { } // The desktop window can disappear between automation calls.
        finally
        {
            foreach (var value in new[] { view, browser, desktop, windows, shell })
                if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool SHGetPathFromIDListEx(
        IntPtr id, StringBuilder path, uint capacity, uint flags);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(in Guid service, in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB();
        void RemoveMenusSB(); void SetStatusTextSB(); void EnableModelessSB(); void TranslateAcceleratorSB();
        void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr handle);
        void ContextSensitiveHelp(); void TranslateAccelerator(); void EnableModeless(); void UIActivate();
        [PreserveSig] int Refresh();
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr id);
        [PreserveSig] int ItemCount(uint flags, out int count);
    }
}
