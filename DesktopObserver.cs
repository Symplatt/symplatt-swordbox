using System;
using System.Drawing;
using System.Windows.Forms;

namespace SwordBox;

// Isolated UI-check harness. Its transparent area exposes the real desktop;
// it neither renders a copy of a box nor forwards input to a box.
internal sealed class DesktopObserver : Form
{
    public DesktopObserver()
    {
        Text = "万剑匣桌面检查";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.WorkingArea;
        BackColor = TransparencyKey = Color.Magenta;
        ShowInTaskbar = true;
        var desktop = new Button { Text = "显示桌面", BackColor = Color.White, UseVisualStyleBackColor = false, Bounds = new Rectangle(12, Height - 44, 100, 30) };
        var close = new Button { Text = "关闭检查", BackColor = Color.White, UseVisualStyleBackColor = false, Bounds = new Rectangle(120, Height - 44, 100, 30) };
        desktop.Click += (_, _) =>
        {
            object? shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type != null) { shell = Activator.CreateInstance(type); type.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null); }
            }
            finally { if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            BeginInvoke(() => { Native.ShowWindow(Handle, 4); Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 1 | 2 | 0x10); });
        };
        close.Click += (_, _) => Close();
        Controls.Add(desktop); Controls.Add(close);
        AcceptButton = desktop;
        Shown += (_, _) => BeginInvoke(() => desktop.PerformClick());
    }
}
