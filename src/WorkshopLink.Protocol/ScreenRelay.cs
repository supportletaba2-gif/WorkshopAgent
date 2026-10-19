using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WorkshopLink.Protocol;

public sealed class ScreenRelay : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private TaskCompletionSource<long>? _pong;
    private readonly TaskCompletionSource<bool> _peer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _send = new(1, 1);

    public long RoundTripMs { get; private set; } = long.MaxValue;
    public bool ViewerAcceptsH264 { get; private set; }
    public event Action<byte, byte[]>? Input;

    public async Task ConnectAsync(string room, string role, CancellationToken cancellationToken)
    {
        var address = LinkConstants.ScreenUrl.TrimEnd('/') + "/room/" + room + "?role=" + role;
        await _socket.ConnectAsync(new Uri(address), cancellationToken);
        var hello = await ReceiveTextAsync(cancellationToken);
        using var document = JsonDocument.Parse(hello);
        var nonce = document.RootElement.GetProperty("nonce").GetString() ?? "";
        var announce = JsonSerializer.Serialize(new
        {
            type = "announce",
            role,
            id = PeerIdentity.Fingerprint,
            publicKey = PeerIdentity.PublicKey,
            signature = PeerIdentity.Sign(nonce)
        });
        await SendTextAsync(announce, cancellationToken);
        _ = Task.Run(() => ReadAsync(cancellationToken), cancellationToken);
        await Task.Delay(200, cancellationToken);
    }

    public Task WaitForViewerAsync(CancellationToken cancellationToken) =>
        _peer.Task.WaitAsync(cancellationToken);

    public async Task<long> MeasureAsync(CancellationToken cancellationToken)
    {
        var tick = Environment.TickCount64;
        var wait = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pong = wait;
        await SendTextAsync(JsonSerializer.Serialize(new { type = "ping", t = tick }), cancellationToken);
        RoundTripMs = await wait.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        return RoundTripMs;
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                    continue;

                var text = Encoding.UTF8.GetString(message.ToArray());
                using var document = JsonDocument.Parse(text);
                var type = document.RootElement.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : "";
                if (type == "input" && document.RootElement.TryGetProperty("kind", out var kindValue) && document.RootElement.TryGetProperty("body", out var bodyValue))
                {
                    try
                    {
                        Input?.Invoke((byte)kindValue.GetInt32(), Convert.FromBase64String(bodyValue.GetString() ?? ""));
                    }
                    catch (FormatException)
                    {
                    }
                }
                else if (type == "codecs")
                {
                    ViewerAcceptsH264 = document.RootElement.TryGetProperty("h264", out var h264) && h264.ValueKind == JsonValueKind.True;
                }
                else if (type == "peer")
                {
                    if (!document.RootElement.TryGetProperty("gone", out _))
                        _peer.TrySetResult(true);
                }
                else if (type == "ping" && document.RootElement.TryGetProperty("t", out var tick))
                {
                    await SendTextAsync(JsonSerializer.Serialize(new { type = "pong", t = tick.GetInt64() }), cancellationToken);
                }
                else if (type == "pong" && document.RootElement.TryGetProperty("t", out var sent))
                {
                    var rtt = Math.Max(1, Environment.TickCount64 - sent.GetInt64());
                    RoundTripMs = rtt;
                    _pong?.TrySetResult(rtt);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
        {
        }
    }

    private async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
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

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
        _socket.Dispose();
        _send.Dispose();
    }
}
