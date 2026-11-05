using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SwordBox;

public sealed class DesktopBox : Form
{
    private const int ResizeBorder = 7, CornerSpan = 20;
    private const int FileIconInset = 10;
    private static readonly Color Surface = Color.FromArgb(14, 14, 14);
    private static readonly Color Table = Color.FromArgb(20, 20, 20);
    private static readonly Color Muted = Color.FromArgb(195, 199, 204);
    public Box Box { get; }
    private readonly FileGrid grid = new();
    public DataGridView Files => grid;
    private readonly ContextMenuStrip fileMenu = new();
    private ContextMenuStrip? boxMenu;
    private readonly DesktopManager manager;
    private readonly Panel header = new() { Dock = DockStyle.Top, Height = 42 };
    private readonly Label title = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) };
    private readonly Label empty = new() { Text = "拖入桌面文件，或点击 + 新建", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, BackColor = Table };
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
    private readonly GlyphButton add, menuButton, closeButton;
    private string signature = "";
    private Native.Point down;
    private Native.Rect original;
    private bool moving;
    private BoxEdge resizing;
    private readonly Timer resizeTimer = new() { Interval = 16 };
    private ResizeOutline? resizeOutline;
    private Rectangle resizeBounds;
    public int BoxOpacity => Math.Clamp(manager.Store.Config.BoxOpacity, Configuration.MinimumOpacity, 100);
    public bool Attached => IsHandleCreated && Native.IsDesktopBox(Handle);
    public int MoveCount { get; private set; }
    public int MenuCount { get; private set; }
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; if (manager?.Verification != true) cp.ExStyle = (cp.ExStyle | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW; return cp; }
    }
    public DesktopBox(DesktopManager manager, Box box)
    {
        this.manager = manager; Box = box;
        Text = Program.Product + " · " + box.Name;
        ShowInTaskbar = manager.Verification; FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Surface; ForeColor = Color.WhiteSmoke;
        Padding = new Padding(ResizeBorder); MinimumSize = new Size(320, 220);
        Width = Math.Clamp((int)box.Width, MinimumSize.Width, Math.Max(MinimumSize.Width, SystemInformation.VirtualScreen.Width));
        Height = Math.Clamp((int)box.Height, MinimumSize.Height, Math.Max(MinimumSize.Height, SystemInformation.VirtualScreen.Height));
        Bounds = new Rectangle(ClampLocation((int)box.X, (int)box.Y, Width, Height), Size);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        title.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold); title.Text = box.Name;
        title.Location = new Point(16, 0); title.Height = 42;
        add = new GlyphButton(Glyph.Add) { AccessibleName = "新建" };
        menuButton = new GlyphButton(Glyph.Menu) { AccessibleName = "收纳盒菜单" };
        closeButton = new GlyphButton(Glyph.Close) { AccessibleName = "关闭收纳盒并恢复文件图标" };
        add.Click += (_, _) => newMenu.Show(add, new Point(0, add.Height));
        menuButton.Click += (_, _) => OpenMenu();
        closeButton.Click += (_, _) => manager.CloseBox(this);
        header.Controls.Add(title); header.Controls.Add(add); header.Controls.Add(menuButton); header.Controls.Add(closeButton);
        foreach (var control in new Control[] { header, title })
        {
            control.MouseDown += StartMove; control.MouseMove += MovePointer; control.MouseUp += StopPointer;
        }
        status.Font = new Font(Font.FontFamily, 8f); status.ForeColor = Muted;
        MouseDown += (_, e) =>
        {
            var edge = ResizeEdge(e.Location);
            if (e.Button == MouseButtons.Left && edge != BoxEdge.None) BeginPointer(this, edge);
        };
        MouseMove += (_, e) =>
        {
            if (resizing != BoxEdge.None) return;
            Cursor = ResizeEdge(e.Location) switch
            {
                BoxEdge.Left or BoxEdge.Right => Cursors.SizeWE,
                BoxEdge.Top or BoxEdge.Bottom => Cursors.SizeNS,
                (BoxEdge.Left | BoxEdge.Top) or (BoxEdge.Right | BoxEdge.Bottom) => Cursors.SizeNWSE,
                (BoxEdge.Right | BoxEdge.Top) or (BoxEdge.Left | BoxEdge.Bottom) => Cursors.SizeNESW,
                _ => Cursors.Default
            };
        };
        MouseLeave += (_, _) => { if (resizing == BoxEdge.None) Cursor = Cursors.Default; };
        MouseUp += StopPointer;
        // Non-activating desktop windows can lose capture outside their bounds.
        resizeTimer.Tick += (_, _) =>
        {
            if (Native.IsKeyHeld((int)Keys.Escape)) FinishResize(commit: false);
            else
            {
                UpdateResizeOutline();
                if (!Native.IsKeyHeld((int)Keys.LButton)) FinishResize(commit: true);
            }
        };

        ConfigureGrid();
        newMenu.Items.Add("文件夹", null, (_, _) => CreateItem("新建文件夹", directory: true));
        newMenu.Items.Add("文本文档", null, (_, _) => CreateItem("新建文本文档.txt"));
        newMenu.Items.Add("Markdown 文件", null, (_, _) => CreateItem("新建 Markdown 文件.md"));
        newMenu.Items.Add("空白文件", null, (_, _) => CreateItem("新建文件"));
        empty.ContextMenuStrip = content.ContextMenuStrip = newMenu;
        Files.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && Files.HitTest(e.X, e.Y).Type == DataGridViewHitTestType.None)
                newMenu.Show(Files, e.Location);
        };
        content.Controls.Add(Files); content.Controls.Add(empty); empty.BringToFront();
        Controls.Add(content); Controls.Add(status); Controls.Add(header);
        Resize += (_, _) =>
        {
            UpdateRegion();
            add.SetBounds(ClientSize.Width - 116, 5, 32, 32);
            menuButton.SetBounds(ClientSize.Width - 80, 5, 32, 32);
            closeButton.SetBounds(ClientSize.Width - 44, 5, 32, 32);
            title.Width = Math.Max(80, ClientSize.Width - 148);
            empty.Bounds = new Rectangle(12, 40, Math.Max(20, content.ClientSize.Width - 24), Math.Max(24, content.ClientSize.Height - 48));
        };
        Load += (_, _) => { OnResize(EventArgs.Empty); RefreshFiles(true); };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.WindowsShutDown) manager.Stop();
            else if (!manager.Stopping && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; manager.CloseBox(this); }
        };
        foreach (var target in new Control[] { this, grid, content, empty, status, header, title, add, menuButton, closeButton })
        {
            target.AllowDrop = true;
            target.DragEnter += (_, e) => e.Effect = DesktopDropEffect(e);
            target.DragOver += (_, e) => e.Effect = DesktopDropEffect(e);
            target.DragDrop += (_, e) =>
            {
                e.Effect = DesktopDropEffect(e);
                if (e.Effect != DragDropEffects.None && e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                    Collect(paths);
            };
        }
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F2) Rename(); if (e.KeyCode == Keys.F5) RefreshFiles(true); };
        if (manager.Verification) ApplyOpacity();
        Disposed += (_, _) => { resizeTimer.Dispose(); resizeOutline?.Dispose(); newMenu.Dispose(); fileMenu.Dispose(); boxMenu?.Dispose(); };
    }
    private static DragDropEffects DesktopDropEffect(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return DragDropEffects.None;
        if ((e.AllowedEffect & DragDropEffects.Link) != 0) return DragDropEffects.Link;
        return e.AllowedEffect & DragDropEffects.Copy;
    }
    private void ConfigureGrid()
    {
        Files.Dock = DockStyle.Fill; Files.BackgroundColor = Table; Files.BorderStyle = BorderStyle.None;
        Files.RowHeadersVisible = false; Files.AutoGenerateColumns = false;
        Files.AllowUserToAddRows = false; Files.AllowUserToDeleteRows = false; Files.ReadOnly = true;
        Files.AllowUserToResizeRows = false; Files.AllowUserToOrderColumns = false;
        Files.SelectionMode = DataGridViewSelectionMode.FullRowSelect; Files.MultiSelect = true;
        Files.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        Files.GridColor = Color.FromArgb(50, 50, 50);
        Files.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None; Files.EnableHeadersVisualStyles = false;
        Files.ColumnHeadersHeight = 34; Files.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        Files.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(36, 36, 36), ForeColor = Muted, Font = new Font(Font.FontFamily, 8.5f), Padding = new Padding(7, 0, 0, 0), SelectionBackColor = Color.FromArgb(36, 36, 36), SelectionForeColor = ForeColor };
        Files.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Table, ForeColor = ForeColor, SelectionBackColor = Color.FromArgb(47, 64, 85), SelectionForeColor = ForeColor, Padding = new Padding(7, 8, 7, 8), WrapMode = DataGridViewTriState.True };
        Files.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        Files.RowTemplate.MinimumHeight = 36;
        Files.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name", HeaderText = "名称", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 251, SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(39, 8, 7, 8) }
        });
        Files.Columns["Name"]!.HeaderCell.Style.Padding = new Padding(FileIconInset - 4, 0, 0, 0);
        Files.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0 || Files.Rows[e.RowIndex].Tag is not DesktopFile { Icon: Image icon }) return;
            e.Paint(e.ClipBounds, e.PaintParts);
            var graphics = e.Graphics!;
            var state = graphics.Save();
            graphics.SetClip(Rectangle.Intersect(e.ClipBounds, e.CellBounds));
            graphics.DrawImage(icon, e.CellBounds.Left + FileIconInset, e.CellBounds.Top + (e.CellBounds.Height - icon.Height) / 2);
            graphics.Restore(state);
            e.Handled = true;
        };
        Files.Columns.Add(new DataGridViewTextBoxColumn { Name = "Modified", HeaderText = "修改日期", Width = 146, MinimumWidth = 140, SortMode = DataGridViewColumnSortMode.Programmatic });
        Files.Columns.Add(new DataGridViewTextBoxColumn { Name = "Created", HeaderText = "创建日期", Width = 146, MinimumWidth = 140, SortMode = DataGridViewColumnSortMode.Programmatic });
        Files.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "类型", Width = 125, MinimumWidth = 100, SortMode = DataGridViewColumnSortMode.Programmatic });
        foreach (var name in new[] { "Modified", "Created", "Type" }) Files.Columns[name]!.DefaultCellStyle.ForeColor = Muted;
        Files.Columns["Modified"]!.Visible = Box.ShowModified;
        Files.Columns["Created"]!.Visible = Box.ShowCreated;
        Files.Columns["Type"]!.Visible = Box.ShowType;
        Files.ColumnHeaderMouseClick += (_, e) =>
        {
            string name = Files.Columns[e.ColumnIndex].Name;
            if (e.Button == MouseButtons.Right && name is ("Modified" or "Created" or "Type"))
            {
                SetColumnVisibility(name, false);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            Box.Descending = Box.Sort == name && !Box.Descending; Box.Sort = name;
            manager.Store.SaveEditedBox(Box); RefreshFiles(true);
        };
        grid.OpenRow += row => OpenFile((DesktopFile)Files.Rows[row].Tag!);
        grid.CanShowDragPreview = manager.CanPreviewDrop;
        Files.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) grid.OpenItem(e.RowIndex); };
        grid.DropFiles += point => manager.DropFiles(this, Selected().Select(file => file.Path).ToArray(), point);
        Files.CellMouseDown += (_, e) => { if (e.RowIndex >= 0 && e.Button == MouseButtons.Right && !Files.Rows[e.RowIndex].Selected) { Files.ClearSelection(); Files.Rows[e.RowIndex].Selected = true; } };
        Files.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; } if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };
        fileMenu.Items.Add("打开", null, (_, _) => OpenSelected());
        fileMenu.Items.Add("在资源管理器中定位", null, (_, _) => { if (Selected().FirstOrDefault() is DesktopFile f) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + f.Path + "\"") { UseShellExecute = true }); });
        fileMenu.Items.Add("复制文件路径", null, (_, _) => { var paths = Selected().Select(f => f.Path).ToArray(); if (paths.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths)); });
        fileMenu.Items.Add(new ToolStripSeparator()); fileMenu.Items.Add("移出收纳", null, (_, _) => RemoveSelected());
        fileMenu.Opening += (_, e) => { if (!Selected().Any()) e.Cancel = true; };
        Files.MouseDown += (_, _) => Native.SetFocus(Files.Handle);
    }
    public void RefreshFiles(bool force = false)
    {
        var rows = Box.Paths.Select(manager.Store.ItemPath).Where(p => File.Exists(p) || Directory.Exists(p)).Select(p => { try { return new DesktopFile(p); } catch { return null; } }).Where(f => f != null).Cast<DesktopFile>().ToList();
        string next = string.Join("|", rows.Select(f => f.Path + f.Modified.Ticks + f.Created.Ticks));
        if (!force && signature == next) return; signature = next;
        var selected = Selected().Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Func<DesktopFile, object> sort = Box.Sort switch { "Modified" => f => f.Modified, "Created" => f => f.Created, "Type" => f => f.Type, _ => f => f.Name };
        var grouped = rows.OrderBy(file => !file.IsFolder);
        if (Box.GroupByType)
        {
            static string FileType(DesktopFile file) => file.IsFolder ? "" : file.Type;
            grouped = Box.Sort == "Type" && Box.Descending
                ? grouped.ThenByDescending(FileType, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenBy(FileType, StringComparer.CurrentCultureIgnoreCase);
        }
        rows = (Box.Descending ? grouped.ThenByDescending(sort) : grouped.ThenBy(sort)).ToList();
        Files.SuspendLayout(); Files.Rows.Clear();
        foreach (var file in rows)
        {
            int index = Files.Rows.Add(file.Name, file.Modified.ToString("yyyy/MM/dd HH:mm"), file.Created.ToString("yyyy/MM/dd HH:mm"), file.Type);
            var row = Files.Rows[index];
            row.Tag = file; row.ContextMenuStrip = fileMenu; row.Cells[0].ToolTipText = file.Path;
        }
        Files.ClearSelection(); foreach (DataGridViewRow row in Files.Rows) row.Selected = row.Tag is DesktopFile f && selected.Contains(f.Path);
        foreach (DataGridViewColumn column in Files.Columns) column.HeaderCell.SortGlyphDirection = column.Name == Box.Sort ? Box.Descending ? SortOrder.Descending : SortOrder.Ascending : SortOrder.None;
        Files.ResumeLayout(); empty.Visible = rows.Count == 0; status.Text = rows.Count + " 个项目";
    }
    private IEnumerable<DesktopFile> Selected() => Files.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<DesktopFile>();
    public void Collect(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        foreach (string path in paths) try { manager.Store.Add(Box, path); } catch (Exception ex) { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
        manager.RefreshAll(); if (errors.Count > 0) manager.Error(string.Join("\n", errors));
    }
    private void RemoveSelected() => RemoveFiles(Selected().Select(file => file.Path).ToArray());
    public void RemoveFiles(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        foreach (string path in paths)
            try { manager.Store.Remove(Box, path); }
            catch (Exception ex) { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
        manager.RefreshAll();
        if (errors.Count > 0) manager.Error(string.Join("\n", errors));
    }
    private void OpenSelected()
    {
        if (Selected().FirstOrDefault() is DesktopFile file) OpenFile(file);
    }
    private void OpenFile(DesktopFile file)
    {
        try { Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true }); }
        catch (Exception ex) { manager.Error(ex.Message); }
    }
    public void PickFiles()
    {
        using var picker = new DesktopPicker(manager.Store);
        if (picker.ShowDialog(this) == DialogResult.OK) Collect(picker.Chosen);
    }
    private readonly ContextMenuStrip newMenu = new();
    private void CreateItem(string name, bool directory = false)
    {
        using var dialog = new Form { Text = "新建", ClientSize = new Size(342, 104), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, Font = Font };
        var input = new TextBox { Text = name, Bounds = new Rectangle(16, 16, 310, 28), MaxLength = 255, AccessibleName = "名称" };
        var save = new Button { Text = "新建", DialogResult = DialogResult.OK, Bounds = new Rectangle(162, 58, 78, 28) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(248, 58, 78, 28) };
        dialog.Controls.AddRange(new Control[] { input, save, cancel });
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => input.Select(0, directory ? name.Length : Path.GetFileNameWithoutExtension(name).Length);
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                string path = manager.Store.CreateItem(Box, input.Text, directory);
                manager.RefreshAll();
                Files.ClearSelection();
                foreach (DataGridViewRow row in Files.Rows)
                    if (row.Tag is DesktopFile file && file.Path == path)
                    {
                        Files.CurrentCell = row.Cells[0];
                        row.Selected = true;
                        break;
                    }
                return;
            }
            catch (Exception ex) { manager.Error(ex.Message); }
        }
    }
    private void Rename()
    {
        using var dialog = new Form { Text = "重命名收纳盒", Size = new Size(360, 150), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Font = Font };
        var input = new TextBox { Text = Box.Name, Bounds = new Rectangle(16, 16, 310, 28), MaxLength = 40 };
        var save = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(246, 58, 80, 28) };
        dialog.Controls.Add(input); dialog.Controls.Add(save); dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text) && input.Text.Trim() != Box.Name)
        {
            Box.Name = input.Text.Trim();
            title.Text = Box.Name;
            Text = Program.Product + " · " + Box.Name;
            manager.Store.SaveEditedBox(Box);
        }
    }
    private void OpenMenu()
    {
        MenuCount++;
        boxMenu?.Dispose();
        var menu = boxMenu = new ContextMenuStrip();
        menu.Items.Add("收纳文件", null, (_, _) => PickFiles());
        menu.Items.Add("移出所选文件", null, (_, _) => RemoveSelected()).Enabled = Selected().Any();
        menu.Items.Add("重命名", null, (_, _) => Rename());
        var columns = new ColumnMenuItem();
        columns.DropDownItems.Add(ColumnToggle("Modified"));
        columns.DropDownItems.Add(ColumnToggle("Created"));
        columns.DropDownItems.Add(ColumnToggle("Type"));
        menu.Items.Add(columns);
        var groupByType = new ToolStripMenuItem("优先按类型排序") { Checked = Box.GroupByType, CheckOnClick = true };
        groupByType.CheckedChanged += (_, _) =>
        {
            Box.GroupByType = groupByType.Checked;
            manager.Store.SaveEditedBox(Box);
            RefreshFiles(true);
        };
        menu.Items.Add(groupByType);
        menu.Items.Add("新建收纳盒", null, (_, _) => manager.NewBox());
        menu.Items.Add("刷新", null, (_, _) => RefreshFiles(true));
        if (manager.Verification) menu.Items.Add("检查桌面模式", null, (_, _) => manager.EnterDesktopVerification(this));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("解散收纳盒", null, (_, _) => manager.Dissolve(this));
        menu.Closed += (_, _) => columns.CloseColumns();
        menu.Show(menuButton, new Point(0, menuButton.Height));
    }
    public void SetDesktopVisibility(bool visible)
    {
        if (!visible && Native.IsWindowVisible(Handle))
        {
            FinishResize(commit: false);
            grid.CancelGesture();
            boxMenu?.Close();
            fileMenu.Close();
            newMenu.Close();
        }
        Native.SetDesktopBoxVisible(Handle, visible);
    }
    private ToolStripMenuItem ColumnToggle(string name)
    {
        var column = Files.Columns[name]!;
        var item = new ToolStripMenuItem(column.HeaderText) { Checked = column.Visible, CheckOnClick = true };
        item.CheckedChanged += (_, _) => SetColumnVisibility(name, item.Checked);
        return item;
    }
    private void SetColumnVisibility(string name, bool visible)
    {
        var column = Files.Columns[name]!;
        if (!visible && Files.CurrentCell?.OwningColumn == column) Files.CurrentCell = null;
        column.Visible = visible;
        switch (name)
        {
            case "Modified": Box.ShowModified = visible; break;
            case "Created": Box.ShowCreated = visible; break;
            case "Type": Box.ShowType = visible; break;
        }
        if (!visible && Box.Sort == name) { Box.Sort = "Name"; Box.Descending = false; }
        manager.Store.SaveEditedBox(Box);
        RefreshFiles(true);
    }
    private BoxEdge ResizeEdge(Point p)
    {
        bool onX = p.X < ResizeBorder || p.X >= ClientSize.Width - ResizeBorder;
        bool onY = p.Y < ResizeBorder || p.Y >= ClientSize.Height - ResizeBorder;
        BoxEdge result = BoxEdge.None;
        if (onX || onY && (p.X < CornerSpan || p.X >= ClientSize.Width - CornerSpan))
            result |= p.X < ClientSize.Width / 2 ? BoxEdge.Left : BoxEdge.Right;
        if (onY || onX && (p.Y < CornerSpan || p.Y >= ClientSize.Height - CornerSpan))
            result |= p.Y < ClientSize.Height / 2 ? BoxEdge.Top : BoxEdge.Bottom;
        return result;
    }
    private void StartMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2) Rename();
        else BeginPointer((Control)sender!, BoxEdge.None);
    }
    private void BeginPointer(Control control, BoxEdge edge)
    {
        Native.GetCursorPos(out down); Native.GetWindowRect(Handle, out original);
        moving = edge == BoxEdge.None; resizing = edge;
        control.Capture = true;
        if (resizing != BoxEdge.None)
        {
            resizeBounds = original.Bounds;
            resizeTimer.Start();
        }
    }
    private void MovePointer(object? sender, MouseEventArgs e)
    {
        if (!moving || (MouseButtons & MouseButtons.Left) == 0) return;
        Native.GetCursorPos(out var point);
        int dx = point.X - down.X, dy = point.Y - down.Y;
        manager.MoveBox(this, new Point(original.Left + dx, original.Top + dy));
    }
    private void UpdateResizeOutline()
    {
        Native.GetCursorPos(out var point);
        int dx = point.X - down.X, dy = point.Y - down.Y;
        int left = original.Left, right = original.Right, top = original.Top, bottom = original.Bottom;
        if ((resizing & BoxEdge.Left) != 0) left = Math.Min(left + dx, right - MinimumSize.Width);
        if ((resizing & BoxEdge.Right) != 0) right = Math.Max(right + dx, left + MinimumSize.Width);
        if ((resizing & BoxEdge.Top) != 0) top = Math.Min(top + dy, bottom - MinimumSize.Height);
        if ((resizing & BoxEdge.Bottom) != 0) bottom = Math.Max(bottom + dy, top + MinimumSize.Height);
        var bounds = manager.SnapBox(this, Rectangle.FromLTRB(left, top, right, bottom), resizing);
        if (bounds == resizeBounds) return;
        resizeBounds = bounds;
        resizeOutline ??= new ResizeOutline();
        resizeOutline.Bounds = bounds;
        if (!resizeOutline.Visible) resizeOutline.Show(this);
    }
    private void FinishResize(bool commit)
    {
        if (resizing == BoxEdge.None) return;
        resizing = BoxEdge.None;
        resizeTimer.Stop();
        Capture = false;
        if (commit && resizeBounds != original.Bounds)
        {
            var bounds = resizeBounds;
            Native.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x4 | 0x10);
            Persist(edited: true);
        }
        resizeOutline?.Hide();
    }
    private void StopPointer(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is not Control control) return;
        if (resizing != BoxEdge.None)
        {
            UpdateResizeOutline();
            FinishResize(commit: true);
        }
        else EndPointer(control);
    }
    private void EndPointer(Control control)
    {
        if (!moving) return;
        MoveCount++;
        moving = false;
        control.Capture = false;
        Persist(edited: true);
    }
    public void ApplyOpacity()
    {
        if (manager.Verification) Opacity = BoxOpacity / 100.0;
        else if (Attached) Native.SetBoxOpacity(Handle, BoxOpacity);
    }
    public void Persist(bool edited = false)
    {
        if (!IsHandleCreated || !Native.IsWindow(Handle)) return;
        Native.GetWindowRect(Handle, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        bool changed = Box.X != rect.Left || Box.Y != rect.Top || Box.Width != width || Box.Height != height;
        Box.X = rect.Left; Box.Y = rect.Top; Box.Width = width; Box.Height = height;
        if (edited && changed) manager.Store.SaveEditedBox(Box);
        else manager.Store.Save();
    }
    public void AttachToDesktop() { Native.GetWindowRect(Handle, out var rect); Native.Attach(this, rect.Left, rect.Top); }
    public void CaptureNativeWindow(string path)
    {
        using var bitmap = new Bitmap(Width, Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!Native.PrintWindow(Handle, dc, 0)) throw new InvalidOperationException("原生窗口截图失败。"); }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    public object Diagnostics()
    {
        Native.GetWindowRect(Handle, out var rect);
        return new { Box.Id, Box.Name, Hwnd = Handle.ToInt64(), Owner = Native.GetWindow(Handle, 4).ToInt64(), DesktopHost = Native.DesktopHost().ToInt64(), Attached, ShowInTaskbar = (Native.GetWindowLong(Handle, -20) & Native.WS_EX_TOOLWINDOW) == 0 && Native.GetWindow(Handle, 4) == IntPtr.Zero, IsChild = (Native.GetWindowLong(Handle, -16) & Native.WS_CHILD) != 0, Style = Native.GetWindowLong(Handle, -16), ExStyle = Native.GetWindowLong(Handle, -20), Bounds = new { rect.Left, rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top }, MoveCount, MenuCount, Files = Files.Rows.Count };
    }
    private static Point ClampLocation(int x, int y, int width, int height)
    {
        Rectangle area = Screen.FromPoint(new Point(x, y)).WorkingArea;
        return new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)), Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)));
    }
    private void UpdateRegion()
    {
        if (IsHandleCreated && Width >= 20 && Height >= 20) Native.RoundWindow(Handle, Width, Height);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRegion();
    }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen = new Pen(Color.FromArgb(83, 83, 83)); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x46 && message.LParam != IntPtr.Zero && Attached && manager?.Verification != true)
        {
            var position = Marshal.PtrToStructure<Native.WindowPosition>(message.LParam);
            if ((position.Flags & 0x40u) != 0 && manager!.BoxesHidden)
                position.Flags = (position.Flags & ~0x40u) | 0x80u;
            var after = Native.DesktopInsertAfter(Handle);
            if (after == Handle) position.Flags |= 0x4u;
            else { position.InsertAfter = after; position.Flags &= ~0x4u; }
            position.Flags |= 0x10u | 0x200u;
            Marshal.StructureToPtr(position, message.LParam, false);
        }
        if (message.Msg == 0x21 && Attached) { message.Result = new IntPtr(3); return; }
        base.WndProc(ref message);
    }
}

internal sealed class ColumnMenuItem : ToolStripMenuItem
{
    private bool toggling;

    public ColumnMenuItem() : base("显示列")
    {
        DropDown.Opening += (_, e) => e.Cancel = !toggling;
        DropDown.Closing += (_, e) =>
        {
            if (!toggling && Owner?.Visible == true &&
                e.CloseReason is ToolStripDropDownCloseReason.ItemClicked or ToolStripDropDownCloseReason.CloseCalled)
                e.Cancel = true;
        };
    }

    protected override void OnMouseEnter(EventArgs e) { }
    protected override void OnMouseDown(MouseEventArgs e) { }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (DropDown.Visible) CloseColumns();
        else
        {
            toggling = true;
            try { ShowDropDown(); }
            finally { toggling = false; }
        }
    }

    public void CloseColumns()
    {
        if (!DropDown.Visible) return;
        toggling = true;
        try { HideDropDown(); }
        finally { toggling = false; }
    }
}

internal enum Glyph { Add, Menu, Close }
internal sealed class GlyphButton : Control
{
    private readonly Glyph glyph;
    private bool hover;
    public GlyphButton(Glyph glyph)
    {
        this.glyph = glyph; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        MouseEnter += (_, _) => { hover = true; Invalidate(); }; MouseLeave += (_, _) => { hover = false; Invalidate(); };
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (hover) { using var b = new SolidBrush(Color.FromArgb(53, 53, 53)); g.FillEllipse(b, 2, 2, Width - 4, Height - 4); }
        using var pen = new Pen(Color.Gainsboro, 1.5f); int x = Width / 2, y = Height / 2;
        if (glyph == Glyph.Add) { g.DrawLine(pen, x - 5, y, x + 5, y); g.DrawLine(pen, x, y - 5, x, y + 5); }
        else if (glyph == Glyph.Menu) { using var b = new SolidBrush(pen.Color); for (int i = -1; i <= 1; i++) g.FillEllipse(b, x + i * 5 - 1, y - 1, 2, 2); }
        else if (glyph == Glyph.Close) { g.DrawLine(pen, x - 4, y - 4, x + 4, y + 4); g.DrawLine(pen, x + 4, y - 4, x - 4, y + 4); }
    }
}
