using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SwordBox;

internal sealed class SettingsForm : Form
{
    private readonly CheckBox startup;
    private bool initialStartup;
    private readonly TrackBar transparency;
    private readonly int initialOpacity;
    private readonly Action<int> saveOpacity;

    public SettingsForm(bool allowStartupChanges, Icon? icon, int opacity, Action<int> saveOpacity)
    {
        initialOpacity = Math.Clamp(opacity, Configuration.MinimumOpacity, 100);
        this.saveOpacity = saveOpacity;
        Text = "设置";
        if (icon != null) Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont!;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 174);

        initialStartup = allowStartupChanges && StartupSetting.IsEnabled();
        startup = new CheckBox
        {
            Text = "开机自启",
            AutoSize = true,
            Location = new Point(20, 22),
            Checked = initialStartup,
            Enabled = allowStartupChanges
        };
        var transparencyLabel = new Label { Text = "收纳盒透明度", AutoSize = true, Location = new Point(20, 53) };
        transparency = new TrackBar
        {
            AccessibleName = transparencyLabel.Text,
            Location = new Point(15, 76),
            Width = 235,
            Maximum = 100 - Configuration.MinimumOpacity,
            Value = 100 - initialOpacity,
            TickFrequency = 10,
            LargeChange = 10
        };
        var value = new Label { Text = $"{transparency.Value}%", AutoSize = true, Location = new Point(261, 80) };
        transparency.ValueChanged += (_, _) => value.Text = $"{transparency.Value}%";
        var ok = new Button { Text = "确定", Location = new Point(144, 126), Size = new Size(74, 28) };
        var cancel = new Button { Text = "取消", Location = new Point(226, 126), Size = new Size(74, 28) };
        ok.Click += async (_, _) => await SaveAndClose();
        cancel.Click += (_, _) => Close();
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { startup, transparencyLabel, transparency, value, ok, cancel });
    }

    private async Task SaveAndClose()
    {
        Enabled = false;
        try
        {
            if (startup.Checked != initialStartup)
            {
                await StartupSetting.SetAsync(startup.Checked);
                initialStartup = startup.Checked;
            }
            int opacity = 100 - transparency.Value;
            if (opacity != initialOpacity) saveOpacity(opacity);
            Close();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Enabled = true;
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
