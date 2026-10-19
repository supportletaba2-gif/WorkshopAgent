using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class BeaconForm : Form
{
    private readonly Label _state = new();
    private readonly Label _detail = new();
    private readonly CancellationTokenSource _stop = new();

    public BeaconForm()
    {
        Text = "Workshop Link beacon test";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 240);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.FromArgb(247, 246, 243);
        Icon = WorkshopIcon.Create();

        var banner = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(18, 53, 47) };
        banner.Controls.Add(new Label
        {
            Text = "Announcing this PC",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 18)
        });

        _state.AutoSize = false;
        _state.Dock = DockStyle.Top;
        _state.Height = 48;
        _state.TextAlign = ContentAlignment.MiddleLeft;
        _state.Padding = new Padding(20, 12, 20, 0);
        _state.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _state.Text = "Starting…";

        _detail.Dock = DockStyle.Fill;
        _detail.Padding = new Padding(20, 8, 20, 16);
        _detail.Text =
            "This window only sends the computer name, Windows version, and address on the workshop LAN. " +
            "Remote administration is not turned on. Close this window to stop.";

        Controls.Add(_detail);
        Controls.Add(_state);
        Controls.Add(banner);
        Shown += async (_, _) => await AnnounceAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stop.Cancel();
        base.OnFormClosed(e);
    }

    private async Task AnnounceAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var ip = MachineFacts.WorkshopIpv4();
            if (ip is null)
            {
                _state.ForeColor = Color.FromArgb(146, 64, 14);
                _state.Text = "No local network address yet";
            }
            else
            {
                try
                {
                    BeaconSender.SendOnce();
                    _state.ForeColor = Color.FromArgb(21, 122, 69);
                    _state.Text = Environment.MachineName + "  ·  " + ip + "  ·  every " +
                                  (int)LinkConstants.BeaconInterval.TotalSeconds + "s";
                }
                catch (Exception ex)
                {
                    _state.ForeColor = Color.FromArgb(153, 27, 47);
                    _state.Text = ex.Message;
                }
            }

            try
            {
                await Task.Delay(LinkConstants.BeaconInterval, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
