namespace WorkshopLink.Protocol;

public static class TunnelEnrollment
{
    public const string Magic = "WLINK-ENROLL1";

    public static string SanitizePeerName(string? hostname)
    {
        var raw = string.IsNullOrWhiteSpace(hostname) ? "pc" : hostname.Trim();
        var chars = raw.Select(ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray();
        var name = new string(chars).Trim('-');
        while (name.Contains("--", StringComparison.Ordinal))
            name = name.Replace("--", "-", StringComparison.Ordinal);
        if (name.Length == 0)
            name = "pc";
        if (name.Length > 24)
            name = name[..24].Trim('-');
        return LinkConstants.OwnTunnelPrefix + name;
    }

    public static bool IsPrivateKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        if (trimmed.Length is < 40 or > 48)
            return false;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=')
                continue;
            return false;
        }

        return true;
    }
}
