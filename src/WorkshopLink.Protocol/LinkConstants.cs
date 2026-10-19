namespace WorkshopLink.Protocol;

public static class LinkConstants
{
    public const string Magic = "WLINK1";
    public const int BeaconPort = 47631;
    public const string LocalRange10 = "10.0.0.0/8";
    public const string LocalRange172 = "172.16.0.0/12";
    public const string LocalRange192 = "192.168.0.0/16";
    public const string LocalRanges = "10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16";
    public const string AccountName = "WorkshopLink";
    public const string ServiceName = "WorkshopCommandCenterAgent";
    public const string ServiceDisplayName = "Workshop Link Agent";
    public const string ConsoleServiceName = "WorkshopCommandCenter";
    public const string ConsoleServiceDisplayName = "Workshop Command Center";
    public const string ConsoleInstallDirectory = @"C:\Program Files\Workshop Command Center";
    public const string ProductVersion = "1.0.134";

    public static bool AgentIsCurrent(string? version) =>
        Version.TryParse(version, out var found) && found >= new Version(1, 0, 90);
    public const string ScreenUrl = "wss://workshop-link-screen.wde-host.workers.dev";
    public const string SignalUrl = "wss://workshop-link-signal.wde-host.workers.dev";
    public const string FileUrl = "https://workshop-link-file.wde-host.workers.dev";
    public const string OversightUrl = "wss://workshop-link-oversight.wde-host.workers.dev";
    public const int FilePartBytes = 5 * 1024 * 1024;
    public const string ServiceType = "_ccagent._udp.local.";
    public const int EnrollPort = 47632;
    public const int LinkPort = 47633;
    public const string LinkRuleName = "WorkshopLink-Link";
    public const string TunnelAllowedIps = "192.168.0.0/24, 192.168.216.0/24";
    public const string BootstrapTunnelName = "wlink-bootstrap";
    public const string OwnTunnelPrefix = "wlink-";
    public const string WinRmRuleName = "WorkshopLink-WinRM";
    public const string BeaconRuleName = "WorkshopLink-Beacon";
    public const string InstallDirectory = @"C:\Program Files\Workshop Command Center Agent";

    public static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan ForgetWindow = TimeSpan.FromSeconds(60);

    public static bool IsLocalAddress(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return false;
        if (!System.Net.IPAddress.TryParse(ip, out var address))
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || bytes.Length != 4)
            return false;
        if (bytes[0] == 10)
            return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return true;
        return bytes[0] == 192 && bytes[1] == 168;
    }

    public static bool IsWorkshopAddress(string? ip) => IsLocalAddress(ip);
}
