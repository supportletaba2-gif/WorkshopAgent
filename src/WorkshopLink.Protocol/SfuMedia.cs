using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace WorkshopLink.Protocol;

public sealed class SfuMedia : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Channel<byte[]> _pending = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });
    private RTCPeerConnection? _peer;
    private CancellationTokenSource? _pump;
    private string _sessionId = "";
    private bool _canSend;

    public event Action<byte[]>? Frame;
    public string Status { get; private set; } = "The picture track is starting.";

    public void Submit(byte[] annexB)
    {
        if (annexB.Length == 0)
            return;
        _pending.Writer.TryWrite(annexB);
    }

    public async Task PublishAsync(DirectoryClient? directory, string room, CancellationToken cancellationToken)
    {
        await ConnectAsync(room, "source", cancellationToken);
        _peer = new RTCPeerConnection(await ConfigurationAsync(directory, cancellationToken));
        WatchPeer("source");
        var format = new VideoFormat(VideoCodecsEnum.AV1, 41, 90000, "profile=0;level-idx=5;tier=0");
        _peer.addTrack(new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly));
        var offer = _peer.createOffer();
        await _peer.setLocalDescription(offer);
        await GatherAsync(_peer, cancellationToken);
        var sdp = _peer.localDescription?.sdp?.ToString() ?? "";
        Note("source offer candidates " + Count(sdp, "a=candidate") + " mid " + Mid(sdp));
        await SendAsync(new { type = "publish", sdp, mid = Mid(sdp) }, cancellationToken);
        var answer = await WaitAsync("answer", cancellationToken);
        var remote = Text(answer, "sdp");
        if (remote.Length == 0)
            throw new InvalidOperationException(Text(answer, "error").Length == 0 ? "The picture track was not accepted." : Text(answer, "error"));
        var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = remote });
        Note("source answer " + result + " candidates " + Count(remote, "a=candidate"));
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException("The picture answer was rejected.");
        _sessionId = Text(answer, "sessionId");
        _canSend = true;
        Status = "The picture track is published.";
        _pump = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => PumpAsync(_pump.Token), _pump.Token);
    }

    public async Task WatchAsync(DirectoryClient? directory, string room, CancellationToken cancellationToken)
    {
        await ConnectAsync(room, "view", cancellationToken);
        _peer = new RTCPeerConnection(await ConfigurationAsync(directory, cancellationToken));
        WatchPeer("view");
        var format = new VideoFormat(VideoCodecsEnum.AV1, 41, 90000, "profile=0;level-idx=5;tier=0");
        _peer.addTrack(new MediaStreamTrack(format, MediaStreamStatusEnum.RecvOnly));
        _peer.OnVideoFrameReceived += (_, _, frame, _) =>
        {
            if (frame is { Length: > 0 })
                Frame?.Invoke(frame);
        };
        var offer = await WaitForOfferAsync(cancellationToken);
        var remote = Text(offer, "sdp");
        if (remote.Length == 0)
            throw new InvalidOperationException(Text(offer, "error").Length == 0 ? "The picture track is not published yet." : Text(offer, "error"));
        var set = _peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = remote });
        Note("view offer " + set + " candidates " + Count(remote, "a=candidate"));
        if (set != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException("The picture offer was rejected.");
        var answer = _peer.createAnswer(null);
        await _peer.setLocalDescription(answer);
        await GatherAsync(_peer, cancellationToken);
        _sessionId = Text(offer, "sessionId");
        await SendAsync(new { type = "answer", sessionId = _sessionId, sdp = _peer.localDescription?.sdp?.ToString() ?? "" }, cancellationToken);
        Status = "The picture track is connected.";
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = _peer?.connectionState;
                if (!_canSend || state is RTCPeerConnectionState.@new or RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
                {
                    await Task.Delay(20, cancellationToken);
                    continue;
                }

                var frame = await _pending.Reader.ReadAsync(cancellationToken);
                try
                {
                    _peer.SendVideo(6000, frame);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task ConnectAsync(string room, string role, CancellationToken cancellationToken)
    {
        var address = LinkConstants.SignalUrl.TrimEnd('/') + "/room/" + room + "?role=" + role;
        await _socket.ConnectAsync(new Uri(address), cancellationToken);
        var hello = JsonDocument.Parse(await ReceiveAsync(cancellationToken));
        var nonce = hello.RootElement.GetProperty("nonce").GetString() ?? "";
        await SendAsync(new
        {
            type = "announce",
            role,
            id = PeerIdentity.Fingerprint,
            publicKey = PeerIdentity.PublicKey,
            signature = PeerIdentity.Sign(nonce)
        }, cancellationToken);
        var ready = JsonDocument.Parse(await ReceiveAsync(cancellationToken));
        if (ready.RootElement.TryGetProperty("type", out var type) && type.GetString() == "rejected")
            throw new InvalidOperationException("The picture session was rejected.");
    }

    private async Task<JsonDocument> WaitForOfferAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            await SendAsync(new { type = "subscribe" }, cancellationToken);
            var message = await WaitAsync("offer", cancellationToken);
            if (Text(message, "sdp").Length > 0)
                return message;
            var error = Text(message, "error");
            if (!error.Contains("not published", StringComparison.OrdinalIgnoreCase)
                && !error.Contains("track", StringComparison.OrdinalIgnoreCase))
                return message;
            Note("view retry " + error);
            await Task.Delay(400, cancellationToken);
        }

        throw new TimeoutException("The picture track is not published yet.");
    }

    private async Task<JsonDocument> WaitAsync(string type, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var document = JsonDocument.Parse(await ReceiveAsync(cancellationToken));
            var kind = document.RootElement.TryGetProperty("type", out var value) ? value.GetString() : "";
            if (kind == type || kind == "error")
                return document;
        }

        throw new OperationCanceledException();
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _send.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task<string> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The picture session closed.");
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static async Task<RTCConfiguration> ConfigurationAsync(DirectoryClient? directory, CancellationToken cancellationToken)
    {
        if (directory is not null)
        {
            try
            {
                var ticket = await directory.IceTicketAsync(cancellationToken);
                return await RelaySession.IceConfigurationAsync(DirectorySettings.BuiltIn, false, ticket, cancellationToken);
            }
            catch (InvalidOperationException)
            {
            }
        }

        return new RTCConfiguration
        {
            iceServers = [new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" }],
            iceTransportPolicy = RTCIceTransportPolicy.all
        };
    }

    private static async Task GatherAsync(RTCPeerConnection peer, CancellationToken cancellationToken)
    {
        if (peer.iceGatheringState == RTCIceGatheringState.complete)
            return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(RTCIceGatheringState state)
        {
            if (state == RTCIceGatheringState.complete)
                done.TrySetResult();
        }

        peer.onicegatheringstatechange += Watch;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { await done.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        finally { peer.onicegatheringstatechange -= Watch; }
    }

    private static string Mid(string sdp)
    {
        foreach (var line in sdp.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("a=mid:", StringComparison.Ordinal))
                return text[6..];
        }

        return "0";
    }

    private void WatchPeer(string role)
    {
        if (_peer is null)
            return;
        _peer.onconnectionstatechange += state => Note(role + " connection " + state);
        _peer.oniceconnectionstatechange += state => Note(role + " ice " + state);
    }

    private static void Note(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "wlink-sfu.log"), DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    private static string Text(JsonDocument document, string name) =>
        document.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    public async ValueTask DisposeAsync()
    {
        _pump?.Cancel();
        _pending.Writer.TryComplete();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { }
        _peer?.Close("picture closed");
        _socket.Dispose();
        _send.Dispose();
    }
}
