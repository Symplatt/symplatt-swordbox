using System.Drawing;
using System.Windows.Forms;

namespace SwordBox;

internal sealed class DragPreview : Form
{
    private readonly DesktopFile file;
    private readonly int count;

    public DragPreview(DesktopFile file, int count)
    {
        this.file = file;
        this.count = count;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = 0.67;
        Font = SystemFonts.MessageBoxFont!;
        BackColor = Color.FromArgb(26, 28, 34);
        var nameSize = TextRenderer.MeasureText(file.Name, Font);
        ClientSize = new Size(System.Math.Clamp(nameSize.Width + (count > 1 ? 80 : 44), 110, 300), 40);
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | 0x20; // WS_EX_TRANSPARENT
            return parameters;
        }
    }

    public void Follow(Point pointer)
    {
        const int offset = 16;
        Rectangle bounds = Screen.FromPoint(pointer).Bounds;
        var location = new Point(pointer.X + offset, pointer.Y + offset);
        if (location.X + Width > bounds.Right) location.X = pointer.X - Width - offset;
        if (location.Y + Height > bounds.Bottom) location.Y = pointer.Y - Height - offset;
        if (location != Location) Location = location;
        if (!Visible) Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        using var pen = new Pen(Color.FromArgb(144, 155, 172));
        graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        if (file.Icon != null) graphics.DrawImage(file.Icon, new Rectangle(10, 10, 20, 20));
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        string badge = count > 1 ? "+" + (count - 1) : "";
        int badgeWidth = count > 1 ? TextRenderer.MeasureText(badge, Font).Width + 8 : 0;
        TextRenderer.DrawText(graphics, file.Name, Font, new Rectangle(38, 0, Width - 46 - badgeWidth, Height), Color.White, flags);
        if (count > 1)
            TextRenderer.DrawText(graphics, badge, Font, new Rectangle(Width - badgeWidth, 0, badgeWidth, Height), Color.Gainsboro, flags);
    }
}
