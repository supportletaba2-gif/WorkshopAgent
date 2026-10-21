using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using WorkshopLink.Protocol;
using Microsoft.Extensions.Hosting;

namespace WorkshopAgent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Has(args, "--h264-probe"))
        {
            string report;
            try
            {
                report = WorkshopLink.Screen.H264Codec.RoundTrip();
            }
            catch (Exception ex)
            {
                report = "threw " + ex.GetType().Name + " " + ex.Message;
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "wlink-h264.txt"), report);
            }
            Environment.Exit(report.StartsWith("ok ", StringComparison.Ordinal) ? 0 : 1);
        }

        if (Has(args, "--sfu-probe"))
        {
            var view = args.Any(arg => string.Equals(arg, "view", StringComparison.OrdinalIgnoreCase));
            PeerIdentity.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkshopLink", "identity.bin"));
            var log = Path.Combine(Path.GetTempPath(), "wlink-sfu-" + (view ? "view" : "source") + ".log");
            try
            {
                RunSfuProbe(view, log).GetAwaiter().GetResult();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.AppendAllText(log, ex.GetType().Name + " " + ex.Message);
                Environment.Exit(1);
            }
        }

        if (Has(args, "--configure"))
            Environment.Exit(AgentConfig.Apply(args));

        if (Has(args, "--remove-old"))
        {
            LegacyAgent.Remove();
            return;
        }

        if (Has(args, "--job"))
        {
            var text = string.Join(' ', args.SkipWhile(arg => !string.Equals(arg, "--job", StringComparison.OrdinalIgnoreCase)).Skip(1));
            ApplicationConfiguration.Initialize();
            Application.Run(new JobProgressForm(string.IsNullOrWhiteSpace(text) ? "Working…" : text));
            return;
        }

        if (Has(args, "--ask"))
        {
            var path = args.SkipWhile(arg => !string.Equals(arg, "--ask", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            var message = "A command center wants to manage this PC. Allow it? Only allow a command center you set up.";
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    message = File.ReadAllText(path);
            }
            catch (IOException)
            {
            }

            ApplicationConfiguration.Initialize();
            var yes = MessageBox.Show(
                message,
                "Workshop Link",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2,
                MessageBoxOptions.DefaultDesktopOnly | (MessageBoxOptions)0x40000);
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }

            Environment.Exit(yes == DialogResult.Yes ? 6 : 7);
        }

        if (Has(args, "--sas"))
        {
            SessionLaunch.CallSendSas();
            return;
        }

        if (Has(args, "--rdp-sas"))
        {
            SessionLaunch.SendRdpSecureSequence();
            return;
        }

        PeerIdentity.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "identity.bin"), machine: true);

        if (Has(args, "--screen"))
        {
            PhysicalDesktop.MakeAware();
            var name = args.SkipWhile(arg => !string.Equals(arg, "--screen", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(name))
                ScreenShare.RunHelper(name, args.Any(arg => string.Equals(arg, "full", StringComparison.OrdinalIgnoreCase)));
            return;
        }

        if (Has(args, "--service"))
        {
            try
            {
                var folder = Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrEmpty(folder))
                    Directory.SetCurrentDirectory(folder);
                var builder = Host.CreateApplicationBuilder(args);
                builder.Services.AddWindowsService(options => options.ServiceName = LinkConstants.ServiceName);
                builder.Services.AddHostedService<BeaconWorker>();
                builder.Services.AddHostedService<LinkWorker>();
                builder.Services.AddHostedService<AgentTrayHost>();
                builder.Build().Run();
            }
            catch (Exception ex)
            {
                NoteServiceFailure(ex);
                throw;
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
            MessageBox.Show(eventArgs.Exception.Message, "Workshop Link");

        if (Has(args, "--beacon"))
        {
            Application.Run(new BeaconForm());
            return;
        }

        if (Has(args, "--change-password"))
        {
            ApplicationConfiguration.Initialize();
            if (!Elevation.IsAdmin())
            {
                Elevation.RelaunchElevated(args);
                return;
            }
            Application.Run(new ChangePasswordForm());
            return;
        }

        if (Has(args, "--status") || Has(args, "--tray"))
        {
            AgentTray.Run(Has(args, "--status"));
            return;
        }

        if (!Elevation.IsAdmin())
        {
            Elevation.RelaunchElevated(args);
            return;
        }

        Application.Run(new InstallerForm());

        static async Task RunSfuProbe(bool view, string log)
        {
            await using var media = new SfuMedia();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            if (view)
            {
                var frames = 0;
                media.Frame += _ => frames++;
                await media.WatchAsync(null, PeerIdentity.Fingerprint, stop.Token);
                await Task.Delay(8000, stop.Token);
                File.AppendAllText(log, "frames " + frames + " " + media.Status);
                return;
            }

            await media.PublishAsync(null, PeerIdentity.Fingerprint, stop.Token);
            using var bitmap = new Bitmap(64, 64);
            var until = Environment.TickCount64 + 12000;
            while (Environment.TickCount64 < until && !stop.IsCancellationRequested)
            {
                if (WorkshopLink.Screen.H264Codec.TryEncodeAv1(bitmap, out var nal))
                    media.Submit(nal);
                await Task.Delay(200, stop.Token);
            }
            File.AppendAllText(log, "published " + media.Status);
        }
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static void NoteServiceFailure(Exception ex)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "service.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + ex + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}
