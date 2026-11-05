using System.Drawing;
using System.Windows.Forms;

namespace SwordBox;

internal sealed class ResizeOutline : Form
{
    public ResizeOutline()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = TransparencyKey = Color.Magenta;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        using var outer = new Pen(Color.FromArgb(36, 40, 46));
        using var inner = new Pen(Color.Gainsboro);
        e.Graphics.DrawRectangle(outer, 0, 0, Width - 1, Height - 1);
        e.Graphics.DrawRectangle(inner, 1, 1, Width - 3, Height - 3);
    }
}
