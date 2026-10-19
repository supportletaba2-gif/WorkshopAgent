using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkshopLink.Protocol;

public sealed class BeaconMessage
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Magic { get; set; } = LinkConstants.Magic;
    public string Kind { get; set; } = "beacon";
    public string Hostname { get; set; } = "";
    public string Os { get; set; } = "";
    public string Ip { get; set; } = "";
    public string? Mac { get; set; }
    public string User { get; set; } = "";
    public string Account { get; set; } = "";
    public bool Link { get; set; }
    public string Arch { get; set; } = "";
    public string Id { get; set; } = "";
    public string Capabilities { get; set; } = "desktop,inventory";
    public string Version { get; set; } = LinkConstants.ProductVersion;
    public string Place { get; set; } = "lan";
    public string Group { get; set; } = "";
    public string Wan { get; set; } = "";
    public string Warp { get; set; } = "";
    public int Cpu { get; set; }
    public int Ram { get; set; }
    public int Disk { get; set; }
    public string Up { get; set; } = "";
    public string Protect { get; set; } = "";

    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static BeaconMessage? TryParse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var message = JsonSerializer.Deserialize<BeaconMessage>(utf8, JsonOptions);
            if (message is null)
                return null;
            if (!string.Equals(message.Magic, LinkConstants.Magic, StringComparison.Ordinal))
                return null;
            if (!string.IsNullOrWhiteSpace(message.Kind)
                && !string.Equals(message.Kind, "beacon", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!LinkConstants.IsWorkshopAddress(message.Ip))
                return null;
            if (string.IsNullOrWhiteSpace(message.Hostname))
                return null;
            message.Hostname = message.Hostname.Trim();
            message.Mac = LinkIdentity.NormalizeMac(message.Mac);
            message.Os = (message.Os ?? "").Trim();
            message.User = (message.User ?? "").Trim();
            message.Account = (message.Account ?? "").Trim();
            message.Arch = (message.Arch ?? "").Trim();
            message.Id = (message.Id ?? "").Trim();
            message.Capabilities = string.IsNullOrWhiteSpace(message.Capabilities)
                ? "desktop,inventory"
                : message.Capabilities.Trim();
            message.Version = string.IsNullOrWhiteSpace(message.Version)
                ? LinkConstants.ProductVersion
                : message.Version.Trim();
            message.Group = AgentPlace.IsGroupKey(message.Group) ? message.Group.Trim().ToUpperInvariant() : "";
            return message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
