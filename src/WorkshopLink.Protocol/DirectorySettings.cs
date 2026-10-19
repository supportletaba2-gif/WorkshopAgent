namespace WorkshopLink.Protocol;

public static class NetPreference
{
    private static int _once;

    public static void PreferIpv4()
    {
        if (Interlocked.Exchange(ref _once, 1) == 1)
            return;
        AppContext.SetSwitch("System.Net.DisableIPv6", true);
    }
}

public sealed class DirectorySettings
{
    public string Url { get; init; } = "";
    public string Site { get; init; } = "";

    public bool Enabled =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && (uri.Scheme == "wss" || uri.Scheme == "https")
        && Site.Length is >= 4 and <= 32
        && Site.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public static DirectorySettings BuiltIn { get; } = new()
    {
        Url = "wss://workshop-link-directory.wde-host.workers.dev",
        Site = "workshop-k7m2p9qx"
    };

    public static DirectorySettings Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return BuiltIn;
            var lines = File.ReadAllLines(path);
            var url = lines.Length > 0 ? lines[0].Trim() : "";
            var site = lines.Length > 1 ? lines[1].Trim().ToLowerInvariant() : "";
            return new DirectorySettings { Url = url, Site = site };
        }
        catch (IOException)
        {
            return new DirectorySettings();
        }
    }
}
