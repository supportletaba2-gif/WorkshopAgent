using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using WorkshopLink.Protocol;
using WorkshopLink.Screen;

namespace WorkshopAgent;

internal static class ScreenShare
{
    private static string _desktop = "";
    private static int _frameCodec;
    private static int _effectsOn;
    private static int _savedAnimate = 1;
    private static int _savedClient = 1;
    private static int _encoderNoted;
    private const int MaxWatchers = 10;
    private static readonly object RoomLock = new();
    private static readonly List<Watcher> Room = [];
    private static int _roomOpen;
    private static CaptureSlot? _roomSlot;
    private static CancellationTokenSource? _roomStop;
    private static DirectoryClient? _roomDirectory;

    private sealed class Watcher
    {
        public Stream Stream = Stream.Null;
        public SemaphoreSlim Gate = new(1, 1);
        public RelaySession? Pictures;
        public long Beat;
    }

    public static async Task ServeAsync(Stream stream, RelaySession? pictures, CancellationToken cancellationToken, DirectoryClient? directory = null)
    {
        AllowServiceSas();
        ConsoleSession.Ensure();
        if (!SessionLaunch.ConsoleUserPresent())
            await Task.Delay(400, cancellationToken).ContinueWith(_ => { }, CancellationToken.None);
        if (!TryJoin(stream, pictures, out var watcher))
        {
            await LinkPipe.WriteScreenAsync(stream, LinkPipe.ScreenBye, Encoding.UTF8.GetBytes("Ten people are already watching this PC. Close one screen, then try again."), cancellationToken);
            return;
        }

        if (directory is not null)
        {
            lock (RoomLock)
                _roomDirectory = directory;
        }

        EnsureRoom();
        if (pictures is not null && _roomSlot is not null)
            await _roomSlot.TryWriteAsync([LinkPipe.ScreenMode, LinkPipe.PictureAv1], cancellationToken);
        await AnnounceWatchersAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var beat = Task.Run(() => BeatAsync(watcher, linked), linked.Token);
        try
        {
            await ReadViewerAsync(stream, _roomSlot!, watcher, linked.Token);
        }
        finally
        {
            linked.Cancel();
            var last = Leave(watcher);
            try { await beat; } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            if (last)
                StopRoom();
            else
                await AnnounceWatchersAsync();
        }
    }

    private static bool TryJoin(Stream stream, RelaySession? pictures, out Watcher watcher)
    {
        watcher = new Watcher
        {
            Stream = stream,
            Pictures = pictures,
            Beat = Environment.TickCount64
        };
        lock (RoomLock)
        {
            if (Room.Count >= MaxWatchers)
                return false;
            Room.Add(watcher);
            return true;
        }
    }

    private static bool Leave(Watcher watcher)
    {
        lock (RoomLock)
        {
            Room.Remove(watcher);
            return Room.Count == 0;
        }
    }

    private static void EnsureRoom()
    {
        lock (RoomLock)
        {
            if (_roomOpen == 1)
                return;
            _roomOpen = 1;
            _roomSlot = new CaptureSlot();
            _roomStop = new CancellationTokenSource();
            if (!SessionLaunch.ConsoleUserPresent())
            {
                _roomSlot.Phase = 2;
                Interlocked.CompareExchange(ref _roomSlot.SasArmed, 1, 0);
            }

            var slot = _roomSlot;
            var token = _roomStop.Token;
            ConsoleSession.Changed += OnRoomSession;
            _ = Task.Run(() => SuperviseAsync(slot, token), token);
            _ = Task.Run(() => WatchUserAsync(slot, token), token);
        }
    }

    private static void StopRoom()
    {
        CancellationTokenSource? stop;
        CaptureSlot? slot;
        lock (RoomLock)
        {
            if (Room.Count > 0 || _roomOpen == 0)
                return;
            _roomOpen = 0;
            stop = _roomStop;
            _roomStop = null;
            slot = _roomSlot;
            _roomSlot = null;
            _roomDirectory = null;
            ConsoleSession.Changed -= OnRoomSession;
        }

        stop?.Cancel();
        slot?.CloseOwned();
        stop?.Dispose();
    }

    private static void OnRoomSession(int code, uint session)
    {
        var slot = _roomSlot;
        if (slot is null || session == 0)
            return;
        if (code == ConsoleSession.Logoff)
        {
            slot.Phase = 1;
            if (Volatile.Read(ref slot.SasArmed) == 0)
                Interlocked.Exchange(ref slot.SasArmed, 1);
            _ = WriteRoomAsync(LinkPipe.ScreenStatus, "logging off"u8.ToArray(), CancellationToken.None);
            return;
        }

        if (code == ConsoleSession.Logon)
            slot.Phase = 0;
        if (code == ConsoleSession.DesktopReady)
        {
            slot.Phase = 0;
            Interlocked.Exchange(ref slot.SasArmed, 0);
            _ = WriteRoomAsync(LinkPipe.ScreenStatus, [], CancellationToken.None);
        }
    }

    private static async Task BeatAsync(Watcher watcher, CancellationTokenSource linked)
    {
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(1000, linked.Token);
                if (Environment.TickCount64 - Volatile.Read(ref watcher.Beat) > 15000)
                {
                    linked.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AnnounceWatchersAsync()
    {
        int count;
        lock (RoomLock)
            count = Room.Count;
        if (count == 0)
            return;
        await WriteRoomAsync(LinkPipe.ScreenStatus, Encoding.UTF8.GetBytes("watching " + count + " of " + MaxWatchers), CancellationToken.None);
    }

    private static bool AnyOffsite()
    {
        lock (RoomLock)
        {
            foreach (var watcher in Room)
            {
                if (watcher.Pictures is not null)
                    return true;
            }
        }

        return false;
    }

    private static async Task SuperviseAsync(CaptureSlot slot, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var ever = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            HelperProcess? helper = null;
            try
            {
                var desktop = DesktopFor(slot);
                if (desktop.Contains("Winlogon", StringComparison.OrdinalIgnoreCase) && Volatile.Read(ref slot.SasArmed) == 0)
                    Interlocked.CompareExchange(ref slot.SasArmed, 1, 0);
                var pipeName = "WorkshopLinkScreen-" + Guid.NewGuid().ToString("N");
                pipe = CreateScreenPipe(pipeName);
                var offsite = AnyOffsite();
                var process = SessionLaunch.StartHelper(pipeName, desktop, offsite);
                helper = new HelperProcess(process);
                if (process == 0 || !await WaitForHelperAsync(pipe, cancellationToken))
                {
                    if (!ever && Environment.TickCount64 - started > 20000)
                    {
                        await WriteRoomAsync(LinkPipe.ScreenBye, Encoding.UTF8.GetBytes("The screen on that PC did not start."), CancellationToken.None);
                        break;
                    }
                }
                else
                {
                    ever = true;
                    slot.Own(pipe, helper);
                    SfuMedia? media = null;
                    var directory = _roomDirectory;
                    if (offsite && directory is not null)
                    {
                        media = new SfuMedia();
                        try
                        {
                            await media.PublishAsync(directory, PeerIdentity.Fingerprint, cancellationToken);
                            await slot.TryWriteAsync([LinkPipe.ScreenMode, LinkPipe.PictureAv1], cancellationToken);
                            _ = ReceiveFilesAsync(slot, cancellationToken);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
                        {
                            await media.DisposeAsync();
                            media = null;
                            await WriteRoomAsync(LinkPipe.ScreenStatus, Encoding.UTF8.GetBytes(ex.Message), CancellationToken.None);
                        }
                    }

                    var copying = CopyOutAsync(pipe, media, cancellationToken);
                    try
                    {
                        await AnnounceLogonAsync(slot, desktop, cancellationToken);
                        await copying;
                    }
                    finally
                    {
                        if (media is not null)
                            await media.DisposeAsync();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                if (pipe is not null)
                    slot.Forget(pipe);
                helper?.Close();
                pipe?.Dispose();
            }

            if (cancellationToken.IsCancellationRequested)
                break;
            try { await Task.Delay(300, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static string DesktopFor(CaptureSlot slot)
    {
        if (slot.Phase is 1 or 2)
            return @"winsta0\Winlogon";
        if (!SessionLaunch.ConsoleUserPresent())
        {
            Thread.Sleep(200);
            if (!SessionLaunch.ConsoleUserPresent())
                return @"winsta0\Winlogon";
        }
        return @"winsta0\default";
    }

    private static async Task AnnounceLogonAsync(CaptureSlot slot, string desktop, CancellationToken cancellationToken)
    {
        if (!desktop.Contains("Winlogon", StringComparison.OrdinalIgnoreCase))
            return;
        if (Interlocked.CompareExchange(ref slot.SasArmed, 2, 1) != 1)
        {
            if (slot.Phase is 1 or 2)
                await WriteRoomAsync(LinkPipe.ScreenStatus, "logon screen"u8.ToArray(), cancellationToken);
            return;
        }

        slot.Phase = 2;
        await WriteRoomAsync(LinkPipe.ScreenStatus, "logon screen"u8.ToArray(), cancellationToken);
        try { await Task.Delay(600, cancellationToken); }
        catch (OperationCanceledException) { return; }
        SendSecureAttention();
    }

    private static async Task WatchUserAsync(CaptureSlot slot, CancellationToken cancellationToken)
    {
        var sawUser = SessionLaunch.ConsoleUserPresent();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken);
                var user = SessionLaunch.ConsoleUserPresent();
                if (sawUser != user)
                {
                    slot.Phase = user ? 0 : 1;
                    Interlocked.Exchange(ref slot.SasArmed, user ? 0 : 1);
                    if (user)
                        await WriteRoomAsync(LinkPipe.ScreenStatus, [], CancellationToken.None);
                    slot.CloseOwned();
                }
                sawUser = user;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ReadViewerAsync(Stream network, CaptureSlot slot, Watcher watcher, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (kind, payload) = await LinkPipe.ReadScreenAsync(network, cancellationToken);
                Volatile.Write(ref watcher.Beat, Environment.TickCount64);
                if (kind == LinkPipe.ScreenBye)
                    break;
                if (kind == LinkPipe.ScreenFile)
                {
                    SaveIncoming(payload);
                    continue;
                }

                if (kind == LinkPipe.ScreenSecure)
                {
                    SendSecureAttention();
                    continue;
                }

                if (kind == LinkPipe.ScreenHeartbeat)
                    continue;

                if (kind == LinkPipe.ScreenLogoff)
                {
                    slot.Phase = 1;
                    if (Volatile.Read(ref slot.SasArmed) != 2)
                        Interlocked.Exchange(ref slot.SasArmed, 1);
                    await WriteRoomAsync(LinkPipe.ScreenStatus, "logging off"u8.ToArray(), cancellationToken);
                    SessionLaunch.LogoffConsole();
                    continue;
                }

                if (kind == LinkPipe.ScreenStatus)
                    continue;
                var packet = new byte[1 + payload.Length];
                packet[0] = kind;
                payload.CopyTo(packet, 1);
                await slot.TryWriteAsync(packet, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static NamedPipeServerStream CreateScreenPipe(string pipeName)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private static async Task<bool> WaitForHelperAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await pipe.WaitForConnectionAsync(wait.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<(bool Yes, string Folder)>> PasteAnswers = new();

    private static async Task<string?> RequestPasteAsync(CaptureSlot slot, string transfer, string name, long size, CancellationToken cancellationToken)
    {
        var wait = new TaskCompletionSource<(bool Yes, string Folder)>(TaskCreationOptions.RunContinuationsAsynchronously);
        PasteAnswers[transfer] = wait;
        var text = Encoding.UTF8.GetBytes(transfer + "\n" + name + "\n" + size);
        var packet = new byte[1 + text.Length];
        packet[0] = LinkPipe.ScreenPaste;
        text.CopyTo(packet, 1);
        await slot.TryWriteAsync(packet, cancellationToken);
        try
        {
            var answer = await wait.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            return answer.Yes ? answer.Folder : null;
        }
        catch (TimeoutException)
        {
            PasteAnswers.TryRemove(transfer, out _);
            return null;
        }
    }

    private static async Task ReceiveFilesAsync(CaptureSlot slot, CancellationToken cancellationToken)
    {
        try
        {
            await using var files = new FileRelay();
            var folders = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            files.PasteOffered += offer =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var folder = await RequestPasteAsync(slot, offer.Transfer, offer.Name, offer.Size, cancellationToken);
                        if (folder is null)
                        {
                            await files.DeclineAsync(offer.Transfer, cancellationToken);
                            return;
                        }

                        folders[offer.Transfer] = folder;
                        await files.AcceptAsync(offer.Transfer, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException)
                    {
                    }
                }, cancellationToken);
            };
            files.Ready += (transfer, name, size) =>
            {
                var folder = folders.TryRemove(transfer, out var path) ? path : @"C:\WorkshopLink\Incoming";
                Directory.CreateDirectory(folder);
                _ = files.DownloadAsync(transfer, Path.Combine(folder, Path.GetFileName(name)), cancellationToken);
            };
            await files.ConnectAsync(PeerIdentity.Fingerprint, "source", cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException or TimeoutException or System.Net.Http.HttpRequestException)
        {
        }
    }

    private static async Task CopyOutAsync(Stream pipe, SfuMedia? media, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        while (!cancellationToken.IsCancellationRequested)
        {
            await ReadExactAsync(pipe, length, cancellationToken);
            var size = BitConverter.ToInt32(length);
            if (size <= 0 || size > 4_000_000)
                break;
            var body = new byte[size];
            await ReadExactAsync(pipe, body, cancellationToken);
            if (body.Length == 0)
                continue;
            if (media is not null && (body[0] == LinkPipe.PictureAv1 || body[0] == LinkPipe.PictureH264))
                media.Submit(body[1..]);
            if (body[0] == LinkPipe.PictureAv1 || body[0] == LinkPipe.PictureH264 || body[0] == LinkPipe.PictureJpeg)
            {
                await FanPictureAsync(body, cancellationToken);
                continue;
            }

            if (body[0] == LinkPipe.ScreenPaste)
            {
                var text = Encoding.UTF8.GetString(body, 1, Math.Max(0, body.Length - 1));
                var lines = text.Split('\n');
                if (lines.Length >= 2 && PasteAnswers.TryRemove(lines[0], out var wait))
                    wait.TrySetResult((lines[1] == "yes", lines.Length > 2 ? lines[2] : @"C:\WorkshopLink\Incoming"));
                continue;
            }

            if (body[0] == LinkPipe.ScreenStatus)
            {
                await WriteRoomAsync(LinkPipe.ScreenStatus, body[1..], cancellationToken);
                continue;
            }

            if (body[0] == LinkPipe.ScreenDisplay)
            {
                await WriteRoomAsync(LinkPipe.ScreenDisplay, body[1..], cancellationToken);
                continue;
            }

            await FanPictureAsync(body, cancellationToken);
        }
    }

    private static async Task FanPictureAsync(byte[] body, CancellationToken cancellationToken)
    {
        Watcher[] watchers;
        lock (RoomLock)
            watchers = Room.ToArray();
        foreach (var watcher in watchers)
        {
            if (watcher.Pictures is not null)
            {
                var sent = watcher.Pictures.TrySendPicture(body);
                for (var attempt = 0; !sent && attempt < 4 && !cancellationToken.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(5, cancellationToken);
                    sent = watcher.Pictures.TrySendPicture(body);
                }

                if (sent)
                    continue;
            }

            await WriteKindAsync(watcher.Stream, watcher.Gate, LinkPipe.ScreenTile, body, cancellationToken);
        }
    }

    private static async Task WriteRoomAsync(byte kind, byte[] payload, CancellationToken cancellationToken)
    {
        Watcher[] watchers;
        lock (RoomLock)
            watchers = Room.ToArray();
        foreach (var watcher in watchers)
            await WriteKindAsync(watcher.Stream, watcher.Gate, kind, payload, cancellationToken);
    }

    private static async Task WriteKindAsync(Stream network, SemaphoreSlim gate, byte kind, byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await LinkPipe.WriteScreenAsync(network, kind, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class HelperProcess
    {
        private nint _handle;
        private int _closed;

        public HelperProcess(nint handle) => _handle = handle;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;
            var handle = Interlocked.Exchange(ref _handle, 0);
            SessionLaunch.StopProcess(handle);
        }
    }

    private sealed class CaptureSlot
    {
        private readonly object _gate = new();
        private NamedPipeServerStream? _pipe;
        private HelperProcess? _helper;
        private int _phase;
        public int SasArmed;

        public int Phase
        {
            get => Volatile.Read(ref _phase);
            set => Volatile.Write(ref _phase, value);
        }

        public void Own(NamedPipeServerStream pipe, HelperProcess helper)
        {
            lock (_gate)
            {
                _pipe = pipe;
                _helper = helper;
            }
        }

        public void Forget(NamedPipeServerStream pipe)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pipe, pipe))
                    _pipe = null;
            }
        }

        public void CloseOwned()
        {
            HelperProcess? helper;
            lock (_gate)
            {
                helper = _helper;
                _helper = null;
                _pipe = null;
            }

            helper?.Close();
        }

        public async Task TryWriteAsync(byte[] packet, CancellationToken cancellationToken)
        {
            NamedPipeServerStream? pipe;
            lock (_gate)
                pipe = _pipe;
            if (pipe is null || !pipe.IsConnected)
                return;
            try
            {
                var size = BitConverter.GetBytes(packet.Length);
                await pipe.WriteAsync(size, cancellationToken);
                await pipe.WriteAsync(packet, cancellationToken);
                await pipe.FlushAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private static void SaveIncoming(byte[] payload)
    {
        if (payload.Length < 8)
            return;
        var nameLength = BitConverter.ToInt32(payload, 0);
        if (nameLength is < 1 or > 180 || payload.Length < 8 + nameLength)
            return;
        var name = Path.GetFileName(Encoding.UTF8.GetString(payload, 4, nameLength));
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return;
        var offset = BitConverter.ToInt32(payload, 4 + nameLength);
        if (offset < 0 || offset > 512 * 1024 * 1024)
            return;
        var start = 8 + nameLength;
        var folder = @"C:\WorkshopLink\Incoming";
        Directory.CreateDirectory(folder);
        using var file = new FileStream(Path.Combine(folder, name), offset == 0 ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (offset > 0)
            file.Position = offset;
        file.Write(payload, start, payload.Length - start);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    public static void RunHelper(string pipeName, bool fullFrame = false)
    {
        _ = fullFrame;
        RunHelperCore(pipeName);
    }

    private static void RunHelperCore(string pipeName)
    {
        PhysicalDesktop.MakeAware();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(20000);
        using var cts = new CancellationTokenSource();
        InputDesktop.AttachStation();
        CancellationTokenSource? borderStop = null;
        var input = new Thread(() => ReadInput(pipe, cts.Token)) { IsBackground = true };
        input.Start();
        var codec = ImageCodecInfo.GetImageEncoders().First(item => item.MimeType == "image/jpeg");
        var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 52L);
        var shown = "";
        CancellationTokenSource? captureStop = null;
        Thread? capture = null;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var seen = InputDesktop.Peek();
                if (seen.Length == 0)
                    seen = InputDesktop.CanOpen("Winlogon") ? "Winlogon" : "Default";
                if (!string.Equals(seen, shown, StringComparison.OrdinalIgnoreCase))
                {
                    captureStop?.Cancel();
                    capture?.Join(1500);
                    shown = seen;
                    UseBorder(cts, ref borderStop, true);
                    captureStop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    var stop = captureStop.Token;
                    capture = new Thread(() =>
                    {
                        var ended = CaptureDesktop(pipe, codec, quality, stop);
                        if (ended == 1)
                            cts.Cancel();
                        else
                            shown = "";
                    }) { IsBackground = true };
                    capture.Start();
                }
                Thread.Sleep(200);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            cts.Cancel();
            captureStop?.Cancel();
            capture?.Join(1500);
            RestoreEffects();
        }
    }

    private static int CaptureDesktop(NamedPipeClientStream pipe, ImageCodecInfo codec, EncoderParameters quality, CancellationToken cancellationToken)
    {
        var misses = 0;
        try
        {
        if (!InputDesktop.AttachOnce())
            return 2;
        var hashes = Array.Empty<int>();
        var heal = 0;
        var attached = InputDesktop.Attached;
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            var pace = 16;
            try
            {
                var winlogon = InputDesktop.Attached.Contains("Winlogon", StringComparison.OrdinalIgnoreCase);
                if (!winlogon)
                    QuietEffects();
                var bounds = winlogon
                    ? new Rectangle(0, 0, GetSystemMetrics(0), GetSystemMetrics(1))
                    : PhysicalDesktop.Selected(Volatile.Read(ref ScreenState.Choice));
                using var frame = CaptureBounds(bounds);
                if (frame is null)
                {
                    if (++misses >= 4)
                        return 2;
                    Thread.Sleep(30);
                    continue;
                }
                misses = 0;
                var video = _frameCodec == LinkPipe.PictureH264 || _frameCodec == LinkPipe.PictureAv1;
                Bitmap? fitted = null;
                var picture = frame;
                var imageWidth = frame.Width;
                var imageHeight = frame.Height;
                try
                {
                    if (video)
                    {
                        pace = 66;
                        fitted = FitStream(frame);
                        if (fitted is not null)
                            picture = fitted;
                        MarkPointer(picture, frame.Width, frame.Height, bounds);
                        imageWidth = picture.Width;
                        imageHeight = picture.Height;
                        QueueEncode(pipe, picture);
                    }
                    else if (_frameCodec == LinkPipe.PictureJpeg)
                        WriteFittedJpeg(pipe, frame, codec);
                    else
                        hashes = WriteTiles(pipe, frame, codec, quality, hashes, ref heal);
                }
                finally
                {
                    fitted?.Dispose();
                }
                ScreenState.ImageWidth = imageWidth;
                ScreenState.ImageHeight = imageHeight;
                ScreenState.Left = bounds.Left;
                ScreenState.Top = bounds.Top;
                ScreenState.Width = Math.Max(1, bounds.Width);
                ScreenState.Height = Math.Max(1, bounds.Height);
                if (!winlogon)
                    AnnounceDisplays(pipe);
                if (!string.Equals(InputDesktop.Peek(), attached, StringComparison.OrdinalIgnoreCase))
                    return 0;
            }
            catch (IOException)
            {
                return 1;
            }
            catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException)
            {
                return 2;
            }
            var spent = Environment.TickCount64 - started;
            var wait = pace - (int)spent;
            if (wait > 0)
                Thread.Sleep(wait);
        }
        return 0;
        }
        finally
        {
            InputDesktop.Release();
        }
    }

    private static void UseBorder(CancellationTokenSource session, ref CancellationTokenSource? borderStop, bool show)
    {
        if (!show)
        {
            borderStop?.Cancel();
            borderStop = null;
            return;
        }

        if (borderStop is not null)
            return;
        borderStop = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        var token = borderStop.Token;
        var border = new Thread(() => Application.Run(new SessionBorder(token))) { IsBackground = true };
        border.SetApartmentState(ApartmentState.STA);
        border.Start();
    }

    private static Bitmap? FitStream(Bitmap frame)
    {
        const int maxWidth = 1280;
        const int maxHeight = 720;
        if (frame.Width <= maxWidth && frame.Height <= maxHeight)
            return null;
        var scale = Math.Min(maxWidth / (float)frame.Width, maxHeight / (float)frame.Height);
        var width = Align8((int)(frame.Width * scale));
        var height = Align8((int)(frame.Height * scale));
        if (width < 8 || height < 8 || (width == frame.Width && height == frame.Height))
            return null;
        var fitted = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        fitted.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(fitted);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.DrawImage(frame, new Rectangle(0, 0, width, height));
        return fitted;
    }

    private static int Align8(int value)
    {
        var size = Math.Max(8, value);
        return size - (size % 8);
    }

    private static void MarkPointer(Bitmap picture, int captureWidth, int captureHeight, Rectangle bounds)
    {
        try
        {
            int x;
            int y;
            if (GetCursorPos(out var point))
            {
                var localX = point.X - bounds.Left;
                var localY = point.Y - bounds.Top;
                if (localX < 0 || localY < 0 || localX >= captureWidth || localY >= captureHeight)
                {
                    ScreenState.Marked = false;
                    return;
                }
                x = (int)Math.Round(localX * (picture.Width - 1) / (double)Math.Max(1, captureWidth - 1));
                y = (int)Math.Round(localY * (picture.Height - 1) / (double)Math.Max(1, captureHeight - 1));
            }
            else if (ScreenState.PointerSeen)
            {
                x = ScreenState.PointerX;
                y = ScreenState.PointerY;
            }
            else
            {
                ScreenState.Marked = false;
                return;
            }
            x = Math.Clamp(x, 0, Math.Max(0, picture.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, picture.Height - 1));
            ScreenState.MarkX = x;
            ScreenState.MarkY = y;
            ScreenState.Marked = true;
            using var mark = Graphics.FromImage(picture);
            mark.PageUnit = GraphicsUnit.Pixel;
            mark.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var left = Math.Clamp(x - 8, 0, Math.Max(0, picture.Width - 17));
            var top = Math.Clamp(y - 8, 0, Math.Max(0, picture.Height - 17));
            using var fill = new SolidBrush(Color.FromArgb(230, 220, 38, 38));
            mark.FillEllipse(fill, left, top, 16, 16);
            using var pen = new Pen(Color.White, 2f);
            mark.DrawEllipse(pen, left, top, 16, 16);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            ScreenState.Marked = false;
        }
    }

    private static void QuietEffects()
    {
        if (Interlocked.Exchange(ref _effectsOn, 1) != 0)
            return;
        try
        {
            var info = new AnimationInfo { Size = Marshal.SizeOf<AnimationInfo>() };
            if (SystemParametersInfoAnim(0x0048, (uint)info.Size, ref info, 0))
            {
                _savedAnimate = info.MinAnimate;
                info.MinAnimate = 0;
                SystemParametersInfoAnim(0x0049, (uint)info.Size, ref info, 2);
            }
            var client = 1;
            if (SystemParametersInfoInt(0x1042, 0, ref client, 0))
            {
                _savedClient = client;
                client = 0;
                SystemParametersInfoInt(0x1043, 0, ref client, 2);
            }
        }
        catch (Win32Exception)
        {
            Interlocked.Exchange(ref _effectsOn, 0);
        }
    }

    private static void RestoreEffects()
    {
        if (Interlocked.Exchange(ref _effectsOn, 0) != 1)
            return;
        try
        {
            var info = new AnimationInfo { Size = Marshal.SizeOf<AnimationInfo>(), MinAnimate = _savedAnimate };
            SystemParametersInfoAnim(0x0049, (uint)info.Size, ref info, 2);
            var client = _savedClient;
            SystemParametersInfoInt(0x1043, 0, ref client, 2);
        }
        catch (Win32Exception)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public int Size;
        public int MinAnimate;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoAnim(uint action, uint param, ref AnimationInfo info, uint update);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoInt(uint action, uint param, ref int value, uint update);

    private static Bitmap? CaptureBounds(Rectangle bounds)
    {
        if (bounds.Width < 2 || bounds.Height < 2 || bounds.Width > 16384 || bounds.Height > 16384)
            return null;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.PageScale = 1f;
        var dest = graphics.GetHdc();
        var source = GetDC(IntPtr.Zero);
        var copied = BitBlt(dest, 0, 0, bounds.Width, bounds.Height, source, bounds.Left, bounds.Top, 0x00CC0020);
        ReleaseDC(IntPtr.Zero, source);
        graphics.ReleaseHdc(dest);
        if (!copied)
        {
            bitmap.Dispose();
            return null;
        }
        return bitmap;
    }

    private static void DrainInput(NamedPipeClientStream pipe)
    {
        while (PeekNamedPipe(pipe.SafePipeHandle.DangerousGetHandle(), 0, 0, out _, out var available, out _) && available >= 4)
        {
            var length = new byte[4];
            ReadExact(pipe, length);
            var size = BitConverter.ToInt32(length);
            if (size <= 0 || size > 65536)
                return;
            var packet = new byte[size];
            ReadExact(pipe, packet);
            if (packet.Length > 1 && packet[0] == LinkPipe.ScreenPaste)
                AskPaste(pipe, Encoding.UTF8.GetString(packet, 1, packet.Length - 1));
            else
                ApplyInput(packet);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int srcX, int srcY, int op);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(nint pipe, nint buffer, uint size, out uint read, out uint available, out uint left);

    private static void ReadInput(Stream pipe, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        while (!cancellationToken.IsCancellationRequested)
        {
            InputDesktop.AttachOnce();
            ReadExact(pipe, length);
            var size = BitConverter.ToInt32(length);
            if (size <= 0 || size > 65536)
                break;
            var packet = new byte[size];
            ReadExact(pipe, packet);
            ApplyInput(packet);
        }
    }

    private static void ApplyInput(byte[] packet)
    {
        if (packet[0] == LinkPipe.ScreenMouse && packet.Length >= 17)
        {
            var x = BitConverter.ToInt32(packet, 1);
            var y = BitConverter.ToInt32(packet, 5);
            var button = BitConverter.ToInt32(packet, 9);
            var wheel = BitConverter.ToInt32(packet, 13);
            MoveMouse(x, y, button, wheel);
            ScreenState.PointerX = x;
            ScreenState.PointerY = y;
            ScreenState.PointerSeen = true;
        }
        else if (packet[0] == LinkPipe.ScreenKey && packet.Length >= 9)
        {
            var extended = packet.Length >= 13 && BitConverter.ToInt32(packet, 9) != 0;
            PressKey(BitConverter.ToInt32(packet, 1), BitConverter.ToInt32(packet, 5) != 0, extended);
        }
        else if (packet[0] == LinkPipe.ScreenPrivacy && packet.Length >= 2)
        {
            PrivacyCover.Set(packet[1] == 1);
        }
        else if (packet[0] == LinkPipe.ScreenDisplay && packet.Length >= 2)
        {
            var next = packet[1];
            if (next > 8)
                next = 0;
            if (next != Volatile.Read(ref ScreenState.Choice))
            {
                Volatile.Write(ref ScreenState.Choice, next);
                ScreenState.PointerSeen = false;
            }
        }
        else if (packet[0] == LinkPipe.ScreenMode && packet.Length >= 2)
        {
            _frameCodec = packet[1];
        }
    }

    internal static void AskPaste(Stream pipe, string text)
    {
        var lines = text.Split('\n');
        if (lines.Length < 3)
            return;
        var transfer = lines[0];
        var name = lines[1];
        var size = lines[2];
        var thread = new Thread(() =>
        {
            var yes = MessageBox.Show(
                "Accept " + name + " (" + size + " bytes) from the command center?",
                "Workshop Link",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2,
                MessageBoxOptions.DefaultDesktopOnly) == DialogResult.Yes;
            var folder = yes ? OpenFolder() : "";
            var answer = Encoding.UTF8.GetBytes(transfer + "\n" + (yes ? "yes" : "no") + "\n" + folder);
            WriteCodedFrame(pipe, LinkPipe.ScreenPaste, answer);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static string OpenFolder()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is not null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                foreach (dynamic window in shell.Windows())
                {
                    try
                    {
                        string path = window.Document.Folder.Self.Path;
                        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                            return path;
                    }
                    catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or System.IO.IOException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or System.IO.IOException)
        {
        }

        var incoming = @"C:\WorkshopLink\Incoming";
        Directory.CreateDirectory(incoming);
        return incoming;
    }

    private static void MoveMouse(int x, int y, int button, int wheel)
    {
        var width = ScreenState.ImageWidth;
        var height = ScreenState.ImageHeight;
        if (width < 2 || height < 2)
            return;
        var screenX = ScreenState.Left + (int)Math.Round(x * (ScreenState.Width - 1) / (double)(width - 1));
        var screenY = ScreenState.Top + (int)Math.Round(y * (ScreenState.Height - 1) / (double)(height - 1));
        var desktop = PhysicalDesktop.Bounds();
        var spanX = Math.Max(1, desktop.Width - 1);
        var spanY = Math.Max(1, desktop.Height - 1);
        var absX = (int)Math.Round((screenX - desktop.Left) * 65535d / spanX);
        var absY = (int)Math.Round((screenY - desktop.Top) * 65535d / spanY);
        absX = Math.Clamp(absX, 0, 65535);
        absY = Math.Clamp(absY, 0, 65535);
        uint flags = MouseMove | MouseAbsolute | MouseVirtualDesk;
        uint data = 0;
        if (button == 1) flags |= MouseLeftDown;
        else if (button == 2) flags |= MouseLeftUp;
        else if (button == 3) flags |= MouseRightDown;
        else if (button == 4) flags |= MouseRightUp;
        if (wheel != 0)
        {
            flags |= MouseWheel;
            data = (uint)wheel;
        }

        SetCursorPos(screenX, screenY);
        var input = new MousePacket
        {
            Type = 0,
            Dx = absX,
            Dy = absY,
            Data = unchecked((int)data),
            Flags = flags
        };
        if (SendInput(1, [input], Marshal.SizeOf<MousePacket>()) == 0)
            mouse_event(flags & ~(MouseMove | MouseAbsolute | MouseVirtualDesk), 0, 0, data, 0);
    }

    private static void AttachInputDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x10000000);
        if (desktop == 0)
            return;
        SetThreadDesktop(desktop);
        CloseDesktop(desktop);
    }

    private static void PressKey(int virtualKey, bool up, bool extended)
    {
        if (virtualKey is < 1 or > 254)
            return;
        var scan = (ushort)MapVirtualKey((uint)virtualKey, 0);
        uint flags = KeyScan;
        if (up)
            flags |= KeyUp;
        if (extended || IsExtended(virtualKey))
            flags |= KeyExtended;
        var input = new KeyPacket
        {
            Type = 1,
            Scan = scan,
            Flags = flags
        };
        if (SendInput(1, [input], Marshal.SizeOf<KeyPacket>()) == 0)
            keybd_event((byte)virtualKey, (byte)scan, flags, 0);
    }

    private static bool IsExtended(int virtualKey) =>
        virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5;

    private static void WriteFittedJpeg(Stream pipe, Bitmap frame, ImageCodecInfo codec)
    {
        var quality = 60L;
        byte[] jpeg = EncodeJpeg(frame, codec, quality);
        for (var attempt = 0; attempt < 2 && jpeg.Length > 150_000; attempt++)
        {
            quality = jpeg.Length > 150_000 ? Math.Max(25, quality - 10) : Math.Min(80, quality + 8);
            jpeg = EncodeJpeg(frame, codec, quality);
        }

        WriteCodedFrame(pipe, LinkPipe.PictureJpeg, jpeg);
    }

    private static byte[] EncodeJpeg(Bitmap frame, ImageCodecInfo codec, long quality)
    {
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var memory = new MemoryStream();
        frame.Save(memory, codec, parameters);
        return memory.ToArray();
    }

    private static int _encoding;

    private static void QueueEncode(Stream pipe, Bitmap frame)
    {
        if (Interlocked.CompareExchange(ref _encoding, 1, 0) != 0)
            return;
        var copy = new Bitmap(frame);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var av1 = _frameCodec == LinkPipe.PictureAv1;
                byte[] nal;
                var encoded = av1 ? H264Codec.TryEncodeAv1(copy, out nal) : H264Codec.TryEncode(copy, out nal);
                if (encoded)
                    WriteCodedFrame(pipe, av1 ? LinkPipe.PictureAv1 : LinkPipe.PictureH264, nal);
                else if (Interlocked.Exchange(ref _encoderNoted, 1) == 0)
                    WriteCodedFrame(pipe, LinkPipe.ScreenStatus, Encoding.UTF8.GetBytes(av1
                        ? "The remote picture did not start. " + H264Codec.LastError
                        : "The screen picture is not ready."));
            }
            finally
            {
                copy.Dispose();
                Interlocked.Exchange(ref _encoding, 0);
            }
        });
    }

    private static readonly object PipeLock = new();

    private static void WriteCodedFrame(Stream pipe, byte codec, byte[] payload)
    {
        var body = new byte[1 + payload.Length];
        body[0] = codec;
        payload.CopyTo(body, 1);
        var size = BitConverter.GetBytes(body.Length);
        lock (PipeLock)
        {
            pipe.Write(size, 0, 4);
            pipe.Write(body, 0, body.Length);
            pipe.Flush();
        }
    }

    private static int _shownChoice = -1;
    private static int _shownCount = -1;

    private static void AnnounceDisplays(Stream pipe)
    {
        var count = Math.Min(8, PhysicalDesktop.Monitors().Count);
        var choice = Volatile.Read(ref ScreenState.Choice);
        if (choice < 0 || choice > count)
            choice = 0;
        if (choice == _shownChoice && count == _shownCount)
            return;
        _shownChoice = choice;
        _shownCount = count;
        WriteCodedFrame(pipe, LinkPipe.ScreenDisplay, [(byte)count, (byte)choice]);
    }

    private static int[] WriteTiles(Stream pipe, Bitmap frame, ImageCodecInfo codec, EncoderParameters quality, int[] hashes, ref int heal)
    {
        const int tile = 256;
        var cols = Math.Max(1, (frame.Width + tile - 1) / tile);
        var rows = Math.Max(1, (frame.Height + tile - 1) / tile);
        var count = cols * rows;
        if (hashes.Length != count)
            hashes = new int[count];
        var sent = 0;
        var refresh = heal % count;
        heal++;
        for (var index = 0; index < count; index++)
        {
            var col = index % cols;
            var row = index / cols;
            var x = col * tile;
            var y = row * tile;
            var tw = Math.Min(tile, frame.Width - x);
            var th = Math.Min(tile, frame.Height - y);
            var hash = TileHash(frame, x, y, tw, th);
            var underPointer = (ScreenState.Marked
                    && ScreenState.MarkX >= x && ScreenState.MarkX < x + tw
                    && ScreenState.MarkY >= y && ScreenState.MarkY < y + th)
                || (ScreenState.PointerSeen
                    && ScreenState.PointerX >= x && ScreenState.PointerX < x + tw
                    && ScreenState.PointerY >= y && ScreenState.PointerY < y + th);
            if (hash == hashes[index] && index != refresh && !underPointer)
                continue;
            if (sent >= 16 && index != refresh)
                continue;
            using var piece = new Bitmap(tw, th);
            using (var graphics = Graphics.FromImage(piece))
                graphics.DrawImage(frame, new Rectangle(0, 0, tw, th), new Rectangle(x, y, tw, th), GraphicsUnit.Pixel);
            using var memory = new MemoryStream();
            piece.Save(memory, codec, quality);
            var jpeg = memory.ToArray();
            var body = new byte[13 + jpeg.Length];
            body[0] = 2;
            BitConverter.TryWriteBytes(body.AsSpan(1), (ushort)frame.Width);
            BitConverter.TryWriteBytes(body.AsSpan(3), (ushort)frame.Height);
            BitConverter.TryWriteBytes(body.AsSpan(5), (ushort)x);
            BitConverter.TryWriteBytes(body.AsSpan(7), (ushort)y);
            BitConverter.TryWriteBytes(body.AsSpan(9), (ushort)tw);
            BitConverter.TryWriteBytes(body.AsSpan(11), (ushort)th);
            jpeg.CopyTo(body, 13);
            var size = BitConverter.GetBytes(body.Length);
            lock (PipeLock)
            {
                pipe.Write(size, 0, 4);
                pipe.Write(body, 0, body.Length);
            }
            hashes[index] = hash;
            sent++;
        }

        lock (PipeLock)
            pipe.Flush();
        return hashes;
    }

    private static int TileHash(Bitmap frame, int x, int y, int width, int height)
    {
        var bits = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var hash = 17;
            var stepX = Math.Max(1, width / 4);
            var stepY = Math.Max(1, height / 4);
            for (var py = y; py < y + height; py += stepY)
            {
                for (var px = x; px < x + width; px += stepX)
                    hash = unchecked(hash * 31 + Marshal.ReadByte(bits.Scan0, py * bits.Stride + px * 4));
            }

            return hash;
        }
        finally
        {
            frame.UnlockBits(bits);
        }
    }

    private static bool SamePicture(Bitmap frame, byte[] sample, ref bool haveSample)
    {
        var bits = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var next = new byte[sample.Length];
            var index = 0;
            for (var y = 0; y < frame.Height && index < next.Length; y += Math.Max(1, frame.Height / 8))
            {
                for (var x = 0; x < frame.Width && index < next.Length; x += Math.Max(1, frame.Width / 12))
                {
                    next[index] = Marshal.ReadByte(bits.Scan0, y * bits.Stride + x * 4);
                    index++;
                }
            }

            if (haveSample && next.AsSpan().SequenceEqual(sample))
                return true;
            next.CopyTo(sample, 0);
            haveSample = true;
            return false;
        }
        finally
        {
            frame.UnlockBits(bits);
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseWheel = 0x0800;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(nint desktop);

    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint KeyScan = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MousePacket[] inputs, int bytes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyPacket[] inputs, int bytes);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scan, uint flags, nuint extra);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MousePacket
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public int Dx;
        [FieldOffset(12)] public int Dy;
        [FieldOffset(16)] public int Data;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public nint Extra;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyPacket
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint Extra;
    }

    private static void AllowServiceSas()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            var current = key?.GetValue("SoftwareSASGeneration");
            var value = current is int number ? number : 0;
            if (value is not (1 or 3))
                key?.SetValue("SoftwareSASGeneration", 3, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private static void SendSecureAttention()
    {
        try
        {
            AllowServiceSas();
            SessionLaunch.SendSecureAttention();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}

internal static class PhysicalDesktop
{
    public static void MakeAware()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { }
    }

    public static Rectangle Bounds()
    {
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        var any = false;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return true;
            any = true;
            if (info.Monitor.Left < left)
                left = info.Monitor.Left;
            if (info.Monitor.Top < top)
                top = info.Monitor.Top;
            if (info.Monitor.Right > right)
                right = info.Monitor.Right;
            if (info.Monitor.Bottom > bottom)
                bottom = info.Monitor.Bottom;
            return true;
        }, IntPtr.Zero);
        if (!any || right <= left || bottom <= top)
            return SystemInformation.VirtualScreen;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static List<Rectangle> Monitors()
    {
        var found = new List<Rectangle>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return true;
            var rect = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
            if (rect.Width >= 2 && rect.Height >= 2)
                found.Add(rect);
            return true;
        }, IntPtr.Zero);
        found.Sort((a, b) =>
        {
            var across = a.Left.CompareTo(b.Left);
            return across != 0 ? across : a.Top.CompareTo(b.Top);
        });
        return found;
    }

    public static Rectangle Selected(int choice)
    {
        var monitors = Monitors();
        if (choice >= 1 && choice <= monitors.Count)
            return monitors[choice - 1];
        return Bounds();
    }

    private delegate bool MonitorProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

internal sealed class SessionBorder : Form
{
    public SessionBorder(CancellationToken token)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = PhysicalDesktop.Bounds();
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        token.Register(() =>
        {
            try
            {
                if (IsHandleCreated)
                    BeginInvoke(Close);
                else
                    Close();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        });
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000020 | 0x00080000 | 0x08000000 | 0x00000080;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(Color.Gold, 3);
        e.Graphics.DrawRectangle(pen, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
    }
}

internal static class PrivacyCover
{
    private static Form? _cover;

    public static void Set(bool on)
    {
        if (on)
        {
            if (_cover is not null)
                return;
            var thread = new Thread(() =>
            {
                var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    WindowState = FormWindowState.Maximized,
                    TopMost = true,
                    BackColor = Color.Black,
                    ShowInTaskbar = false,
                    Text = "Support has covered this screen"
                };
                form.Shown += (_, _) => _cover = form;
                form.FormClosed += (_, _) => _cover = null;
                Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return;
        }

        var cover = _cover;
        if (cover is null || cover.IsDisposed)
            return;
        try { cover.BeginInvoke(cover.Close); } catch (InvalidOperationException) { }
    }
}

internal static class ScreenState
{
    public static int ImageWidth = 1;
    public static int ImageHeight = 1;
    public static int Left;
    public static int Top;
    public static int Width = 1;
    public static int Height = 1;
    public static int Choice;
    public static int PointerX;
    public static int PointerY;
    public static bool PointerSeen;
    public static int MarkX;
    public static int MarkY;
    public static bool Marked;
}

internal static class InputDesktop
{
    [ThreadStatic] private static nint _open;
    [ThreadStatic] private static string _name = "";

    public static void AttachStation()
    {
        var station = OpenWindowStation("winsta0", false, 0x37F);
        if (station != 0)
            SetProcessWindowStation(station);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenWindowStation(string name, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(nint station);

    public static bool CanOpen(string name)
    {
        var desktop = OpenDesktop(name, 0, false, 0x01FF);
        if (desktop == 0)
            return false;
        CloseDesktop(desktop);
        return true;
    }

    public static string Attached => _name;

    public static void Release()
    {
        var open = _open;
        _open = 0;
        _name = "";
        if (open != 0)
            CloseDesktop(open);
    }

    public static string Peek()
    {
        var input = OpenInputDesktop(0, false, 0x01FF);
        if (input == 0)
            return "";
        var name = NameOf(input);
        CloseDesktop(input);
        return name;
    }

    public static bool AttachOnce()
    {
        Release();
        var input = OpenInputDesktop(0, false, 0x01FF);
        if (input == 0)
            input = OpenDesktop("Winlogon", 0, false, 0x01FF);
        if (input == 0)
            return false;
        if (!SetThreadDesktop(input))
        {
            CloseDesktop(input);
            return false;
        }
        _open = input;
        _name = NameOf(input);
        return true;
    }

    private static string NameOf(nint desktop)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!GetUserObjectInformation(desktop, 2, buffer, 256, out _))
                return "";
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenDesktop(string name, uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, nint info, int length, out int needed);
}

internal static class SessionLaunch
{
    public static nint StartHelper(string pipeName, string desktop, bool fullFrame = false)
    {
        EnablePrivilege("SeAssignPrimaryTokenPrivilege");
        EnablePrivilege("SeIncreaseQuotaPrivilege");
        EnablePrivilege("SeTcbPrivilege");
        var process = StartScreen(pipeName, desktop, asSystem: true, fullFrame);
        if (process != 0)
            return process;
        if (desktop.Contains("Winlogon", StringComparison.OrdinalIgnoreCase))
            return 0;
        return StartScreen(pipeName, desktop, asSystem: false, fullFrame);
    }

    public static void StopProcess(nint process)
    {
        if (process == 0)
            return;
        TerminateProcess(process, 0);
        CloseHandle(process);
    }

    public static bool ConsoleUserPresent()
    {
        EnablePrivilege("SeTcbPrivilege");
        var session = WTSGetActiveConsoleSessionId();
        if (session is 0 or 0xFFFFFFFF)
            return false;
        if (!WTSQueryUserToken(session, out var token))
            return false;
        CloseHandle(token);
        return true;
    }

    public static void LogoffConsole()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session is 0 or 0xFFFFFFFF)
            return;
        WTSLogoffSession(IntPtr.Zero, session, false);
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(nint server, uint sessionId, bool wait);

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0028, out var token))
            return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
                return;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x00000002 };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges privileges, uint length, nint previous, nint returned);

    private static nint StartScreen(string pipeName, string desktop, bool asSystem, bool fullFrame)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return 0;
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF)
            return 0;
        nint token = 0;
        var closeToken = false;
        if (asSystem)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x02000000, out token))
                return 0;
            closeToken = true;
        }
        else if (!WTSQueryUserToken(session, out token))
        {
            return 0;
        }
        else
        {
            var elevated = Elevated(token);
            if (elevated != token)
            {
                CloseHandle(token);
                token = elevated;
            }
            closeToken = true;
        }
        try
        {
            if (!DuplicateTokenEx(token, 0x10000000, IntPtr.Zero, 2, 1, out var primary))
                return 0;
            try
            {
                if (asSystem && !SetTokenInformation(primary, 12, ref session, sizeof(uint)))
                    return 0;
                var start = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = desktop };
                var command = "\"" + exe + "\" --screen " + pipeName + (fullFrame ? " full" : "");
                if (!CreateProcessAsUser(primary, null, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref start, out var info))
                    return 0;
                CloseHandle(info.Thread);
                return info.Process;
            }
            finally
            {
                CloseHandle(primary);
            }
        }
        finally
        {
            if (closeToken)
                CloseHandle(token);
        }
    }

    public static nint StartJob(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return 0;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return 0;
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out var token))
            return 0;
        var launchToken = Elevated(token);
        try
        {
            if (!DuplicateTokenEx(launchToken, 0x10000000, IntPtr.Zero, 2, 1, out var primary))
                return 0;
            try
            {
                var start = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
                var command = "\"" + exe + "\" --job \"" + title.Replace("\"", "") + "\"";
                if (!CreateProcessAsUser(primary, null, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref start, out var info))
                    return 0;
                CloseHandle(info.Thread);
                return info.Process;
            }
            finally
            {
                CloseHandle(primary);
            }
        }
        finally
        {
            if (launchToken != token)
                CloseHandle(launchToken);
            CloseHandle(token);
        }
    }

    public static void CloseJob(nint process)
    {
        if (process == 0)
            return;
        TerminateProcess(process, 0);
        CloseHandle(process);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint process, uint code);

    public static void SendSecureAttention()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session != 0xFFFFFFFF && IsRemoteDesktopSession(session))
        {
            LaunchInSession(session, "--rdp-sas");
            return;
        }

        // SendSAS only succeeds on this thread while it is still the WorkshopLink
        // service. Impersonating the signed-in user first makes the call a no-op.
        LoadSendSas()(false);
    }

    private static SendSasDelegate LoadSendSas()
    {
        var module = LoadLibrary(Path.Combine(Environment.SystemDirectory, "sas.dll"));
        if (module == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var address = GetProcAddress(module, "SendSAS");
        if (address == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return Marshal.GetDelegateForFunctionPointer<SendSasDelegate>(address);
    }

    private delegate void SendSasDelegate(bool asUser);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibrary(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string name);

    public static void SendRdpSecureSequence()
    {
        var keys = new ScanKey[6];
        keys[0] = Scan(0x1D, up: false, extended: false);
        keys[1] = Scan(0x38, up: false, extended: false);
        keys[2] = Scan(0x53, up: false, extended: true);
        keys[3] = Scan(0x53, up: true, extended: true);
        keys[4] = Scan(0x38, up: true, extended: false);
        keys[5] = Scan(0x1D, up: true, extended: false);
        SendInput(6, keys, Marshal.SizeOf<ScanKey>());
    }

    private static ScanKey Scan(ushort code, bool up, bool extended)
    {
        var flags = 0x0008u;
        if (extended)
            flags |= 0x0001u;
        if (up)
            flags |= 0x0002u;
        return new ScanKey { Type = 1, Scan = code, Flags = flags };
    }

    private static bool IsRemoteDesktopSession(uint session)
    {
        if (!WTSQuerySessionInformation(0, session, 16, out var buffer, out _))
            return false;
        try
        {
            return Marshal.ReadInt16(buffer) == 2;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static void LaunchInSession(uint session, string argument)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !WTSQueryUserToken(session, out var token))
            return;
        try
        {
            if (!DuplicateTokenEx(token, 0x10000000, IntPtr.Zero, 2, 1, out var primary))
                return;
            try
            {
                var start = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
                var command = "\"" + exe + "\" " + argument;
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

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct ScanKey
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ScanKey[] inputs, int bytes);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(nint server, uint session, int information, out nint buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    public static void CallSendSas() => SendSAS(false);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ImpersonateLoggedOnUser(nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();

    [DllImport("sas.dll")]
    private static extern void SendSAS(bool asUser);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(nint token, int information, ref uint sessionId, int length);

    private static nint Elevated(nint token)
    {
        int kind;
        int returned;
        if (!GetTokenInformation(token, 18, out kind, 4, out returned))
            return token;
        if (kind != 3)
            return token;
        nint linked;
        if (!GetTokenInformation(token, 19, out linked, IntPtr.Size, out returned))
            return token;
        return linked;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int information, out int value, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int information, out nint value, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint existing, uint access, nint attributes, int level, int type, out nint primary);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(nint token, string? application, string command, nint process, nint thread, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo start, out ProcessInfo processInfo);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

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
        public int XCount;
        public int YCount;
        public int Fill;
        public int Flags;
        public short Show;
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
}

internal static class ConsoleSession
{
    public const int Logon = 0x5;
    public const int Logoff = 0x6;
    public const int DesktopReady = 0xF;
    public static event Action<int, uint>? Changed;

    private static int _ready;
    private static WndProc? _proc;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _ready, 1) != 0)
            return;
        var thread = new Thread(Pump) { IsBackground = true, Name = "WorkshopLinkSession" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Pump()
    {
        _proc = Dispatch;
        var cls = new WndClass
        {
            WndProc = _proc,
            Instance = GetModuleHandle(null),
            ClassName = "WorkshopLinkSessionEvents"
        };
        RegisterClass(ref cls);
        var hwnd = CreateWindowEx(0, cls.ClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, cls.Instance, IntPtr.Zero);
        if (hwnd != 0)
            WTSRegisterSessionNotification(hwnd, 1);
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private static nint Dispatch(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x02B1)
        {
            try { Changed?.Invoke((int)wParam, (uint)lParam); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public WndProc? WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? Menu;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(nint window, int flags);
}
