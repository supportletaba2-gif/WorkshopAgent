using System.Text.RegularExpressions;

namespace WorkshopLink.Protocol;

public sealed record DhcpLease(string Ip, string? Mac, string Hostname, string ClassId);

public static class DhcpPrint
{
    private static readonly Regex Field = new(@"(?:^|\s)([A-Za-z0-9-]+)=", RegexOptions.Compiled);

    public static List<DhcpLease> Parse(string text)
    {
        var leases = new List<DhcpLease>();
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var map = ReadFields(raw);
            if (!map.TryGetValue("status", out var status)
                || !status.Equals("bound", StringComparison.OrdinalIgnoreCase))
                continue;

            var ip = First(map, "active-address", "address");
            if (!LinkConstants.IsWorkshopAddress(ip))
                continue;

            var hostname = map.TryGetValue("host-name", out var host) ? host.Trim() : "";
            var classId = map.TryGetValue("class-id", out var klass) ? klass.Trim() : "";
            if (hostname.Length == 0 && !classId.StartsWith("MSFT", StringComparison.OrdinalIgnoreCase))
                continue;

            leases.Add(new DhcpLease(
                ip!,
                LinkIdentity.NormalizeMac(First(map, "active-mac-address", "mac-address")),
                hostname,
                classId));
        }

        return leases;
    }

    private static Dictionary<string, string> ReadFields(string line)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = Field.Matches(line);
        for (var i = 0; i < matches.Count; i++)
        {
            var name = matches[i].Groups[1].Value;
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
            map[name] = line[start..end].Trim();
        }

        return map;
    }

    private static string? First(Dictionary<string, string> map, params string[] names)
    {
        foreach (var name in names)
        {
            if (map.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}
