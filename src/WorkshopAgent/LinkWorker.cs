using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class LinkWorker(ILogger<LinkWorker> logger) : BackgroundService
{
    private static readonly string[] AllowedKeys =
    [
        "WLINK_ACTION",
        "WLINK_HOST",
        "WLINK_CMD",
        "WLINK_PKG",
        "WLINK_REMOTE",
        "WLINK_LOCAL"
    ];

    private static readonly SemaphoreSlim Gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, LinkConstants.LinkPort);
        listener.Start();
        logger.LogInformation(
            "Workshop Link is listening for the command center on TCP {Port}. The announcement password is checked before any command runs.",
            LinkConstants.LinkPort);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleAsync(client, stoppingToken), stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        {
            client.ReceiveTimeout = 30_000;
            client.SendTimeout = 30_000;
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote is null || !LinkPipe.IsAllowed(remote.Address))
            {
                logger.LogWarning("Refused a command center connection from {Address}.", remote?.Address);
                return;
            }

            using var stream = await LinkSeal.OpenServerAsync(client.GetStream(), ActivePipeKey(), stoppingToken);
            await ServeAsync(stream, stoppingToken, local: TrustsLocalNetwork());
        }
    }

    internal static async Task ServeAsync(Stream stream, CancellationToken cancellationToken, RelaySession? pictures = null, DirectoryClient? directory = null, bool local = false)
    {
        await Gate.WaitAsync(cancellationToken);
        var released = false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromMinutes(30));
            var (password, _, environment, blob) = await LinkPipe.ReadRequestAsync(stream, limit.Token);
            var view = environment.TryGetValue("WLINK_ACTION", out var action) && string.Equals(action, "view", StringComparison.Ordinal);
            var denied = ConsoleGate.Admit(environment, password, out _, out var issued, local);
            if (denied is not null)
            {
                var message = Encoding.UTF8.GetBytes(denied);
                if (view)
                    await LinkPipe.WriteScreenAsync(stream, LinkPipe.ScreenBye, message, limit.Token);
                else
                    await LinkPipe.WriteResultAsync(stream, 1, "", denied, null, limit.Token);
                return;
            }

            if (view)
            {
                Gate.Release();
                released = true;
                OpenViews.Enter();
                try
                {
                    if (!string.IsNullOrEmpty(issued))
                        await LinkPipe.WriteScreenAsync(stream, LinkPipe.ScreenStatus, Encoding.UTF8.GetBytes("ticket " + issued), limit.Token);
                    await ScreenShare.ServeAsync(stream, pictures, limit.Token, directory);
                }
                finally
                {
                    OpenViews.Leave();
                }
                return;
            }

            var (code, output, error, file) = await RunAsync(BuiltInScript.Text, environment, blob, limit.Token);
            if (file is null && !string.IsNullOrEmpty(issued))
                file = Encoding.UTF8.GetBytes("ticket " + issued);
            await LinkPipe.WriteResultAsync(stream, code, output, error, file, limit.Token);
        }
        finally
        {
            if (!released)
                Gate.Release();
        }
    }

    internal static byte[] ActivePipeKey() =>
        TrustsLocalNetwork() ? LinkConstants.LocalPipeKey : ReadPipeKey() ?? [];

    internal static bool TrustsLocalNetwork() => !AgentPlace.IsOffsite();

    internal static byte[]? ReadPipeKey()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "link.key");
            if (!File.Exists(path))
                return null;
            var key = File.ReadAllBytes(path);
            return key.Length == 32 ? key : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static string? ReadHash()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "link.hash");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<(int Code, string Output, string Error, byte[]? File)> RunAsync(
        string script,
        Dictionary<string, string> environment,
        byte[] blob,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "WorkshopLink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var scriptPath = Path.Combine(folder, "command.ps1");
        var blobPath = Path.Combine(folder, "incoming.bin");
        var outPath = Path.Combine(folder, "outgoing.bin");
        nint job = 0;
        try
        {
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), cancellationToken);
            if (blob.Length > 0)
                await File.WriteAllBytesAsync(blobPath, blob, cancellationToken);

            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = folder,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var key in AllowedKeys)
            {
                if (environment.TryGetValue(key, out var value))
                    start.Environment[key] = value;
            }

            if (blob.Length > 0)
                start.Environment["WLINK_FILE"] = blobPath;
            start.Environment["WLINK_OUT"] = outPath;

            job = SessionLaunch.StartJob(JobTitle(environment));
            using var process = new Process { StartInfo = start };
            if (!process.Start())
                return (1, "", "Windows PowerShell did not start.", null);

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            byte[]? file = File.Exists(outPath) ? await File.ReadAllBytesAsync(outPath, cancellationToken) : null;
            return (process.ExitCode, output, error, file);
        }
        finally
        {
            SessionLaunch.CloseJob(job);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static string? JobTitle(Dictionary<string, string> environment)
    {
        if (!environment.TryGetValue("WLINK_ACTION", out var action))
            return null;
        var name = environment.TryGetValue("WLINK_PKG", out var package) && !string.IsNullOrWhiteSpace(package)
            ? package
            : environment.GetValueOrDefault("WLINK_CMD") ?? "";
        var removing = action is "uninstall"
            || (action is "inbox" && name.StartsWith("remove:", StringComparison.OrdinalIgnoreCase))
            || (action is "feature" && name.StartsWith("off:", StringComparison.OrdinalIgnoreCase));
        if (action is not ("install" or "uninstall" or "winhance" or "inbox" or "feature"))
            return null;
        var label = string.IsNullOrWhiteSpace(name) ? "Workshop Link" : name;
        return (removing ? "Removing " : "Installing ") + label;
    }
}
