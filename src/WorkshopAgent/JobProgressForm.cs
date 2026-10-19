namespace WorkshopAgent;

internal sealed class JobProgressForm : Form
{
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Value = 0, Height = 22, Width = 440, Left = 20, Top = 78 };
    private readonly Label _heading = new() { AutoSize = false, Left = 20, Top = 16, Width = 440, Height = 28, ForeColor = Color.White, Font = new Font("Segoe UI", 16f, FontStyle.Bold) };
    private readonly Label _action = new() { AutoSize = false, Left = 20, Top = 108, Width = 440, Height = 48, ForeColor = Color.FromArgb(203, 213, 225) };
    private readonly System.Windows.Forms.Timer _watch;
    private readonly string? _log;
    private long _read;

    public JobProgressForm(string text, string? log = null)
    {
        _log = log;
        var uninstall = text.StartsWith("Removing", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase);
        Text = "Workshop Link";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(480, 180);
        BackColor = Color.FromArgb(15, 23, 42);
        Font = new Font("Segoe UI", 11f);
        _heading.Text = uninstall ? "Uninstall" : "Install";
        _action.Text = text;
        Controls.Add(_heading);
        Controls.Add(_bar);
        Controls.Add(_action);
        _watch = new System.Windows.Forms.Timer { Interval = 200 };
        _watch.Tick += (_, _) => ReadLog();
        if (!string.IsNullOrEmpty(log))
            _watch.Start();
    }

    public void Report(string line)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => Report(line));
            return;
        }

        if (line.StartsWith("STEP ", StringComparison.Ordinal))
        {
            var rest = line["STEP ".Length..];
            var space = rest.IndexOf(' ');
            var fraction = space < 0 ? rest : rest[..space];
            var detail = space < 0 ? rest : rest[(space + 1)..];
            var slash = fraction.IndexOf('/');
            if (slash > 0 && int.TryParse(fraction[..slash], out var current) && int.TryParse(fraction[(slash + 1)..], out var total) && total > 0)
                _bar.Value = Math.Clamp(current * 100 / total, 0, 100);
            _action.Text = detail;
            return;
        }

        if (line.StartsWith("RESULT:", StringComparison.Ordinal))
        {
            _bar.Value = 100;
            _action.Text = line.Contains("removed", StringComparison.OrdinalIgnoreCase) ? "Uninstall finished." : "Install finished.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(line))
            _action.Text = line;
    }

    private void ReadLog()
    {
        if (string.IsNullOrEmpty(_log) || !File.Exists(_log))
            return;
        try
        {
            using var stream = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _read)
                _read = 0;
            stream.Position = _read;
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                Report(line);
            _read = stream.Position;
        }
        catch (IOException)
        {
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watch.Stop();
        _watch.Dispose();
        base.OnFormClosed(e);
    }
}
