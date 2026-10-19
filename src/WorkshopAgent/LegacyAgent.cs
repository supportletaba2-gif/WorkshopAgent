using System.Diagnostics;

namespace WorkshopAgent;

internal static class LegacyAgent
{
    private const string OldDirectory = @"C:\Program Files\WorkshopLink";
    public static bool Present()
    {
        if (File.Exists(Path.Combine(OldDirectory, "WorkshopAgent.exe")))
            return true;
        return Run("sc.exe", "query WorkshopLink").Contains("STATE", StringComparison.OrdinalIgnoreCase);
    }

    public static string? Version()
    {
        var path = Path.Combine(OldDirectory, "WorkshopAgent.exe");
        if (!File.Exists(path))
            return Present() ? "unknown" : null;
        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
    }

    public static void Remove()
    {
        StopAndDelete("WorkshopLink");
        var self = Environment.ProcessPath ?? "";
        try
        {
            foreach (var proc in Process.GetProcessesByName("WorkshopAgent"))
            {
                try
                {
                    if (proc.Id == Environment.ProcessId)
                        continue;
                    var path = proc.MainModule?.FileName ?? "";
                    if (path.Contains(@"\Program Files\WorkshopLink\", StringComparison.OrdinalIgnoreCase))
                        proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        StopAndDelete("WorkshopLink");
        foreach (var rule in new[] { "WorkshopLink-WinRM", "WorkshopLink-Link", "WorkshopLink-RDP", "WorkshopLink-Announce", "WorkshopLink-Announce-Out", "WorkshopLink-Announce-Setup" })
            Run("netsh.exe", "advfirewall firewall delete rule name=\"" + rule + "\"");
        foreach (var desktop in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
        {
            var link = Path.Combine(desktop, "Workshop Link Agent.lnk");
            try { if (File.Exists(link)) File.Delete(link); }
            catch (IOException) { }
        }
        if (!self.StartsWith(OldDirectory, StringComparison.OrdinalIgnoreCase) && Directory.Exists(OldDirectory))
        {
            try { Directory.Delete(OldDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void StopAndDelete(string name)
    {
        Run("sc.exe", "stop " + name);
        Thread.Sleep(1200);
        var pid = QueryPid(name);
        if (pid > 0)
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            Thread.Sleep(400);
        }
        Run("sc.exe", "delete " + name);
    }

    private static int QueryPid(string name)
    {
        var output = Run("sc.exe", "queryex " + name);
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("PID", StringComparison.OrdinalIgnoreCase))
                continue;
            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var pid))
                return pid;
        }
        return 0;
    }

    private static string Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
                return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            return output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }
}
