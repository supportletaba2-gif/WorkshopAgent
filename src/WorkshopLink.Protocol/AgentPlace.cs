namespace WorkshopLink.Protocol;

public static class AgentPlace
{
    public static string PathFor() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "placement.txt");

    public static bool IsOffsite()
    {
        var path = PathFor();
        return File.Exists(path)
            && string.Equals(File.ReadAllText(path).Trim(), "offsite", StringComparison.OrdinalIgnoreCase);
    }

    public static string GroupPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink", "group.txt");

    public static string GroupKey()
    {
        var path = GroupPath();
        if (!File.Exists(path))
            return "";
        var text = File.ReadAllText(path).Trim().ToUpperInvariant();
        return IsGroupKey(text) ? text : "";
    }

    public static bool IsGroupKey(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), "^[A-Za-z0-9]{4}-[A-Za-z0-9]{4}$");
}
