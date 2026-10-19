namespace WorkshopLink.Protocol;

public static class ConsoleProof
{
    public static string Text(string? action, string? host, string? detail) =>
        "wlink-act\n" + (action ?? "") + "\n" + (host ?? "") + "\n" + (detail ?? "");

    public static string Detail(IReadOnlyDictionary<string, string> environment)
    {
        if (environment.TryGetValue("WLINK_CMD", out var command) && !string.IsNullOrEmpty(command))
            return command;
        if (environment.TryGetValue("WLINK_PKG", out var package) && !string.IsNullOrEmpty(package))
            return package;
        if (environment.TryGetValue("WLINK_REMOTE", out var remote) && !string.IsNullOrEmpty(remote))
            return remote;
        return "";
    }
}
