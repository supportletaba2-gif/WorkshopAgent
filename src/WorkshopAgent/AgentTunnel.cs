using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal static class AgentTunnel
{
    private const string MsiName = "wireguard-amd64-1.1.1.msi";
    private const string MsiSha256 = "7BFED60AD61B785C914B38B61555A975488E1D3EC472DBFB2FCDF498FCA75242";
    private static readonly string MsiUrl = "https://download.wireguard.com/windows-client/" + MsiName;

    public static string Ensure()
    {
        if (OnWorkshopLan())
        {
            StopTunnel(LinkConstants.BootstrapTunnelName);
            StopOwnTunnels();
            return "This PC is on the workshop LAN, so its WireGuard tunnel stays off.";
        }

        var own = OwnConfigPath();
        if (File.Exists(own))
        {
            InstallWireGuard();
            StartTunnel(own);
            return "This PC is using its own workshop tunnel, " + Path.GetFileNameWithoutExtension(own) + ".";
        }

        return "This PC has no workshop tunnel yet. It enrolls with the command center that answers on the LAN.";
    }

    public static string TryProvision(string consoleIp)
    {
        if (!LinkConstants.IsWorkshopAddress(consoleIp))
            throw new IOException("The command center address is not on the workshop LAN.");
        if (File.Exists(OwnConfigPath()))
            return "This PC already has a workshop tunnel.";

        InstallWireGuard();
        var (privateKey, publicKey) = NewKeyPair();
        var assigned = Enroll(consoleIp, publicKey);
        WriteOwnConfig(assigned, privateKey);
        return "Saved a workshop tunnel as " + assigned.PeerName + ". It stays off while this PC is on the LAN.";
    }

    private static bool OnWorkshopLan()
    {
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (LinkConstants.IsWorkshopAddress(address.ToString()))
                    return true;
            }
        }
        catch (SocketException)
        {
        }

        return false;
    }

    private static void InstallWireGuard()
    {
        if (File.Exists(WireGuardExe()) && File.Exists(WgExe()))
            return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink");
        Directory.CreateDirectory(dir);
        var msi = Path.Combine(dir, MsiName);
        if (!File.Exists(msi) || !HashMatches(msi))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var bytes = client.GetByteArrayAsync(MsiUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(msi, bytes);
        }

        if (!HashMatches(msi))
            throw new IOException("The WireGuard installer did not match the expected hash.");

        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        Run(msiexec, "/i \"" + msi + "\" /qn DO_NOT_LAUNCH=1", TimeSpan.FromMinutes(3));
        if (!File.Exists(WireGuardExe()))
            throw new IOException("WireGuard did not install.");
    }

    private static bool HashMatches(string path)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        return hash.Equals(MsiSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static (string PrivateKey, string PublicKey) NewKeyPair()
    {
        var secret = Run(WgExe(), "genkey", TimeSpan.FromSeconds(15)).Trim();
        if (!TunnelEnrollment.IsPrivateKey(secret))
            throw new IOException("WireGuard did not return a key.");
        var start = new ProcessStartInfo
        {
            FileName = WgExe(),
            Arguments = "pubkey",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not start wg.");
        process.StandardInput.Write(secret);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(15000);
        if (!TunnelEnrollment.IsPrivateKey(output))
            throw new IOException("WireGuard did not return a public key.");
        return (secret, output);
    }

    private static AssignedTunnel Enroll(string consoleIp, string publicKey)
    {
        using var client = new TcpClient();
        var connected = client.ConnectAsync(consoleIp, LinkConstants.EnrollPort).Wait(TimeSpan.FromSeconds(20));
        if (!connected || !client.Connected)
            throw new IOException("Could not reach the command center at " + consoleIp + ".");
        client.ReceiveTimeout = 30000;
        client.SendTimeout = 15000;
        using var stream = client.GetStream();
        var request = JsonSerializer.Serialize(new
        {
            hostname = Environment.MachineName,
            publicKey
        });
        var bytes = Encoding.UTF8.GetBytes(TunnelEnrollment.Magic + "\n" + request + "\n");
        stream.Write(bytes, 0, bytes.Length);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("The workshop laptop did not answer the tunnel request.");
        var assigned = JsonSerializer.Deserialize<AssignedTunnel>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (assigned is null || !string.IsNullOrWhiteSpace(assigned.Error))
            throw new IOException(assigned?.Error ?? "The workshop laptop rejected the tunnel request.");
        if (string.IsNullOrWhiteSpace(assigned.Address) || string.IsNullOrWhiteSpace(assigned.PeerConfig) || !assigned.PeerConfig.Contains("[Peer]", StringComparison.Ordinal))
            throw new IOException("The workshop laptop returned an incomplete tunnel.");
        if (assigned.PeerConfig.Contains("0.0.0.0/0", StringComparison.Ordinal) || assigned.PeerConfig.Contains("::/0", StringComparison.Ordinal))
            throw new IOException("The workshop laptop offered a full tunnel. This agent only accepts the workshop LAN.");
        return assigned;
    }

    private static string WriteOwnConfig(AssignedTunnel assigned, string privateKey)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Interface]");
        builder.AppendLine("PrivateKey = " + privateKey.Trim());
        builder.AppendLine("Address = " + assigned.Address.Trim());
        builder.AppendLine();
        builder.AppendLine(assigned.PeerConfig.Trim());
        var path = Path.Combine(TunnelDir(), assigned.PeerName + ".conf");
        WriteProtected(path, builder.ToString());
        return path;
    }

    private static void StartTunnel(string confPath)
    {
        var name = Path.GetFileNameWithoutExtension(confPath);
        StopTunnel(name);
        Run(WireGuardExe(), "/installtunnelservice \"" + confPath + "\"", TimeSpan.FromSeconds(40));
    }

    private static void StopTunnel(string name)
    {
        if (!File.Exists(WireGuardExe()) || string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            Run(WireGuardExe(), "/uninstalltunnelservice " + name, TimeSpan.FromSeconds(30));
        }
        catch (IOException)
        {
        }
    }

    private static void StopOwnTunnels()
    {
        var dir = TunnelDir();
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.GetFiles(dir, "*.conf"))
            StopTunnel(Path.GetFileNameWithoutExtension(file));
    }

    private static string OwnConfigPath()
    {
        var dir = TunnelDir();
        if (!Directory.Exists(dir))
            return Path.Combine(dir, "missing.conf");
        return Directory.GetFiles(dir, "*.conf")
            .FirstOrDefault(path => !string.Equals(Path.GetFileNameWithoutExtension(path), LinkConstants.BootstrapTunnelName, StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(dir, "missing.conf");
    }

    private static string TunnelDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "tunnels");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteProtected(string path, string text)
    {
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static string WireGuardExe() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe");

    private static string WgExe() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wg.exe");

    private static string Run(string file, string arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + Path.GetFileName(file) + ".");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            throw new IOException(Path.GetFileName(file) + " timed out.");
        }

        var text = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(error) ? Path.GetFileName(file) + " failed." : error.Trim());
        return string.IsNullOrWhiteSpace(text) ? error : text;
    }

    private sealed class AssignedTunnel
    {
        public string? Error { get; set; }
        public string PeerName { get; set; } = "";
        public string Address { get; set; } = "";
        public string AllowedIps { get; set; } = "";
        public string PeerConfig { get; set; } = "";
    }
}
