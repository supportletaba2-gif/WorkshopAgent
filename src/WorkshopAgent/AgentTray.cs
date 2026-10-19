using System.Diagnostics;

namespace WorkshopAgent;

internal static class AgentTray
{
    private const string EventName = "Global\\WorkshopLinkAgentTray";

    public static void Run(bool showWindow)
    {
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out var created);
        if (!created)
        {
            signal.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        var context = new TrayContext(showWindow, signal);
        Application.Run(context);
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly EventWaitHandle _signal;
        private readonly NotifyIcon _tray;
        private StatusForm? _window;

        public TrayContext(bool showWindow, EventWaitHandle signal)
        {
            _signal = signal;
            var icon = WorkshopIcon.Create();
            _tray = new NotifyIcon
            {
                Icon = icon,
                Visible = true,
                Text = "Workshop Link Agent"
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Status", null, (_, _) => ShowStatus());
            menu.Items.Add("Change password", null, (_, _) => ChangePassword());
            menu.Items.Add("Exit tray", null, (_, _) => ExitThread());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ShowStatus();
            var pump = new Control();
            pump.CreateControl();
            var timer = new System.Windows.Forms.Timer { Interval = 4000 };
            timer.Tick += (_, _) => _tray.Text = StatusText();
            timer.Start();
            _tray.Text = StatusText();
            if (showWindow)
                ShowStatus();
            var waiter = new Thread(() =>
            {
                while (_signal.WaitOne())
                    pump.BeginInvoke(ShowStatus);
            })
            { IsBackground = true };
            waiter.Start();
        }

        private static void ChangePassword()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "",
                Arguments = "--change-password",
                UseShellExecute = true
            });
        }

        private void ShowStatus()
        {
            if (_window is { IsDisposed: false })
            {
                _window.Show();
                _window.WindowState = FormWindowState.Normal;
                _window.Activate();
                return;
            }
            _window = new StatusForm();
            _window.Show();
        }

        protected override void ExitThreadCore()
        {
            _tray.Visible = false;
            _tray.Dispose();
            base.ExitThreadCore();
        }
    }

    private static string StatusText()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = "query WorkshopCommandCenterAgent",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            var text = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(3000);
            if (text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return "Workshop Link Agent is running";
            if (text.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                return "Workshop Link Agent is offline";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return "Workshop Link Agent";
    }
}
