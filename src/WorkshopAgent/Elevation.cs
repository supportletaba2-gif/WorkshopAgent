using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace WorkshopAgent;

internal static class Elevation
{
    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RelaunchElevated(string[] args)
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("Workshop Link could not find its own program file.", "Workshop Link");
            return;
        }

        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        try
        {
            Process.Start(start);
        }
        catch (Win32Exception)
        {
            MessageBox.Show(
                "Workshop Link needs administrator approval to prepare this PC.",
                "Workshop Link");
        }
    }
}
