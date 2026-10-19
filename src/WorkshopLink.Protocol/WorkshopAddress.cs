using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WorkshopLink.Protocol;

public readonly record struct LocalLan(string Address, int Prefix);

public static class WorkshopAddress
{
    public static string? LocalIpv4() => LocalNetwork()?.Address;

    public static LocalLan? LocalNetwork()
    {
        LocalLan? fallback = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                continue;
            var label = nic.Name + " " + nic.Description;
            if (label.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                || label.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                || label.Contains("WARP", StringComparison.OrdinalIgnoreCase)
                || label.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
                continue;
            var hasGateway = nic.GetIPProperties().GatewayAddresses.Any(gate =>
                gate.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(gate.Address));
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var text = address.Address.ToString();
                if (!LinkConstants.IsLocalAddress(text))
                    continue;
                var prefix = address.PrefixLength;
                if (prefix is < 8 or > 30)
                    prefix = 24;
                var lan = new LocalLan(text, prefix);
                if (hasGateway)
                    return lan;
                fallback ??= lan;
            }
        }

        return fallback;
    }

    public static string? LocalMac()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (!LinkConstants.IsWorkshopAddress(address.Address.ToString()))
                    continue;
                return LinkIdentity.NormalizeMac(nic.GetPhysicalAddress().ToString());
            }
        }

        return null;
    }

    public static async Task<string> SeenFromInternetAsync(CancellationToken cancellationToken)
    {
        var v4 = await TraceAsync("https://1.1.1.1/cdn-cgi/trace", cancellationToken);
        var v6 = await TraceAsync("https://[2606:4700:4700::1111]/cdn-cgi/trace", cancellationToken);
        return string.Join(' ', new[] { v4, v6 }.Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static string WarpText()
    {
        var found = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            var name = nic.Name + " " + nic.Description;
            if (name.IndexOf("WARP", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.IsIPv6LinkLocal || IPAddress.IsLoopback(address.Address))
                    continue;
                var text = address.Address.ToString();
                var zone = text.IndexOf('%');
                if (zone > 0)
                    text = text[..zone];
                if (text.Length > 0)
                    found.Add(text);
            }
        }

        return string.Join(' ', found.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<string> TraceAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var text = await client.GetStringAsync(url, cancellationToken);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("ip=", StringComparison.Ordinal) && trimmed.Length > 3)
                    return trimmed[3..].Trim();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
        }

        return "";
    }
}
