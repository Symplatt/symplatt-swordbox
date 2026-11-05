using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SwordBox;

internal sealed class FileGrid : DataGridView
{
    private enum Gesture { None, Select, Move }
    private readonly Timer scroll = new() { Interval = 90 };
    private Gesture gesture;
    private Point start;
    private int anchor;
    private bool dragged;
    private DragPreview? preview;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Point, bool>? CanShowDragPreview { get; set; }
    public event Action<int>? OpenRow;
    public event Action<Point>? DropFiles;
    public void OpenItem(int row) => OpenRow?.Invoke(row);

    public FileGrid()
    {
        DoubleBuffered = true;
        _ = new GridScrollBarSkin(this, VerticalScrollBar, () => BackgroundColor, vertical: true);
        _ = new GridScrollBarSkin(this, HorizontalScrollBar, () => BackgroundColor, vertical: false);
        scroll.Tick += (_, _) =>
        {
            // Desktop boxes cannot take foreground capture outside their own bounds.
            var point = PointToClient(MousePosition);
            if (Native.IsKeyHeld((int)Keys.Escape)) ResetGesture();
            else if (!Native.IsKeyHeld((int)Keys.LButton)) FinishGesture(point);
            else MoveGesture(point, scrollAtEdge: true);
        };
    }

    protected override void WndProc(ref Message message)
    {
        const int mouseMove = 0x200, leftDown = 0x201, leftUp = 0x202, doubleClick = 0x203;
        if (message.Msg is leftDown or doubleClick && (ModifierKeys & (Keys.Control | Keys.Shift)) == 0)
        {
            var point = MouseLocation(message);
            var hit = HitTest(point.X, point.Y);
            if (hit.RowIndex >= 0 || hit.Type == DataGridViewHitTestType.None && point.Y >= ColumnHeadersHeight && Rows.Count != 0)
            {
                Native.SetFocus(Handle);
                if (message.Msg == doubleClick)
                {
                    if (hit.RowIndex >= 0) OpenItem(hit.RowIndex);
                    return;
                }
                start = point;
                anchor = hit.RowIndex >= 0 ? hit.RowIndex : Rows.Count - 1;
                gesture = hit.RowIndex >= 0 && Rows[anchor].Selected ? Gesture.Move : Gesture.Select;
                if (gesture == Gesture.Select) SelectSingle(hit.RowIndex, hit.ColumnIndex);
                scroll.Interval = gesture == Gesture.Move ? 16 : 90;
                Capture = true;
                scroll.Start();
                return;
            }
        }
        if (gesture != Gesture.None)
        {
            if (message.Msg == mouseMove)
            {
                MoveGesture(MouseLocation(message), scrollAtEdge: false);
                return;
            }
            if (message.Msg == leftUp)
            {
                FinishGesture(MouseLocation(message));
                return;
            }
        }
        base.WndProc(ref message);
    }

    private void MoveGesture(Point point, bool scrollAtEdge)
    {
        var size = SystemInformation.DragSize;
        var threshold = new Rectangle(start.X - size.Width / 2, start.Y - size.Height / 2, size.Width, size.Height);
        dragged |= !threshold.Contains(point);
        if (!dragged) return;
        if (gesture == Gesture.Move)
        {
            Cursor = Cursors.SizeAll;
            var screenPoint = PointToScreen(point);
            if (CanShowDragPreview?.Invoke(screenPoint) == true &&
                anchor < Rows.Count && Rows[anchor].Tag is DesktopFile file)
            {
                preview ??= new DragPreview(file, SelectedRows.Count);
                preview.Follow(screenPoint);
            }
            else preview?.Hide();
        }
        else SelectTo(point, scrollAtEdge);
    }

    private void FinishGesture(Point point)
    {
        MoveGesture(point, scrollAtEdge: false);
        bool drop = dragged && gesture == Gesture.Move;
        if (!dragged && gesture == Gesture.Move)
        {
            var hit = HitTest(start.X, start.Y);
            SelectSingle(hit.RowIndex, hit.ColumnIndex);
        }
        ResetGesture();
        if (drop) DropFiles?.Invoke(PointToScreen(point));
    }

    private static Point MouseLocation(Message message)
    {
        long packed = message.LParam.ToInt64();
        return new Point((short)packed, (short)(packed >> 16));
    }

    private void SelectSingle(int row, int column)
    {
        if (row >= 0) CurrentCell = Rows[row].Cells[column];
        ClearSelection();
        if (row >= 0) Rows[row].Selected = true;
    }

    private void SelectTo(Point point, bool scrollAtEdge)
    {
        if (Rows.Count == 0 || anchor >= Rows.Count) return;
        int first = Math.Max(0, FirstDisplayedScrollingRowIndex);
        int last = Math.Min(Rows.Count - 1, first + Math.Max(1, DisplayedRowCount(true)) - 1);
        int bottom = GetRowDisplayRectangle(last, true).Bottom;
        if (scrollAtEdge)
        {
            if (point.Y < ColumnHeadersHeight + 8 && first > 0) FirstDisplayedScrollingRowIndex = first - 1;
            else if (point.Y >= bottom - 8 && last < Rows.Count - 1) FirstDisplayedScrollingRowIndex = first + 1;
            first = Math.Max(0, FirstDisplayedScrollingRowIndex);
            last = Math.Min(Rows.Count - 1, first + Math.Max(1, DisplayedRowCount(true)) - 1);
        }
        int row = HitTest(Math.Clamp(point.X, 0, ClientSize.Width - 1), point.Y).RowIndex;
        if (row < 0) row = point.Y < ColumnHeadersHeight ? first : last;
        int lower = Math.Min(row, anchor), upper = Math.Max(row, anchor);
        for (int i = 0; i < Rows.Count; i++)
            Rows[i].Selected = i >= lower && i <= upper;
    }

    private void ResetGesture()
    {
        gesture = Gesture.None;
        dragged = false;
        scroll.Stop();
        preview?.Dispose();
        preview = null;
        Cursor = Cursors.Default;
        if (Capture) Capture = false;
    }

    public void CancelGesture()
    {
        if (gesture != Gesture.None) ResetGesture();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ResetGesture(); scroll.Dispose(); }
        base.Dispose(disposing);
    }
}
