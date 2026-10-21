using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal static class ConsoleGate
{
    private static int _fails;
    private static long _lockedUntil;
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink");
    private static readonly ConcurrentDictionary<string, (string Console, long Expires)> Tickets = new(StringComparer.Ordinal);

    public static bool Quiet(string action) => action is "info" or "view" or "programs";

    public static bool Known(string action) => Quiet(action) || action is "copyto" or "copyfrom" or "desktop"
        or "winhance" or "tune" or "inbox" or "feature" or "install" or "uninstall" or "updates" or "agentupdate"
        or "restart" or "shutdown" or "signout";

    public static string? Admit(
        Dictionary<string, string> environment,
        string password,
        out string console,
        out string? ticket)
    {
        console = "";
        ticket = null;
        var action = environment.GetValueOrDefault("WLINK_ACTION") ?? "";
        var host = environment.GetValueOrDefault("WLINK_HOST") ?? "";
        var detail = ConsoleProof.Detail(environment);
        if (!Known(action))
        {
            Audit("", action, "refused unknown action");
            return "That action is not available.";
        }

        console = (environment.GetValueOrDefault("WLINK_CONSOLE") ?? "").Trim();
        var proof = environment.GetValueOrDefault("WLINK_PROOF") ?? "";
        if (!PeerIdentity.Verify(console, proof, ConsoleProof.Text(action, host, detail)))
        {
            Audit(console, action, "refused unsigned command center");
            return "This command center is not signed.";
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < _lockedUntil)
        {
            Audit(console, action, "locked");
            return "This PC is locked after wrong passwords. Wait 15 minutes.";
        }

        if (!Paired(console))
        {
            var fingerprint = console.Length >= 16 ? console[..16] : console;
            var allow = Ask("Command center " + fingerprint + " wants to manage this PC. Allow it? Only allow a command center you set up.");
            if (allow != 6 || !Pair(console))
            {
                Audit(console, action, "refused new command center");
                if (allow == 6)
                    return "This PC already trusts ten command centers.";
                return PromptFailure(allow, "This PC did not allow that command center.");
            }

            Audit(console, action, "paired command center");
        }

        var passwordOk = LinkSecret.Verify(LinkWorker.ReadHash(), password);
        var ticketOk = Quiet(action) && Accepts(environment.GetValueOrDefault("WLINK_TICKET"), console);
        if (!passwordOk && !ticketOk)
        {
            _fails++;
            if (_fails >= 5)
            {
                _fails = 0;
                _lockedUntil = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
            }

            Audit(console, action, "refused password");
            return "Password wrong.";
        }

        if (passwordOk)
            _fails = 0;

        if (!Quiet(action) && action is not "agentupdate")
        {
            if (!passwordOk)
            {
                Audit(console, action, "refused ticket for a change");
                return "Type the password on the command center. A screen ticket cannot do that.";
            }

            var allow = Ask(PromptFor(action, detail));
            if (allow != 6)
            {
                Audit(console, action, "person denied " + action);
                return PromptFailure(allow, "The person at this PC denied that.");
            }
        }

        if (action is "copyto" or "copyfrom" && !SafePath(environment.GetValueOrDefault("WLINK_REMOTE")))
        {
            Audit(console, action, "refused path");
            return "That file path is not allowed.";
        }

        if (passwordOk && Quiet(action))
            ticket = Issue(console);
        Audit(console, action, "allowed");
        return null;
    }

    public static void Audit(string console, string action, string result)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, "audit.log");
            var created = !File.Exists(path);
            var who = string.IsNullOrWhiteSpace(console) ? "-" : (console.Length > 16 ? console[..16] : console);
            var line = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "  " + who + "  " + action + "  " + result + "\r\n";
            File.AppendAllText(path, line, Encoding.UTF8);
            if (created)
                Protect(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string PromptFor(string action, string detail)
    {
        var what = action switch
        {
            "copyto" => "copy a file onto this PC",
            "copyfrom" => "copy a file off this PC",
            "install" => "install software",
            "uninstall" => "remove software",
            "command" => "run a command",
            "restart" => "restart this PC",
            "shutdown" => "turn this PC off",
            "signout" => "sign out the person at this PC",
            "updates" => "install Windows updates",
            "agentupdate" => "replace the Workshop Link agent with the copy published on GitHub",
            "desktop" => "open Remote Desktop",
            _ => action
        };
        var extra = string.IsNullOrWhiteSpace(detail) ? "" : "\n\n" + detail;
        if (extra.Length > 400)
            extra = extra[..400];
        return "The command center wants to " + what + " on this PC. Allow it?" + extra;
    }

    private static bool SafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("..", StringComparison.Ordinal) || !Path.IsPathRooted(path))
            return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
        if (full.StartsWith(Folder, StringComparison.OrdinalIgnoreCase))
            return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !full.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Paired(string console)
    {
        return Read().Any(line => string.Equals(line, console, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Pair(string console)
    {
        var lines = Read().ToList();
        if (lines.Any(line => string.Equals(line, console, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (lines.Count >= 10)
            return false;
        lines.Add(console.ToUpperInvariant());
        var path = Path.Combine(Folder, "consoles.allow");
        Directory.CreateDirectory(Folder);
        File.WriteAllLines(path, lines);
        return true;
    }

    private static IEnumerable<string> Read()
    {
        var path = Path.Combine(Folder, "consoles.allow");
        if (!File.Exists(path))
            return [];
        try { return File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray(); }
        catch (IOException) { return []; }
    }

    private static string Issue(string console)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Tickets[token] = (console.ToUpperInvariant(), DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds());
        return token;
    }

    private static bool Accepts(string? token, string console)
    {
        if (string.IsNullOrWhiteSpace(token) || !Tickets.TryGetValue(token.Trim(), out var row))
            return false;
        if (row.Expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            Tickets.TryRemove(token.Trim(), out _);
            return false;
        }

        return string.Equals(row.Console, console, StringComparison.OrdinalIgnoreCase);
    }

    private static string PromptFailure(int answer, string denied)
    {
        if (answer == 7)
            return denied;
        if (answer == 32000)
            return "The question timed out on that PC. Try again and click Yes on its screen.";
        return "This PC could not show the allow question. Sign in on that PC and try again.";
    }

    private static int Ask(string message)
    {
        var sessions = new List<int>();
        var console = WTSGetActiveConsoleSessionId();
        if (console > 0)
            sessions.Add(console);
        foreach (var session in ActiveSessions())
        {
            if (!sessions.Contains(session))
                sessions.Add(session);
        }

        foreach (var session in sessions)
        {
            var answer = Send(session, message);
            if (answer != int.MinValue)
                return answer;
        }

        return ShowInSession(sessions, message);
    }

    private static int Send(int session, string message)
    {
        const int style = 0x4 | 0x30 | 0x100 | 0x10000 | 0x40000;
        var title = "Workshop Link";
        var ok = WTSSendMessage(IntPtr.Zero, session, title, (title.Length + 1) * 2, message, (message.Length + 1) * 2, style, 60, out var response, true);
        return ok ? response : int.MinValue;
    }

    private static List<int> ActiveSessions()
    {
        var list = new List<int>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pointer, out var count) || pointer == IntPtr.Zero)
            return list;
        try
        {
            var size = Marshal.SizeOf<WtsSession>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<WtsSession>(pointer + index * size);
                if (row.SessionId > 0 && row.State == 0)
                    list.Add(row.SessionId);
            }
        }
        finally
        {
            WTSFreeMemory(pointer);
        }

        return list;
    }

    private static int ShowInSession(List<int> sessions, string message)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return -1;
        var path = Path.Combine(Folder, "ask-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(path, message, new UTF8Encoding(false));
            foreach (var session in sessions)
            {
                var answer = Launch(exe, path, session);
                if (answer != int.MinValue)
                    return answer;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }

        return -1;
    }

    private static int Launch(string exe, string path, int session)
    {
        if (session <= 0 || !WTSQueryUserToken(session, out var token))
            return int.MinValue;
        try
        {
            if (!DuplicateTokenEx(token, 0x10000000, IntPtr.Zero, 2, 1, out var primary))
                return int.MinValue;
            try
            {
                var start = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
                var command = "\"" + exe + "\" --ask \"" + path + "\"";
                if (!CreateProcessAsUser(primary, null, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref start, out var info))
                    return int.MinValue;
                try
                {
                    if (WaitForSingleObject(info.Process, 60000) != 0)
                    {
                        TerminateProcess(info.Process, 32000);
                        return 32000;
                    }

                    return GetExitCodeProcess(info.Process, out var code) ? code : int.MinValue;
                }
                finally
                {
                    CloseHandle(info.Thread);
                    CloseHandle(info.Process);
                }
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

    private static void Protect(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        var append = FileSystemRights.Read | FileSystemRights.AppendData | FileSystemRights.ReadAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Synchronize;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), append, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.Read | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSession
    {
        public int SessionId;
        public IntPtr Name;
        public int State;
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
    private static extern int WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint primary);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(nint token, string? application, string command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo start, out ProcessInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out int code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSSendMessage(IntPtr server, int sessionId, string title, int titleBytes, string message, int messageBytes, int style, int timeoutSeconds, out int response, bool wait);
}

internal static class OpenViews
{
    private static int _count;
    public static void Enter() => Interlocked.Increment(ref _count);
    public static void Leave()
    {
        if (Interlocked.Decrement(ref _count) < 0)
            Interlocked.Exchange(ref _count, 0);
    }

    public static bool Any => Volatile.Read(ref _count) > 0;
}
