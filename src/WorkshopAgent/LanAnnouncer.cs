using System.Net;
using System.Net.Sockets;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class LanAnnouncer : IDisposable
{
    private readonly HashSet<string> _consoles = new(StringComparer.OrdinalIgnoreCase);
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private UdpClient? _udp;
    private ConsoleAck? _latest;
    private DateTime _latestUtc = DateTime.MinValue;
    private DateTime _lastSweepUtc = DateTime.MinValue;
    private string? _boundIp;
    private bool _forceSweep;
    private string? _noted;

    public string? LastProblem { get; private set; }

    private static string ReadAccount()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "account.txt");
            if (!File.Exists(path))
                return "";
            var name = File.ReadAllText(path).Trim();
            return name.IndexOfAny(['\\', '/', ' ', '\r', '\n']) >= 0 ? "" : name;
        }
        catch (IOException)
        {
            return "";
        }
    }

    public ConsoleAck? FreshAck()
    {
        if (_latest is null)
            return null;
        return DateTime.UtcNow - _latestUtc <= LinkConstants.OnlineWindow ? _latest : null;
    }

    public void Announce()
    {
        var lan = WorkshopAddress.LocalNetwork();
        if (lan is null)
        {
            Note("This PC has no local network address yet.");
            return;
        }

        var ip = lan.Value.Address;
        if (!string.Equals(_boundIp, ip, StringComparison.Ordinal))
        {
            ReplaceSocket();
            _boundIp = ip;
            _forceSweep = true;
        }

        EnsureSocket();
        if (DateTime.UtcNow - _latestUtc > LinkConstants.ForgetWindow)
            _consoles.Clear();

        var message = new BeaconMessage
        {
            Hostname = Environment.MachineName,
            Os = MachineFacts.OperatingSystem(),
            Ip = ip,
            Mac = MachineFacts.Mac(),
            User = MachineFacts.SignedInUser(),
            Account = ReadAccount(),
            Link = true,
            Id = PeerIdentity.Fingerprint,
            Capabilities = "desktop,inventory",
            Arch = MachineFacts.Architecture(),
            Version = LinkConstants.ProductVersion,
            Place = AgentPlace.IsOffsite() ? "offsite" : "lan",
            Group = AgentPlace.GroupKey()
        };
        var health = MachineFacts.ReadHealth();
        message.Cpu = health.Cpu;
        message.Ram = health.Ram;
        message.Disk = health.Disk;
        message.Up = health.Up;
        message.Protect = health.Protect;
        var bytes = message.ToUtf8();
        var broadcast = SubnetBroadcast(ip, lan.Value.Prefix);
        var sent = Send(bytes, broadcast) + Send(bytes, "255.255.255.255") + Send(bytes, "224.0.0.251");

        // A broadcast from another PC is dropped before it reaches a normal socket on Windows.
        // Address every host on this subnet directly until a console answers, and again after this PC's address changes.
        var sweepDue = _forceSweep || DateTime.UtcNow - _lastSweepUtc >= TimeSpan.FromSeconds(8);
        if (_forceSweep || _consoles.Count == 0)
        {
            if (sweepDue)
            {
                _forceSweep = false;
                _lastSweepUtc = DateTime.UtcNow;
                foreach (var target in HostsOnSubnet(ip, lan.Value.Prefix))
                {
                    if (string.Equals(target, ip, StringComparison.Ordinal))
                        continue;
                    sent += Send(bytes, target);
                }
            }
        }
        else
        {
            foreach (var console in _consoles)
                sent += Send(bytes, console);
        }

        Drain();
        Note(sent == 0 ? "The announcement did not leave this PC." : null);
    }

    public void Dispose()
    {
        _udp?.Dispose();
        _udp = null;
    }

    private void EnsureSocket()
    {
        if (_udp is not null)
            return;
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.EnableBroadcast = true;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        try
        {
            // An unreachable host answers ICMP, and Windows then fails the next send on this socket.
            client.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }

        _udp = client;
    }

    private void ReplaceSocket()
    {
        _udp?.Dispose();
        _udp = null;
    }

    private int Send(byte[] bytes, string target)
    {
        if (SendOnce(bytes, target))
            return 1;
        return SendOnce(bytes, target) ? 1 : 0;
    }

    private bool SendOnce(byte[] bytes, string target)
    {
        try
        {
            EnsureSocket();
            _udp!.Send(bytes, bytes.Length, target, LinkConstants.BeaconPort);
            return true;
        }
        catch (SocketException)
        {
            ReplaceSocket();
            return false;
        }
        catch (ObjectDisposedException)
        {
            ReplaceSocket();
            return false;
        }
    }

    private static string SubnetBroadcast(string ip, int prefix)
    {
        if (!TryPack(ip, out var packed) || prefix is < 24 or > 30)
            return "255.255.255.255";
        var bits = 32 - prefix;
        var mask = uint.MaxValue << bits;
        var broadcast = (packed & mask) | ~mask;
        return Format(broadcast);
    }

    private static IEnumerable<string> HostsOnSubnet(string ip, int prefix)
    {
        if (!TryPack(ip, out var packed) || prefix is < 24 or > 30)
            yield break;
        var bits = 32 - prefix;
        var count = 1u << bits;
        var mask = uint.MaxValue << bits;
        var network = packed & mask;
        for (var host = 1u; host < count - 1; host++)
            yield return Format(network + host);
    }

    private static bool TryPack(string ip, out uint packed)
    {
        packed = 0;
        if (!IPAddress.TryParse(ip, out var address))
            return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;
        packed = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    private static string Format(uint packed) =>
        $"{(packed >> 24) & 255}.{(packed >> 16) & 255}.{(packed >> 8) & 255}.{packed & 255}";

    private void Drain()
    {
        if (_udp is null)
            return;
        while (_udp.Available > 0)
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] data;
            try
            {
                data = _udp.Receive(ref remote);
            }
            catch (SocketException)
            {
                ReplaceSocket();
                return;
            }

            var from = remote.Address.ToString();
            if (!IPAddress.IsLoopback(remote.Address) && !LinkConstants.IsWorkshopAddress(from))
                continue;
            var ack = ConsoleAck.TryParse(data);
            if (ack is null)
                continue;
            if (string.IsNullOrWhiteSpace(ack.Ip) && LinkConstants.IsWorkshopAddress(from))
                ack.Ip = from;
            if (LinkConstants.IsWorkshopAddress(from))
                _consoles.Add(from);
            _latest = ack;
            _latestUtc = DateTime.UtcNow;
        }
    }

    private void Note(string? problem)
    {
        LastProblem = problem;
        var line = problem ?? (_latest is null ? "announcing" : "console " + _latest.Hostname);
        if (string.Equals(line, _noted, StringComparison.Ordinal))
            return;
        _noted = line;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "announce.log"), DateTime.Now.ToString("HH:mm:ss ") + line);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
