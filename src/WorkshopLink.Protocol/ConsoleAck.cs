namespace WorkshopLink.Protocol;

public sealed class ConsoleAck
{
    public string Magic { get; set; } = LinkConstants.Magic;
    public string Kind { get; set; } = "ack";
    public string Hostname { get; set; } = "";
    public string Ip { get; set; } = "";

    public byte[] ToUtf8() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this, BeaconMessage.JsonOptions);

    public static ConsoleAck? TryParse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var message = System.Text.Json.JsonSerializer.Deserialize<ConsoleAck>(utf8, BeaconMessage.JsonOptions);
            if (message is null)
                return null;
            if (!string.Equals(message.Magic, LinkConstants.Magic, StringComparison.Ordinal))
                return null;
            if (!string.Equals(message.Kind, "ack", StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.IsNullOrWhiteSpace(message.Hostname))
                return null;
            message.Hostname = message.Hostname.Trim();
            message.Ip = (message.Ip ?? "").Trim();
            if (message.Ip.Length > 0 && !LinkConstants.IsWorkshopAddress(message.Ip))
                return null;
            return message;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
