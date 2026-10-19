using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class InstallerForm : Form
{
    private enum StepKind
    {
        Waiting,
        Working,
        Done,
        Bad
    }

    private static readonly Color Header = Color.FromArgb(18, 53, 47);
    private static readonly Color Ink = Color.FromArgb(24, 39, 34);
    private static readonly Color Muted = Color.FromArgb(70, 90, 82);
    private static readonly Color Green = Color.FromArgb(21, 122, 69);
    private static readonly Color Page = Color.FromArgb(247, 246, 243);
    private static readonly string[] StepNames =
    [
        "Step 1 — Account and remote administration",
        "Step 2 — Agent service",
        "Step 3 — Laptop console"
    ];

    private readonly Label _subtitle = new();
    private readonly Panel _setup = new();
    private readonly Panel _steps = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _saved = new();
    private readonly RadioButton _onLan = new();
    private readonly RadioButton _offsite = new();
    private readonly TextBox _group = new();
    private readonly Panel _ready = new();
    private readonly Label _readyTitle = new();
    private readonly Label _readyDetail = new();
    private readonly Button _install = new();
    private readonly Button _update = new();
    private readonly Button _check = new();
    private readonly Button _remove = new();
    private readonly Label _version = new();
    private readonly Button _again = new();
    private readonly TextBox _log = new();
    private readonly TextBox _stepLog = new();
    private readonly Label _status = new();
    private readonly Label[] _titles = new Label[3];
    private readonly Label[] _details = new Label[3];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private CancellationTokenSource? _work;
    private LanAnnouncer? _link;
    private int _announcing;
    private int _activeStep;
    private bool _installed;
    private bool _readyShown;

    public InstallerForm(bool statusOnly = false)
    {
        Text = "Workshop Link Agent";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 680);
        MinimumSize = new Size(700, 620);
        Font = new Font("Segoe UI", 10f);
        BackColor = Page;
        Icon = WorkshopIcon.Create();

        var header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Header };
        header.Controls.Add(new Label
        {
            Text = "Workshop Link Agent",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 16)
        });
        _subtitle.Text = "Prepare this PC so the workshop laptop can read, write, and run on it.";
        _subtitle.ForeColor = Color.FromArgb(214, 232, 224);
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(26, 52);
        header.Controls.Add(_subtitle);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 32;
        _status.BackColor = Color.FromArgb(232, 236, 233);
        _status.ForeColor = Ink;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(16, 0, 16, 0);
        _status.Text = statusOnly ? "Checking this PC." : "Waiting to install.";

        BuildSetup();
        BuildSteps();
        BuildReady();

        Controls.Add(_ready);
        Controls.Add(_steps);
        Controls.Add(_setup);
        Controls.Add(_status);
        Controls.Add(header);
        _timer.Tick += OnTick;
        Shown += (_, _) => RefreshVersion();

        if (statusOnly)
            ShowAgentStatus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _work?.Cancel();
        _link?.Dispose();
        base.OnFormClosing(e);
    }

    private void BuildSetup()
    {
        _setup.Dock = DockStyle.Fill;
        _setup.BackColor = Page;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(24, 16, 24, 12)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 9; row++)
        {
            var style = row switch
            {
                3 or 5 => new RowStyle(SizeType.Absolute, 40),
                8 => new RowStyle(SizeType.Percent, 100),
                _ => new RowStyle(SizeType.AutoSize)
            };
            grid.RowStyles.Add(style);
        }

        var about = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Ink,
            Padding = new Padding(0, 0, 0, 8),
            Text =
                "This PC gets its own command center password. It is not a Windows logon, and it is not the password from another PC. " +
                "No WorkshopLink Windows account is created. If one is left over, install removes it. " +
                "The laptop that runs the command center should be used only for that job, not for mail or everyday browsing. " +
                "The command center connects on TCP 47633 from the local network. Remote Desktop uses this person's own Windows password.\r\n\r\n" +
                "Write this password down, then type it on the workshop laptop when you check access to this PC. " +
                "After install, this window stays open until the laptop can see this PC."
        };

        _password.ReadOnly = true;
        _password.Text = LinkSecret.Create();
        StyleBox(_password);
        _saved.Text = "I have written this password down. It works only on this PC.";
        _saved.AutoSize = true;
        _saved.ForeColor = Ink;
        _saved.Margin = new Padding(0, 4, 0, 8);
        _onLan.Text = "This PC is on the local network.";
        _offsite.Text = "This PC is offsite. Connect it through Cloudflare.";
        _onLan.AutoSize = true;
        _offsite.AutoSize = true;
        _onLan.ForeColor = Ink;
        _offsite.ForeColor = Ink;
        _onLan.Margin = new Padding(0, 8, 0, 2);
        _offsite.Margin = new Padding(0, 0, 0, 8);
        var place = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 4)
        };
        place.Controls.Add(_onLan);
        place.Controls.Add(_offsite);
        _group.PlaceholderText = "Group key, or leave blank";
        StyleBox(_group);
        var groupLine = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            ForeColor = Muted,
            Margin = new Padding(0, 6, 0, 2),
            Text = "Paste a group key from the command center. Local and offsite both use it. Leave it blank if this PC has no group."
        };
        var groupBox = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 4)
        };
        groupBox.Controls.Add(groupLine);
        groupBox.Controls.Add(_group);

        _version.AutoSize = true;
        _version.MaximumSize = new Size(680, 0);
        _version.ForeColor = Ink;
        _version.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _version.Margin = new Padding(0, 4, 0, 8);
        _version.Text = "Checking the installed agent…";

        _check.Text = "Check version";
        _install.Text = "Install";
        _update.Text = "Update";
        _remove.Text = "Remove from this PC";
        StyleQuiet(_check);
        StylePrimary(_install);
        StylePrimary(_update);
        StyleQuiet(_remove);
        _check.Click += (_, _) => RefreshVersion();
        _install.Click += async (_, _) => await InstallAsync(updating: false);
        _update.Click += async (_, _) => await InstallAsync(updating: true);
        _remove.Click += async (_, _) => await RemoveAsync();

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 4, 0, 8)
        };
        buttons.Controls.Add(_check);
        buttons.Controls.Add(_install);
        buttons.Controls.Add(_update);
        buttons.Controls.Add(_remove);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.FromArgb(244, 247, 245);
        _log.ForeColor = Ink;
        _log.Font = new Font("Consolas", 9.5f);
        _log.Dock = DockStyle.Fill;
        _log.Margin = new Padding(0, 4, 0, 0);

        grid.Controls.Add(about, 0, 0);
        grid.Controls.Add(_version, 0, 1);
        grid.Controls.Add(FieldLabel("This PC's command center password. Copy it before you install."), 0, 2);
        grid.Controls.Add(_password, 0, 3);
        grid.Controls.Add(_saved, 0, 4);
        grid.Controls.Add(place, 0, 5);
        grid.Controls.Add(groupBox, 0, 6);
        grid.Controls.Add(buttons, 0, 7);
        grid.Controls.Add(_log, 0, 8);
        _setup.Controls.Add(grid);
    }

    private void BuildSteps()
    {
        _steps.Dock = DockStyle.Fill;
        _steps.BackColor = Page;
        _steps.Visible = false;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(24, 16, 24, 12)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < 6; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = Ink,
            Padding = new Padding(0, 0, 0, 12),
            Text = "The install finishes only when all three steps are green. Leave this window open while it reaches the laptop."
        };
        grid.Controls.Add(intro, 0, 0);

        for (var i = 0; i < 3; i++)
        {
            _titles[i] = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                Margin = new Padding(0, 8, 0, 0),
                Text = "○  " + StepNames[i]
            };
            _details[i] = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(680, 0),
                ForeColor = Muted,
                Margin = new Padding(28, 2, 0, 4),
                Text = "Waiting."
            };
            grid.Controls.Add(_titles[i], 0, 1 + (i * 2));
            grid.Controls.Add(_details[i], 0, 2 + (i * 2));
        }

        _again.Text = "Try again";
        StylePrimary(_again);
        _again.Visible = false;
        _again.Click += (_, _) =>
        {
            _timer.Stop();
            _steps.Visible = false;
            _setup.Visible = true;
            _subtitle.Text = "Prepare this PC so the workshop laptop can read, write, and run on it.";
            _status.Text = "Waiting to install.";
        };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 8)
        };
        buttons.Controls.Add(_again);
        grid.Controls.Add(buttons, 0, 7);

        _stepLog.Multiline = true;
        _stepLog.ReadOnly = true;
        _stepLog.ScrollBars = ScrollBars.Vertical;
        _stepLog.BorderStyle = BorderStyle.None;
        _stepLog.BackColor = Color.FromArgb(244, 247, 245);
        _stepLog.ForeColor = Ink;
        _stepLog.Font = new Font("Consolas", 9.5f);
        _stepLog.Dock = DockStyle.Fill;
        _stepLog.Margin = new Padding(0, 4, 0, 0);
        grid.Controls.Add(_stepLog, 0, 8);
        _steps.Controls.Add(grid);
    }

    private void RefreshVersion()
    {
        var here = LinkConstants.ProductVersion;
        var installed = InstalledVersion();
        var runningHere = string.Equals(
            Path.GetFullPath(Environment.ProcessPath ?? ""),
            Path.GetFullPath(Path.Combine(LinkConstants.InstallDirectory, "WorkshopAgent.exe")),
            StringComparison.OrdinalIgnoreCase);
        if (installed is null)
        {
            _version.Text = "This copy is " + here + ". Workshop Link is not installed on this PC.";
            _install.Enabled = true;
            _update.Enabled = false;
        }
        else if (installed == here)
        {
            _version.Text = "This PC already has Workshop Link agent " + here + ".";
            _install.Enabled = !runningHere;
            _update.Enabled = false;
        }
        else if (Newer(here, installed))
        {
            _version.Text = "This PC has agent " + installed + ". Install removes it first, then installs " + here + ".";
            _install.Enabled = false;
            _update.Enabled = !runningHere;
            if (runningHere)
                _version.Text += " Close this window and run the newer WorkshopAgent.exe. The installed shortcut cannot replace itself.";
        }
        else
        {
            _version.Text = "This PC has agent " + installed + ", which is newer than this copy (" + here + ").";
            _install.Enabled = false;
            _update.Enabled = false;
        }
    }

    private static string? InstalledVersion()
    {
        foreach (var directory in new[] { LinkConstants.InstallDirectory, @"C:\Program Files\WorkshopLink" })
        {
            var candidate = Path.Combine(directory, "WorkshopAgent.exe");
            if (!File.Exists(candidate))
                continue;
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(candidate);
            var version = info.ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
        }
        return QueryService() == "missing" ? null : "unknown";
    }

    private static bool Newer(string here, string installed)
    {
        if (installed == "unknown")
            return true;
        if (Version.TryParse(here, out var current) && Version.TryParse(installed, out var present))
            return current > present;
        return !string.Equals(here, installed, StringComparison.OrdinalIgnoreCase);
    }

    private async Task InstallAsync(bool updating)
    {
        if (!_onLan.Checked && !_offsite.Checked)
        {
            MessageBox.Show(this, "Choose workshop LAN or offsite before installing.", "Workshop Link");
            return;
        }
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
        if (_onLan.Checked && MachineFacts.WorkshopIpv4() is null)
        {
            var proceed = MessageBox.Show(
                this,
                "This PC does not currently have a local network address, so the command center will not see it yet. Install anyway?",
                "Workshop Link",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (proceed != DialogResult.Yes)
                return;
        }

        if (KasperskyPresent() && !KasperskyReady())
            return;

        var confirm = MessageBox.Show(
            this,
            updating
                ? "The older Workshop Link agent will be removed first, then version " + LinkConstants.ProductVersion + " will be installed. Your Windows account stays."
                : _offsite.Checked
                    ? "This PC will join the workshop through Cloudflare. The picture uses the directory when the workshop LAN cannot be reached. Continue?"
                    : "This PC will accept administration from a command center on the same local network. Continue?",
            "Workshop Link",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (confirm != DialogResult.Yes)
            return;

        var group = _group.Text.Trim();
        if (group.Length > 0 && !AgentPlace.IsGroupKey(group))
        {
            MessageBox.Show(this, "The group key should look like ABCD-EFGH, or leave the box blank.", "Workshop Link");
            return;
        }
        var password = _password.Text;
        _password.Clear();
        _installed = false;
        _readyShown = false;
        _activeStep = 0;
        _again.Visible = false;
        _stepLog.Clear();
        ShowSteps(updating ? "Updating this PC, then connecting it to the laptop." : "Installing this PC, then connecting it to the laptop.");
        SetStep(0, StepKind.Working, "Using the signed-in Windows account and turning on remote administration.");
        SetStep(1, StepKind.Waiting, "The agent service and the desktop shortcut come next.");
        SetStep(2, StepKind.Waiting, "The laptop console is last.");

        var ok = await WithJob("Install", () =>
            RunAsync("Installing…", progress =>
            {
                if (LegacyAgent.Present())
                {
                    progress.Report("Removing the older Workshop Link agent first.");
                    LegacyAgent.Remove();
                    progress.Report("The older agent has been removed.");
                }
                return WorkshopSetup.InstallAsync(password, _offsite.Checked ? "offsite" : "lan", group, progress, _work!.Token);
            }));
        if (ok)
            StartLink();
        else
            _again.Visible = true;
    }

    private static bool KasperskyPresent()
    {
        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("avp").Length > 0)
                return true;
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            foreach (var root in roots)
            {
                if (!string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root, "Kaspersky Lab")))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    private bool KasperskyReady()
    {
        var answer = MessageBox.Show(
            this,
            "Kaspersky on this PC deletes Workshop Link during install.\r\n\r\n" +
            "The report is PDM:Trojan.Win32.Generic, reason Behavior analysis. That is Kaspersky System Watcher, not a virus signature. " +
            "A scan exclusion does not stop it. Kaspersky only skips that check for a Trusted application, and the Security Center policy lock has to allow the exception.\r\n\r\n" +
            "Before Install, in Kaspersky Endpoint Security open Threats and Exclusions. Add both Program Files folders as a trusted application and as a scan exclusion, and allow both services. Add:\r\n" +
            "C:\\Program Files\\Workshop Command Center Agent\\\r\n" +
            "C:\\Program Files\\Workshop Command Center\\\r\n" +
            "Turn on Do not monitor application activity. This only holds the alert while a false-positive ticket is open. System Watcher can still warn until Kaspersky changes the verdict. Restore anything already deleted, then run this installer again.\r\n\r\n" +
            "If the alert remains after that, send the file to Kaspersky as a false positive from https://support.kaspersky.com/general/false-positives\r\n\r\n" +
            "Continue only after the trusted-application rule is in place.",
            "Workshop Link",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        return answer == DialogResult.Yes;
    }

    private async Task RemoveAsync()
    {
        var confirm = MessageBox.Show(
            this,
            "Remove Workshop Link from this PC? Remote administration is turned off. Your Windows account stays.",
            "Workshop Link",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
            return;
        _timer.Stop();
        _readyShown = false;
        _ready.Visible = false;
        _steps.Visible = false;
        _setup.Visible = true;
        await WithJob("Uninstall", () => RunAsync("Removing…", progress => WorkshopSetup.RemoveAsync(progress, _work!.Token)));
        RefreshVersion();
    }

    private JobProgressForm? _job;

    private async Task<T> WithJob<T>(string title, Func<Task<T>> work)
    {
        using var job = new JobProgressForm(title);
        _job = job;
        job.Show(this);
        try
        {
            return await work();
        }
        finally
        {
            _job = null;
            if (!job.IsDisposed)
                job.Close();
        }
    }

    private Task WithJob(string title, Func<Task> work) =>
        WithJob(title, async () =>
        {
            await work();
            return true;
        });

    private async Task<bool> RunAsync(string status, Func<IProgress<string>, Task> work)
    {
        SetBusy(true, status);
        _log.Clear();
        _work = new CancellationTokenSource();
        var progress = new Progress<string>(NoteInstallLine);
        try
        {
            await work(progress);
            if (!_installed && _steps.Visible)
                _status.Text = "Finished.";
            return _installed || !_steps.Visible;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
            if (_steps.Visible)
                SetStep(_activeStep, StepKind.Bad, "Cancelled before this step finished.");
            return false;
        }
        catch (Exception ex)
        {
            _log.AppendText(ex.Message + Environment.NewLine);
            _stepLog.AppendText(ex.Message + Environment.NewLine);
            _status.Text = "Stopped.";
            if (_steps.Visible)
                SetStep(_activeStep, StepKind.Bad, ex.Message);
            MessageBox.Show(this, ex.Message, "Workshop Link");
            return false;
        }
        finally
        {
            _work.Dispose();
            _work = null;
            SetBusy(false, _status.Text);
        }
    }

    private void NoteInstallLine(string line)
    {
        _job?.Report(line);
        _log.AppendText(line + Environment.NewLine);
        _stepLog.AppendText(line + Environment.NewLine);
        _status.Text = line;
        if (line.StartsWith("Removed the Workshop Link account", StringComparison.Ordinal)
            || line.StartsWith("Using ", StringComparison.Ordinal)
            || line.StartsWith("Added ", StringComparison.Ordinal)
            || line.StartsWith("This account has no", StringComparison.Ordinal)
            || line.StartsWith("Removed the old Workshop", StringComparison.Ordinal)
            || line.StartsWith("Set the network", StringComparison.Ordinal)
            || line.StartsWith("Could not set", StringComparison.Ordinal))
        {
            SetStep(0, StepKind.Working, line);
        }
        else if (line.StartsWith("Turned on Windows", StringComparison.Ordinal))
        {
            SetStep(0, StepKind.Working, "Remote administration is on. Limiting it to the workshop LAN.");
        }
        else if (line.StartsWith("Limited the built-in", StringComparison.Ordinal)
            || line.StartsWith("Allowed TCP 5985", StringComparison.Ordinal)
            || line.StartsWith("Remote Desktop is on", StringComparison.Ordinal)
            || line.StartsWith("This PC is Windows Home", StringComparison.Ordinal)
            || line.StartsWith("A Windows policy", StringComparison.Ordinal))
        {
            SetStep(0, StepKind.Done, "Remote administration is limited to the local network.");
        }
        else if (line.StartsWith("Copied the agent", StringComparison.Ordinal)
            || line.StartsWith("Placed a Workshop", StringComparison.Ordinal)
            || line.StartsWith("Could not place", StringComparison.Ordinal)
            || line.StartsWith("Could not create a shortcut", StringComparison.Ordinal))
        {
            _activeStep = 1;
            SetStep(0, StepKind.Done, "Remote administration is limited to the local network.");
            SetStep(1, StepKind.Working, line);
        }
        else if (line.StartsWith("Started the Workshop Link Agent", StringComparison.Ordinal))
        {
            _activeStep = 2;
            SetStep(1, StepKind.Done, "The agent service is running. The desktop shortcut is Workshop Link Agent.");
        }
        else if (line.StartsWith("This PC has no local network", StringComparison.Ordinal))
        {
            SetStep(2, StepKind.Working, line);
        }
        else if (line.StartsWith("RESULT: installed", StringComparison.Ordinal))
        {
            _installed = true;
            _activeStep = 2;
            SetStep(0, StepKind.Done, "This Windows account can be administered from the workshop LAN. No extra account was created.");
            SetStep(1, StepKind.Done, "The agent service is running. A Workshop Link Agent shortcut is on the desktop.");
            if (OffsiteInstall())
            {
                SetStep(2, StepKind.Done, "This PC is offsite. Open Workshop Link on the laptop. It appears under Offsite agents.");
                _status.Text = "Offsite. Joining through Cloudflare.";
                ShowOffsiteReady();
            }
            else
            {
                SetStep(2, StepKind.Working, "Trying to reach the command center on this network. Open Workshop Link on that PC.");
                _status.Text = "Trying to connect to the laptop…";
            }
        }
        else if (line.StartsWith("This PC is offsite", StringComparison.Ordinal))
        {
            SetStep(2, StepKind.Done, line);
            _status.Text = "Offsite. Joining through Cloudflare.";
        }
    }

    private bool OffsiteInstall() => _offsite.Checked || AgentPlace.IsOffsite();

    private void ShowAgentStatus()
    {
        _setup.Visible = false;
        _steps.Visible = false;
        _ready.Visible = true;
        _subtitle.Text = "Agent status";
        RefreshAgentStatus();
        _timer.Tick -= OnTick;
        _timer.Tick += (_, _) => RefreshAgentStatus();
        _timer.Start();
    }

    private void RefreshAgentStatus()
    {
        var service = QueryService();
        var place = AgentPlace.IsOffsite() ? "offsite" : "lan";
        var cloud = DirectoryConnected();
        var online = service == "running" && (place == "lan" || cloud);
        if (online)
        {
            _readyTitle.Text = "✓   The agent is running perfectly.";
            _readyTitle.ForeColor = Green;
            _readyDetail.Text = place == "offsite"
                ? "Online. Connected to the command center through Cloudflare."
                : "Online. This PC is on the workshop LAN and the agent service is running.";
            _status.Text = "Online.";
            return;
        }

        if (service == "running")
        {
            _readyTitle.Text = "The agent is running.";
            _readyTitle.ForeColor = Ink;
            _readyDetail.Text = "It is still joining Cloudflare. This page updates on its own.";
            _status.Text = "Joining Cloudflare.";
            return;
        }

        _readyTitle.Text = "The agent is offline.";
        _readyTitle.ForeColor = Color.FromArgb(153, 27, 47);
        _readyDetail.Text = "The WorkshopCommandCenterAgent service is not running.";
        _status.Text = "Offline.";
    }

    private static bool DirectoryConnected()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "announce.log");
            if (!File.Exists(path))
                return false;
            var lines = File.ReadAllLines(path);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("directory ", StringComparison.Ordinal))
                    continue;
                return lines[i].Contains("connected", StringComparison.OrdinalIgnoreCase)
                    && !lines[i].Contains("not", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (IOException)
        {
        }
        return false;
    }

    private void ShowCurrentInstallation()
    {
        var service = QueryService();
        ShowSteps("This window keeps reaching the laptop. The service does the same after you close it.");
        if (service == "running")
        {
            SetStep(0, StepKind.Done, "Workshop Link is already installed on this PC.");
            SetStep(1, StepKind.Done, "The Workshop Link Agent service is running.");
        }
        else if (service == "stopped")
        {
            SetStep(0, StepKind.Done, "Workshop Link is installed on this PC.");
            SetStep(1, StepKind.Bad, "The agent service is stopped. Run Install again from the agent program.");
        }
        else
        {
            SetStep(0, StepKind.Waiting, "Workshop Link is not installed yet. Close this window and run Install.");
            SetStep(1, StepKind.Waiting, "The service is created by Install.");
        }
        if (OffsiteInstall())
        {
            SetStep(2, StepKind.Done, "This PC is offsite. Open Workshop Link on the laptop. It appears under Offsite agents.");
            _status.Text = "Offsite. Joining through Cloudflare.";
            ShowOffsiteReady();
            return;
        }

        SetStep(2, StepKind.Working, "Trying to reach the command center on this network. Open Workshop Link on that PC.");
        StartLink();
    }

    private void BuildReady()
    {
        _ready.Dock = DockStyle.Fill;
        _ready.BackColor = Page;
        _ready.Visible = false;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(24, 28, 24, 12)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _readyTitle.Text = "This PC is connected.";
        _readyTitle.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
        _readyTitle.ForeColor = Green;
        _readyTitle.AutoSize = true;
        _readyTitle.Margin = new Padding(0, 8, 0, 8);

        _readyDetail.Text = "The workshop laptop can see this PC. You can close this window. The agent keeps running.";
        _readyDetail.ForeColor = Ink;
        _readyDetail.AutoSize = true;
        _readyDetail.MaximumSize = new Size(680, 0);
        _readyDetail.Margin = new Padding(0, 0, 0, 16);

        var done = new Button { Text = "Close", AutoSize = true, Margin = new Padding(0, 0, 12, 0) };
        StylePrimary(done);
        done.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.Add(done);

        grid.Controls.Add(_readyTitle, 0, 0);
        grid.Controls.Add(_readyDetail, 0, 1);
        grid.Controls.Add(buttons, 0, 2);
        _ready.Controls.Add(grid);
    }

    private void ShowReady(string where)
    {
        if (_readyShown)
            return;
        _readyShown = true;
        _timer.Stop();
        _setup.Visible = false;
        _steps.Visible = false;
        _ready.Visible = true;
        _subtitle.Text = "Connected to the workshop laptop.";
        _readyDetail.Text = "Connected to " + where + ". The laptop can administer this Windows account. You can close this window. The agent keeps running.";
        _status.Text = "Connected.";
    }

    private void ShowOffsiteReady()
    {
        if (_readyShown)
            return;
        _readyShown = true;
        _timer.Stop();
        _setup.Visible = false;
        _steps.Visible = false;
        _ready.Visible = true;
        _readyTitle.Text = "This PC is offsite.";
        _subtitle.Text = "Joining the command center through Cloudflare.";
        _readyDetail.Text = "This PC is offsite. Open Workshop Link on the laptop and this PC appears under Offsite agents. You can close this window. The agent keeps running.";
        _status.Text = "Offsite. Joining through Cloudflare.";
    }

    private void ShowSteps(string subtitle)
    {
        _subtitle.Text = subtitle;
        _setup.Visible = false;
        _steps.Visible = true;
    }

    private void StartLink()
    {
        if (OffsiteInstall())
        {
            NoteInstallLine("This PC is offsite. It joins the command center through Cloudflare.");
            ShowOffsiteReady();
            return;
        }

        _link ??= new LanAnnouncer();
        _ = Task.Run(() =>
        {
            try
            {
                var note = AgentTunnel.Ensure();
                if (!IsDisposed)
                    BeginInvoke(() => NoteInstallLine(note));
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    BeginInvoke(() => NoteInstallLine("Workshop tunnel: " + ex.Message));
            }
        });
        _timer.Start();
        OnTick(this, EventArgs.Empty);
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_link is null)
            return;
        if (Interlocked.Exchange(ref _announcing, 1) == 1)
            return;
        try
        {
            var link = _link;
            await Task.Run(link.Announce);
            if (IsDisposed)
                return;
            var ack = link.FreshAck();
            if (ack is not null)
            {
                var where = string.IsNullOrWhiteSpace(ack.Ip) ? ack.Hostname : ack.Hostname + " at " + ack.Ip;
                SetStep(2, StepKind.Done, "Connected to " + where + ". This PC is on the laptop console.");
                _status.Text = "Connected to " + ack.Hostname + ".";
                ShowReady(where);
            }
            else
            {
                var problem = link.LastProblem;
                var detail = string.IsNullOrWhiteSpace(problem)
                    ? "Trying to reach the command center on this network. Open Workshop Link on that PC."
                    : "Trying to connect. " + problem;
                SetStep(2, StepKind.Working, detail);
                _status.Text = "Trying to connect to the laptop…";
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                SetStep(2, StepKind.Working, "Trying to connect. " + ex.Message);
                _status.Text = "Trying to connect to the laptop…";
            }
        }
        finally
        {
            Interlocked.Exchange(ref _announcing, 0);
        }
    }

    private void SetStep(int index, StepKind kind, string detail)
    {
        _titles[index].ForeColor = kind switch
        {
            StepKind.Done => Green,
            StepKind.Bad => Color.FromArgb(153, 27, 47),
            StepKind.Working => Ink,
            _ => Muted
        };
        var mark = kind == StepKind.Waiting ? "○" : "●";
        _titles[index].Text = mark + "  " + StepNames[index];
        _details[index].Text = detail;
    }

    private static string QueryService()
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
                return "unknown";
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return "running";
            if (text.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                return "stopped";
            return "missing";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _check.Enabled = !busy;
        _password.Enabled = !busy;
        _saved.Enabled = !busy;
        _onLan.Enabled = !busy;
        _offsite.Enabled = !busy;
        _again.Enabled = !busy;
        if (busy)
        {
            _install.Enabled = false;
            _update.Enabled = false;
            _remove.Enabled = false;
        }
        else
        {
            _remove.Enabled = true;
            RefreshVersion();
        }
        _status.Text = status;
        UseWaitCursor = busy;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(700, 0),
        ForeColor = Muted,
        Margin = new Padding(0, 8, 0, 2)
    };

    private static void StyleBox(TextBox box)
    {
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = new Font("Segoe UI", 12f);
        box.Margin = new Padding(0, 0, 0, 6);
        box.Dock = DockStyle.Fill;
    }

    private static void StylePrimary(Button button)
    {
        button.AutoSize = true;
        button.Padding = new Padding(16, 6, 16, 6);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Green;
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 8, 8, 8);
    }

    private static void StyleQuiet(Button button)
    {
        button.AutoSize = true;
        button.Padding = new Padding(12, 6, 12, 6);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(180, 70, 80);
        button.BackColor = Page;
        button.ForeColor = Color.FromArgb(153, 27, 47);
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 8, 0, 8);
    }
}
