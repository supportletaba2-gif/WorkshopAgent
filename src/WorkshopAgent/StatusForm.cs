using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class StatusForm : Form
{
    private static readonly Color Green = Color.FromArgb(21, 122, 69);
    private static readonly Color Ink = Color.FromArgb(24, 39, 34);
    private static readonly Color Muted = Color.FromArgb(120, 130, 126);
    private readonly Label _title = new();
    private readonly Label _agentMark = new();
    private readonly Label _cloudMark = new();
    private readonly Label _placeCaption = new();
    private readonly Label _placeMark = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    public StatusForm()
    {
        Text = "Workshop Link Agent";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 280);
        Font = new Font("Segoe UI", 11f);
        BackColor = Color.FromArgb(247, 246, 243);
        Icon = WorkshopIcon.Create();

        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Color.FromArgb(18, 53, 47) };
        header.Controls.Add(new Label
        {
            Text = "Workshop Link Agent",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 14)
        });
        _title.Text = "Checking the agent.";
        _title.ForeColor = Color.FromArgb(214, 232, 224);
        _title.AutoSize = true;
        _title.Location = new Point(22, 44);
        header.Controls.Add(_title);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 18, 22, 8),
            ColumnCount = 2
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        for (var row = 0; row < 4; row++)
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        AddRow(body, 0, "Agent is running", _agentMark);
        AddRow(body, 1, "Connected through Cloudflare", _cloudMark);
        AddRow(body, 2, _placeCaption, "Workshop LAN", _placeMark);

        var close = new Button { Text = "Close", AutoSize = true, Anchor = AnchorStyles.Left };
        close.Click += (_, _) => Close();
        body.Controls.Add(close, 0, 3);

        Controls.Add(body);
        Controls.Add(header);
        _timer.Tick += (_, _) => RefreshStatus();
        Shown += (_, _) =>
        {
            RefreshStatus();
            _timer.Start();
        };
        FormClosed += (_, _) => _timer.Stop();
    }

    private static void AddRow(TableLayoutPanel body, int row, string caption, Label mark) =>
        AddRow(body, row, new Label(), caption, mark);

    private static void AddRow(TableLayoutPanel body, int row, Label captionLabel, string caption, Label mark)
    {
        captionLabel.Text = caption;
        captionLabel.AutoSize = true;
        captionLabel.ForeColor = Ink;
        captionLabel.Anchor = AnchorStyles.Left;
        captionLabel.Margin = new Padding(0, 8, 0, 0);
        body.Controls.Add(captionLabel, 0, row);
        mark.Text = "–";
        mark.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        mark.ForeColor = Muted;
        mark.AutoSize = true;
        mark.Anchor = AnchorStyles.Right;
        mark.Margin = new Padding(0, 4, 0, 0);
        body.Controls.Add(mark, 1, row);
    }

    private void RefreshStatus()
    {
        var running = ServiceRunning();
        var offsite = AgentPlace.IsOffsite();
        var directory = LastDirectoryLine();
        var cloud = directory.Contains("directory connected", StringComparison.OrdinalIgnoreCase)
            && !directory.Contains("waiting", StringComparison.OrdinalIgnoreCase);
        SetMark(_agentMark, running);
        SetMark(_cloudMark, cloud);
        _placeCaption.Text = offsite ? "Offsite" : "Workshop LAN";
        SetMark(_placeMark, running);
        if (!running)
            _title.Text = "The agent is offline.";
        else if (offsite && !cloud)
        {
            var marker = "directory ";
            var at = directory.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var detail = at >= 0 ? directory[(at + marker.Length)..].Trim() : "";
            _title.Text = detail.Length == 0 ? "The agent is running. Joining Cloudflare." : detail;
        }
        else
            _title.Text = "The agent is running perfectly.";
    }

    private static void SetMark(Label mark, bool ok)
    {
        mark.Text = ok ? "✓" : "–";
        mark.ForeColor = ok ? Green : Muted;
    }

    private static bool ServiceRunning()
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                    Arguments = "query WorkshopCommandCenterAgent",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                }
            };
            if (!process.Start())
                return false;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string LastDirectoryLine()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "announce.log");
            if (!File.Exists(path))
                return "";
            var lines = File.ReadAllLines(path);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Contains("directory ", StringComparison.Ordinal))
                    return lines[i];
            }
        }
        catch (IOException)
        {
        }
        return "";
    }
}
