using System.Diagnostics;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class ChangePasswordForm : Form
{
    private readonly TextBox _password = new() { ReadOnly = true, Width = 360 };
    private readonly CheckBox _saved = new() { Text = "I have written this password down. It works only on this PC.", AutoSize = true };
    private readonly RadioButton _lan = new() { Text = "This PC is on the local network.", AutoSize = true };
    private readonly RadioButton _offsite = new() { Text = "This PC is offsite. Connect it through Cloudflare.", AutoSize = true, Checked = true };

    public ChangePasswordForm()
    {
        Text = "Workshop Link Agent";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 280);
        Font = new Font("Segoe UI", 10f);
        Icon = WorkshopIcon.Create();
        _password.Text = LinkSecret.Create();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "This PC's new command center password", AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Text = "This replaces the old password on this PC only. The other PCs keep their own.", AutoSize = true, MaximumSize = new Size(420, 0) }, 0, 1);
        layout.Controls.Add(_password, 0, 2);
        layout.Controls.Add(_saved, 0, 3);
        layout.Controls.Add(_lan, 0, 4);
        layout.Controls.Add(_offsite, 0, 5);
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => Save();
        layout.Controls.Add(save, 0, 6);
        Controls.Add(layout);
    }

    private void Save()
    {
        if (!_saved.Checked)
        {
            MessageBox.Show(this, "Write down this PC's password first. It is not the password from another PC.", "Workshop Link");
            return;
        }
        var problem = PasswordPolicy.Problem(_password.Text);
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "Workshop Link");
            return;
        }
        var code = AgentConfig.Apply(["--configure", _offsite.Checked ? "offsite" : "lan", _password.Text]);
        if (code != 0)
        {
            MessageBox.Show(this, problem ?? "Use at least 12 characters, with upper-case, lower-case, and a number or symbol.", "Workshop Link");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "stop WorkshopCommandCenterAgent",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(8000);
            Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "start WorkshopCommandCenterAgent",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(8000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        MessageBox.Show(this, "The command center password is saved. The agent service is running with that setting.", "Workshop Link");
        Close();
    }
}
