using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class BeaconWorker(ILogger<BeaconWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var address = WorkshopAddress.LocalIpv4();
        var offsite = AgentPlace.IsOffsite() || !LinkConstants.IsWorkshopAddress(address);
        logger.LogInformation(offsite
            ? "Workshop Link Agent is joining the offsite directory. The announcement accepts no commands."
            : "Workshop Link Agent is announcing this PC on the workshop LAN. The announcement accepts no commands.");

        if (!offsite)
        {
            try
            {
                logger.LogInformation(AgentTunnel.Ensure());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Workshop Link did not bring up the workshop tunnel.");
            }
        }

        using var link = new LanAnnouncer();
        var wan = "";
        var warp = "";
        if (offsite && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lookup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lookup.CancelAfter(TimeSpan.FromSeconds(8));
                wan = await WorkshopAddress.SeenFromInternetAsync(lookup.Token);
                warp = WorkshopAddress.WarpText();
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
        }
        var directory = DirectorySettings.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "directory.txt"));
        if (directory.Enabled)
        {
            var client = new DirectoryClient(directory, "agent");
            client.StatusChanged += NoteDirectory;
            _ = new RelayHost(client, directory);
            _ = new PipeHost(client);
            var health = MachineFacts.ReadHealth();
            _ = client.RunAsync(new BeaconMessage
            {
                Hostname = Environment.MachineName,
                Os = MachineFacts.OperatingSystem(),
                Arch = MachineFacts.Architecture(),
                User = MachineFacts.SignedInUser(),
                Ip = address ?? "",
                Mac = MachineFacts.Mac(),
                Wan = wan,
                Warp = warp,
                Id = PeerIdentity.Fingerprint,
                Version = LinkConstants.ProductVersion,
                Link = true,
                Place = offsite ? "offsite" : "lan",
                Group = AgentPlace.GroupKey(),
                Cpu = health.Cpu,
                Ram = health.Ram,
                Disk = health.Disk,
                Up = health.Up,
                Protect = health.Protect
            }, stoppingToken);
            var healthSent = DateTime.MinValue;
            _ = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                        var health = MachineFacts.ReadHealth();
                        if (DateTime.UtcNow - healthSent >= TimeSpan.FromSeconds(50))
                        {
                            client.PostHealth(health.Cpu, health.Ram, health.Disk, health.Up, health.Protect);
                            healthSent = DateTime.UtcNow;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, stoppingToken);
        }
        var lastWarning = DateTime.MinValue;
        var provisionAfter = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!offsite)
                    link.Announce();
                var ack = link.FreshAck();
                if (!offsite && ack is not null && DateTime.UtcNow >= provisionAfter)
                {
                    try
                    {
                        logger.LogInformation(AgentTunnel.TryProvision(ack.Ip));
                        provisionAfter = DateTime.MaxValue;
                    }
                    catch (Exception ex)
                    {
                        provisionAfter = DateTime.UtcNow.AddMinutes(2);
                        logger.LogWarning(ex, "Workshop Link did not save a tunnel with the command center.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (DateTime.UtcNow - lastWarning > TimeSpan.FromMinutes(1))
                {
                    lastWarning = DateTime.UtcNow;
                    logger.LogWarning(ex, "Workshop Link could not announce this PC.");
                }
            }

            try
            {
                await Task.Delay(LinkConstants.BeaconInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void NoteDirectory(string line)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "announce.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + "directory " + line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}
