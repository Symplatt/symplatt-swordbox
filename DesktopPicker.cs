using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SwordBox;

internal sealed class DesktopPicker : Form
{
    public string[] Chosen { get; private set; } = Array.Empty<string>();
    public DesktopPicker(Storage store)
    {
        Text = "收纳桌面文件"; Size = new Size(720, 490); MinimumSize = new Size(520, 340);
        StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(245, 247, 251); Padding = new Padding(16);
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, MultiSelect = true, BorderStyle = BorderStyle.None };
        list.Columns.Add("名称", 460); list.Columns.Add("类型", 160);
        var files = store.Available().Select(p => { try { return new DesktopFile(p); } catch { return null; } }).Where(f => f != null).Cast<DesktopFile>().OrderBy(f => f.Name).ToArray();
        var search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "筛选桌面文件", AccessibleName = "筛选桌面文件" };
        void Fill()
        {
            var checkedPaths = list.CheckedItems.Cast<ListViewItem>().Select(i => ((DesktopFile)i.Tag!).Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            list.BeginUpdate(); list.Items.Clear();
            foreach (var file in files.Where(f => f.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
                list.Items.Add(new ListViewItem(new[] { file.Name, file.Type }) { Tag = file, Checked = checkedPaths.Contains(file.Path) });
            list.EndUpdate();
        }
        search.TextChanged += (_, _) => Fill();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var add = new Button { Text = "收纳所选", Width = 96 }; var cancel = new Button { Text = "取消", Width = 80, DialogResult = DialogResult.Cancel }; var all = new Button { Text = "全选", Width = 80 };
        footer.Controls.Add(add); footer.Controls.Add(cancel); footer.Controls.Add(all);
        all.Click += (_, _) => { foreach (ListViewItem item in list.Items) item.Checked = true; };
        add.Click += (_, _) => { Chosen = list.CheckedItems.Cast<ListViewItem>().Select(i => ((DesktopFile)i.Tag!).Path).ToArray(); if (Chosen.Length > 0) DialogResult = DialogResult.OK; };
        CancelButton = cancel; AcceptButton = add;
        var space = new Panel { Dock = DockStyle.Top, Height = 12 };
        Controls.Add(list); Controls.Add(space); Controls.Add(search); Controls.Add(footer); Fill();
    }
}
