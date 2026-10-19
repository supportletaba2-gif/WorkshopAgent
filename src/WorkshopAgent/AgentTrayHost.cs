using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;

namespace WorkshopAgent;

internal sealed class AgentTrayHost : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartTray();
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private static void StartTray()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out var token))
            return;
        try
        {
            if (!DuplicateTokenEx(token, 0x10000000, IntPtr.Zero, 2, 1, out var primary))
                return;
            try
            {
                var start = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
                var command = "\"" + exe + "\" --tray";
                if (!CreateProcessAsUser(primary, null, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref start, out var info))
                    return;
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
            finally
            {
                CloseHandle(primary);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved3;
        public nint StdIn;
        public nint StdOut;
        public nint StdErr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint primary);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(nint token, string? application, string command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo start, out ProcessInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
