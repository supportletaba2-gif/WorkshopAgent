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
}
