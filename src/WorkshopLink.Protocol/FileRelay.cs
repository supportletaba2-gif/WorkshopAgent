using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WorkshopLink.Protocol;

public sealed record PasteOffer(string Transfer, string Name, long Size, int Parts, string From);

public sealed class FileRelay : IAsyncDisposable
{
    public event Action<string, string, long>? Began;
    public event Action<string, bool, byte[]>? Piece;

    private readonly ClientWebSocket _desk = new();
    private readonly ClientWebSocket _pipe = new();
    private readonly SemaphoreSlim _pipeSend = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private Task? _pipeRead;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim _send = new(1, 1);
    private string _token = "";
    private string _room = "";
    private Task? _read;

    public event Action<PasteOffer>? PasteOffered;
    public event Action<string>? Accepted;
    public event Action<string>? Declined;
    public event Action<string, int, long>? Progress;
    public event Action<string, string, long>? Ready;

    public async Task ConnectAsync(string room, string role, CancellationToken cancellationToken)
    {
        _room = room;
        await OpenAsync(_desk, LinkConstants.OversightUrl, room, role, cancellationToken);
        await OpenAsync(_pipe, LinkConstants.FileUrl.Replace("https://", "wss://").Replace("http://", "ws://"), room, role, cancellationToken);
        var token = await OneTextAsync(_pipe, cancellationToken);
        using (var document = JsonDocument.Parse(token))
            _token = document.RootElement.TryGetProperty("token", out var value) ? value.GetString() ?? "" : "";
        _read = Task.Run(() => ReadAsync(cancellationToken), cancellationToken);
        _pipeRead = Task.Run(() => ReadPipeAsync(cancellationToken), cancellationToken);
    }

    public Task OfferAsync(string transfer, string name, long size, CancellationToken cancellationToken)
    {
        var parts = size == 0 ? 1 : (int)Math.Min(10000, (size + LinkConstants.FilePartBytes - 1) / LinkConstants.FilePartBytes);
        return SendAsync(new { type = "offer", transfer, name, size, parts }, cancellationToken);
    }

    public Task AcceptAsync(string transfer, CancellationToken cancellationToken) =>
        SendAsync(new { type = "accept", transfer }, cancellationToken);

    public Task DeclineAsync(string transfer, CancellationToken cancellationToken) =>
        SendAsync(new { type = "decline", transfer }, cancellationToken);

    public string NameOf(string transfer) => _names.TryGetValue(transfer, out var name) ? name : "file";

    public async Task UploadAsync(string transfer, string name, string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        await SendPipeAsync(JsonSerializer.Serialize(new { type = "begin", transfer, name, size = info.Length }), cancellationToken);
        await using var input = File.OpenRead(path);
        if (info.Length == 0)
        {
            await SendPipeAsync(Frame(transfer, true, ReadOnlyMemory<byte>.Empty), cancellationToken);
            return;
        }

        var left = info.Length;
        while (left > 0)
        {
            var count = (int)Math.Min(256 * 1024, left);
            var buffer = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                var read = await input.ReadAsync(buffer.AsMemory(filled, count - filled), cancellationToken);
                if (read == 0)
                    break;
                filled += read;
            }

            left -= filled;
            await SendPipeAsync(Frame(transfer, left == 0, buffer.AsMemory(0, filled)), cancellationToken);
        }
    }

    public async Task DownloadAsync(string transfer, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LinkConstants.FileUrl.TrimEnd('/') + "/room/" + _room + "/transfer/" + transfer);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(path);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private async Task PutAsync(string transfer, string name, int part, bool last, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, LinkConstants.FileUrl.TrimEnd('/') + "/room/" + _room + "/transfer/" + transfer + "/part/" + part);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.TryAddWithoutValidation("X-Last-Part", last ? "1" : "0");
        request.Headers.TryAddWithoutValidation("X-File-Name", name);
        request.Content = new ByteArrayContent(body.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task OpenAsync(ClientWebSocket socket, string root, string room, string role, CancellationToken cancellationToken)
    {
        var address = root.TrimEnd('/') + "/room/" + room + "?role=" + role;
        await socket.ConnectAsync(new Uri(address), cancellationToken);
        var hello = JsonDocument.Parse(await OneTextAsync(socket, cancellationToken));
        var nonce = hello.RootElement.GetProperty("nonce").GetString() ?? "";
        var announce = JsonSerializer.Serialize(new
        {
            type = "announce",
            role,
            id = PeerIdentity.Fingerprint,
            publicKey = PeerIdentity.PublicKey,
            signature = PeerIdentity.Sign(nonce)
        });
        var bytes = Encoding.UTF8.GetBytes(announce);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        if (socket != _desk)
            return;
        var ready = JsonDocument.Parse(await OneTextAsync(socket, cancellationToken));
        if (ready.RootElement.TryGetProperty("type", out var type) && type.GetString() == "rejected")
            throw new InvalidOperationException("The file desk was rejected.");
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_desk.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var document = JsonDocument.Parse(await OneTextAsync(_desk, cancellationToken));
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var kind) ? kind.GetString() : "";
                var transfer = root.TryGetProperty("transfer", out var id) ? id.GetString() ?? "" : "";
                if (type == "paste")
                {
                    PasteOffered?.Invoke(new PasteOffer(
                        transfer,
                        root.TryGetProperty("name", out var name) ? name.GetString() ?? "file" : "file",
                        root.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                        root.TryGetProperty("parts", out var parts) ? parts.GetInt32() : 1,
                        root.TryGetProperty("from", out var from) ? from.GetString() ?? "" : ""));
                }
                else if (type == "accepted")
                    Accepted?.Invoke(transfer);
                else if (type == "declined")
                    Declined?.Invoke(transfer);
                else if (type == "progress")
                {
                    Progress?.Invoke(
                        root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        root.TryGetProperty("part", out var part) ? part.GetInt32() : 0,
                        root.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
                }
                else if (type == "ready")
                {
                    Ready?.Invoke(transfer, root.TryGetProperty("name", out var name) ? name.GetString() ?? "file" : "file", root.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
        {
        }
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _send.WaitAsync(cancellationToken);
        try
        {
            await _desk.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _send.Release();
        }
    }

    private static async Task<string> OneTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    private async Task ReadPipeAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024 + 64];
        try
        {
            while (_pipe.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                var binary = false;
                do
                {
                    result = await _pipe.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    binary = result.MessageType == WebSocketMessageType.Binary;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var raw = message.ToArray();
                if (!binary)
                {
                    using var document = JsonDocument.Parse(raw);
                    var root = document.RootElement;
                    if (root.TryGetProperty("type", out var kind) && kind.GetString() == "begin")
                    {
                        var transfer = root.TryGetProperty("transfer", out var id) ? id.GetString() ?? "" : "";
                        var name = root.TryGetProperty("name", out var file) ? file.GetString() ?? "file" : "file";
                        var size = root.TryGetProperty("size", out var bytes) ? bytes.GetInt64() : 0;
                        _names[transfer] = Path.GetFileName(name);
                        Began?.Invoke(transfer, _names[transfer], size);
                    }
                    continue;
                }

                if (raw.Length < 33 || raw[0] is not (1 or 2))
                    continue;
                var transferId = Encoding.ASCII.GetString(raw, 1, 32);
                var data = raw.Length == 33 ? [] : raw[33..];
                Piece?.Invoke(transferId, raw[0] == 2, data);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
        {
        }
    }

    private async Task SendPipeAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _pipeSend.WaitAsync(cancellationToken);
        try
        {
            await _pipe.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _pipeSend.Release();
        }
    }

    private async Task SendPipeAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _pipeSend.WaitAsync(cancellationToken);
        try
        {
            await _pipe.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
        }
        finally
        {
            _pipeSend.Release();
        }
    }

    private static byte[] Frame(string transfer, bool last, ReadOnlyMemory<byte> data)
    {
        var id = transfer.Length >= 32 ? transfer[..32] : transfer.PadLeft(32, '0');
        var frame = new byte[33 + data.Length];
        frame[0] = last ? (byte)2 : (byte)1;
        Encoding.ASCII.GetBytes(id).CopyTo(frame, 1);
        data.Span.CopyTo(frame.AsSpan(33));
        return frame;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_desk.State == WebSocketState.Open)
                await _desk.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { }
        try
        {
            if (_pipe.State == WebSocketState.Open)
                await _pipe.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { }
        _desk.Dispose();
        _pipe.Dispose();
        _http.Dispose();
        _send.Dispose();
        _pipeSend.Dispose();
        if (_read is not null)
            await Task.WhenAny(_read, Task.Delay(200));
        if (_pipeRead is not null)
            await Task.WhenAny(_pipeRead, Task.Delay(200));
    }
}
