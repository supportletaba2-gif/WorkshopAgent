namespace WorkshopLink.Protocol;

public static class LinkIdentity
{
    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return null;

        Span<char> hex = stackalloc char[12];
        var count = 0;
        foreach (var character in mac)
        {
            if (!Uri.IsHexDigit(character))
                continue;
            if (count == 12)
                return null;
            hex[count++] = char.ToUpperInvariant(character);
        }

        if (count != 12)
            return null;

        return string.Create(17, hex.ToString(), static (span, value) =>
        {
            for (var i = 0; i < 6; i++)
            {
                if (i > 0)
                    span[(i * 3) - 1] = ':';
                span[i * 3] = value[i * 2];
                span[(i * 3) + 1] = value[(i * 2) + 1];
            }
        });
    }

    public static string Key(string? mac, string? hostname)
    {
        var normalized = NormalizeMac(mac);
        if (normalized is not null)
            return "mac:" + normalized;
        return "host:" + (hostname ?? "").Trim().ToUpperInvariant();
    }
}
