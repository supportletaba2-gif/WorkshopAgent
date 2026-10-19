using System.Runtime.InteropServices;
using Microsoft.Win32;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal static class MachineFacts
{
    public static string? WorkshopIpv4() => WorkshopAddress.LocalIpv4();

    public static string? Mac() => WorkshopAddress.LocalMac();

    public static string OperatingSystem()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        var display = key?.GetValue("DisplayVersion") as string;
        var buildText = key?.GetValue("CurrentBuild") as string ?? "0";
        _ = int.TryParse(buildText, out var build);
        if (build >= 22000)
            product = product.Replace("Windows 10", "Windows 11");
        return string.IsNullOrWhiteSpace(display) ? product : product + " " + display;
    }

    public static string Architecture() => Environment.Is64BitOperatingSystem ? "x64" : "x86";

    public readonly record struct Health(int Cpu, int Ram, int Disk, string Up, string Protect);

    private static DateTime _healthTaken = DateTime.MinValue;
    private static Health _health = new(0, 0, 0, "", "");
    private static long _idle;
    private static long _kernel;
    private static long _user;

    public static Health ReadHealth()
    {
        if (_healthTaken != DateTime.MinValue && DateTime.UtcNow - _healthTaken < TimeSpan.FromSeconds(45))
            return _health;
        var primed = _kernel > 0;
        if (!primed)
            SampleCpu();
        if (!primed && _kernel > 0)
            Thread.Sleep(200);
        var cpu = SampleCpu();
        var ram = SampleRam();
        var disk = SampleDisk();
        var up = SampleUp();
        var protect = SampleProtection();
        _health = new Health(cpu, ram, disk, up, protect);
        _healthTaken = DateTime.UtcNow;
        return _health;
    }

    private static int SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return _health.Cpu;
        if (_kernel > 0 && kernel >= _kernel && idle >= _idle)
        {
            var busy = (kernel - _kernel) - (idle - _idle);
            var span = (kernel + user) - (_kernel + _user);
            _idle = idle;
            _kernel = kernel;
            _user = user;
            if (span <= 0)
                return _health.Cpu;
            return (int)Math.Clamp(busy * 100 / span, 0, 100);
        }
        _idle = idle;
        _kernel = kernel;
        _user = user;
        return _health.Cpu;
    }

    private static int SampleRam()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (int)Math.Clamp(status.Load, 0, 100) : 0;
    }

    private static int SampleDisk()
    {
        if (!GetDiskFreeSpaceEx("C:\\", out _, out var total, out var free) || total == 0)
            return 0;
        var used = total - free;
        return (int)Math.Clamp((double)used * 100 / total, 0, 100);
    }

    private static string SampleProtection()
    {
        try
        {
            using var defender = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender");
            if (defender is null)
                return "";
            if (defender.GetValue("DisableAntiSpyware") is int spyware && spyware != 0)
                return "Off";
            using var realtime = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection");
            if (realtime?.GetValue("DisableRealtimeMonitoring") is int disabled && disabled != 0)
                return "Off";
            return "On";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
        catch (System.Security.SecurityException)
        {
            return "";
        }
    }

    private static string SampleUp()
    {
        var span = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return (int)span.TotalDays + "d " + span.Hours + "h";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPage;
        public ulong AvailPage;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtended;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong free, out ulong total, out ulong totalFree);

    public static string SignedInUser()
    {
        try
        {
            var sessionId = WtsGetActiveConsoleSessionId();
            if (sessionId == unchecked((int)0xFFFFFFFF))
                return "";

            var user = QuerySessionString(sessionId, wtsUserName);
            if (string.IsNullOrWhiteSpace(user))
                return "";
            var domain = QuerySessionString(sessionId, wtsDomainName);
            return string.IsNullOrWhiteSpace(domain) ? user : domain + "\\" + user;
        }
        catch (DllNotFoundException)
        {
            return "";
        }
        catch (EntryPointNotFoundException)
        {
            return "";
        }
    }

    private static string QuerySessionString(int sessionId, int infoClass)
    {
        if (!WtsQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            return "";
        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            WtsFreeMemory(buffer);
        }
    }

    private const int wtsUserName = 5;
    private const int wtsDomainName = 7;

    [DllImport("kernel32.dll", EntryPoint = "WTSGetActiveConsoleSessionId")]
    private static extern int WtsGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WtsQuerySessionInformation(
        IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    private static extern void WtsFreeMemory(IntPtr memory);
}
