using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace SwordBox;

internal sealed class DesktopIcons
{
    private const string RegionOwner = "Symplatt.SwordBox.DesktopIconRegion";
    private IntPtr lastList;
    private Rectangle lastBounds;
    private Rectangle[] lastHoles = Array.Empty<Rectangle>();

    public void Sync(IEnumerable<string> paths)
    {
        var collected = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (collected.Count == 0)
        {
            if (lastList != IntPtr.Zero) ResetRegion(lastList);
            lastList = IntPtr.Zero;
            return;
        }

        WithView((view, list) =>
        {
            if (lastList != IntPtr.Zero && lastList != list) ResetRegion(lastList);
            if (view is not IFolderView items || items.ItemCount(2, out int count) != 0 ||
                !Native.GetWindowRect(list, out var window)) return;

            var accessibilityId = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            if (AccessibleObjectFromWindow(list, 0xFFFFFFFC, accessibilityId, out object accessible) != 0) return;
            try
            {
                var bounds = window.Bounds;
                var holes = new List<Rectangle>();
                var visible = new List<Rectangle>();
                var buffer = new StringBuilder(32768);
                dynamic listItems = accessible;
                int accessibleCount = listItems.accChildCount;
                if (accessibleCount < count) return;
                for (int index = 0; index < count; index++)
                {
                    int child = index + 1;
                    var (path, label) = ReadItem(items, index, buffer);
                    string? actualLabel = listItems.accName[child];
                    listItems.accLocation(out int x, out int y, out int width, out int height, child);
                    if (width <= 0 || height <= 0) continue;
                    var area = Rectangle.Intersect(bounds, new Rectangle(x, y, width, height));
                    if (area.Width == 0 || area.Height == 0) continue;
                    area.Offset(-bounds.X, -bounds.Y);
                    if (path != null && collected.Contains(path) &&
                        string.Equals(label, actualLabel, StringComparison.OrdinalIgnoreCase))
                        holes.Add(area);
                    else visible.Add(area);
                }

                // Keep the previous region while Explorer is rebuilding its item view.
                if (items.ItemCount(2, out int latest) != 0 || latest != count || (int)listItems.accChildCount != accessibleCount)
                    return;
                holes.RemoveAll(area => visible.Any(area.IntersectsWith));
                ApplyRegion(list, bounds, holes);
            }
            finally { Marshal.ReleaseComObject(accessible); }
        });
    }

    public void Restore()
    {
        if (lastList != IntPtr.Zero) ResetRegion(lastList);
        WithView((_, list) => ResetRegion(list));
        lastList = IntPtr.Zero;
    }

    private void ApplyRegion(IntPtr list, Rectangle bounds, List<Rectangle> holes)
    {
        if (holes.Count == 0)
        {
            ResetRegion(list);
            lastList = IntPtr.Zero;
            return;
        }
        bool ownRegion = GetProp(list, RegionOwner) != IntPtr.Zero;
        if (ownRegion && lastList == list && lastBounds == bounds && holes.SequenceEqual(lastHoles)) return;

        IntPtr scratch = CreateRectRgn(0, 0, 0, 0);
        IntPtr region = CreateRectRgn(0, 0, bounds.Width, bounds.Height);
        try
        {
            if (scratch == IntPtr.Zero || region == IntPtr.Zero || !ownRegion && GetWindowRgn(list, scratch) != 0)
                return;
            foreach (var area in holes)
            {
                if (!SetRectRgn(scratch, area.Left, area.Top, area.Right, area.Bottom) ||
                    CombineRgn(region, region, scratch, 4) == 0) return;
            }
            if (!ownRegion && !SetProp(list, RegionOwner, new IntPtr(1))) return;
            if (SetWindowRgn(list, region, true) == 0)
            {
                if (!ownRegion) RemoveProp(list, RegionOwner);
                return;
            }
            region = IntPtr.Zero; // The system owns the applied region.
            lastList = list;
            lastBounds = bounds;
            lastHoles = holes.ToArray();
        }
        finally
        {
            if (region != IntPtr.Zero) DeleteObject(region);
            if (scratch != IntPtr.Zero) DeleteObject(scratch);
        }
    }

    private static void ResetRegion(IntPtr list)
    {
        if (GetProp(list, RegionOwner) != IntPtr.Zero && SetWindowRgn(list, IntPtr.Zero, true) != 0)
            RemoveProp(list, RegionOwner);
    }

    private static (string? Path, string? Label) ReadItem(IFolderView view, int index, StringBuilder buffer)
    {
        if (view.Item(index, out IntPtr id) != 0 || id == IntPtr.Zero) return default;
        try
        {
            buffer.Clear();
            string? path = SHGetPathFromIDListEx(id, buffer, (uint)buffer.Capacity, 0) ? buffer.ToString() : null;
            string? label = null;
            if (SHGetNameFromIDList(id, 0, out IntPtr name) == 0)
            {
                try { label = Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            return (path, label);
        }
        finally { Marshal.FreeCoTaskMem(id); }
    }

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
            IntPtr list = Native.FindWindowEx(handle, IntPtr.Zero, "SysListView32", null);
            if (list != IntPtr.Zero) action(shellView, list);
        }
        catch (COMException) { } // Explorer can restart while its desktop view is being queried.
        catch (RuntimeBinderException) { } // The desktop window can disappear between automation calls.
        finally
        {
            foreach (var value in new[] { view, browser, desktop, windows, shell })
                if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool SHGetPathFromIDListEx(
        IntPtr id, StringBuilder path, uint capacity, uint flags);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr id, uint form, out IntPtr name);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr handle, uint objectId, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object accessible);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool SetRectRgn(IntPtr region, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);

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
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr id);
        [PreserveSig] int ItemCount(uint flags, out int count);
    }
}
