using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SwordBox;

internal sealed class GridScrollBarSkin : Control
{
    private const int ClientObject = -4;
    private const int ClipSiblings = 0x04000000;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ScrollBarInfo
    {
        public uint Size;
        public Native.Rect Bounds;
        public int ArrowLength, ThumbStart, ThumbEnd, Reserved;
        public fixed uint State[6];
    }

    [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr window, int objectId, ref ScrollBarInfo info);

    private readonly ScrollBar bar;
    private readonly Func<Color> background;
    private readonly bool vertical;
    private bool hovered;

    public GridScrollBarSkin(Control parent, ScrollBar bar, Func<Color> background, bool vertical)
    {
        this.bar = bar;
        this.background = background;
        this.vertical = vertical;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Visible = false;
        parent.Controls.Add(this);
        bar.HandleCreated += (_, _) => ClipNativeDrawing();
        bar.LocationChanged += (_, _) => FollowBar();
        bar.SizeChanged += (_, _) => FollowBar();
        bar.VisibleChanged += (_, _) => FollowBar();
        bar.EnabledChanged += (_, _) => Invalidate();
        bar.MouseEnter += (_, _) => { hovered = true; Invalidate(); };
        bar.MouseLeave += (_, _) => { hovered = false; Invalidate(); };
        bar.MouseCaptureChanged += (_, _) => Invalidate();
        bar.ValueChanged += (_, _) => Invalidate();
        bar.Scroll += (_, _) => Invalidate();
        if (bar.IsHandleCreated) ClipNativeDrawing();
        FollowBar();
    }

    private void ClipNativeDrawing()
    {
        // The native control still processes input, while its direct redraws are clipped by this sibling.
        Native.SetWindowLong(bar.Handle, -16, Native.GetWindowLong(bar.Handle, -16) | ClipSiblings);
    }

    private void FollowBar()
    {
        Bounds = bar.Bounds;
        Visible = bar.Visible;
        if (Visible) BringToFront();
        Invalidate();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084) // WM_NCHITTEST
        {
            message.Result = new IntPtr(-1); // HTTRANSPARENT
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        using var backdrop = new SolidBrush(background());
        graphics.FillRectangle(backdrop, ClientRectangle);
        var info = new ScrollBarInfo { Size = (uint)Marshal.SizeOf<ScrollBarInfo>() };
        if (!bar.IsHandleCreated || !bar.Enabled || !GetScrollBarInfo(bar.Handle, ClientObject, ref info)) return;

        int length = vertical ? bar.ClientSize.Height : bar.ClientSize.Width;
        float center = (vertical ? bar.ClientSize.Width : bar.ClientSize.Height) / 2f;
        float scale = bar.DeviceDpi / 96f;
        bool active = hovered || bar.Capture;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var rail = new Pen(Color.FromArgb(52, 52, 58), 2 * scale);
        using var thumb = new Pen(active ? Color.FromArgb(171, 171, 183) : Color.FromArgb(121, 121, 135),
            (active ? 6 : 4) * scale);
        DrawLine(rail, info.ArrowLength, length - info.ArrowLength);
        DrawLine(thumb, info.ThumbStart, info.ThumbEnd);

        void DrawLine(Pen pen, float start, float end)
        {
            float inset = pen.Width / 2;
            if (end - start <= pen.Width) return;
            pen.StartCap = pen.EndCap = LineCap.Round;
            if (vertical) graphics.DrawLine(pen, center, start + inset, center, end - inset);
            else graphics.DrawLine(pen, start + inset, center, end - inset, center);
        }
    }
}
