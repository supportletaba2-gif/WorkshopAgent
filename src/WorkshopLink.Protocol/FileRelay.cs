using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WorkshopLink.Protocol;

public sealed record PasteOffer(string Transfer, string Name, long Size, int Parts, string From);

public sealed class FileRelay : IAsyncDisposable
{
    private readonly ClientWebSocket _desk = new();
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
        var tokenSocket = new ClientWebSocket();
        await OpenAsync(tokenSocket, LinkConstants.FileUrl.Replace("https://", "wss://").Replace("http://", "ws://"), room, role, cancellationToken);
        var token = await OneTextAsync(tokenSocket, cancellationToken);
        using (var document = JsonDocument.Parse(token))
            _token = document.RootElement.TryGetProperty("token", out var value) ? value.GetString() ?? "" : "";
        tokenSocket.Dispose();
        _read = Task.Run(() => ReadAsync(cancellationToken), cancellationToken);
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

    public async Task UploadAsync(string transfer, string name, string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        await using var input = File.OpenRead(path);
        if (info.Length == 0)
        {
            await PutAsync(transfer, name, 1, true, ReadOnlyMemory<byte>.Empty, cancellationToken);
            return;
        }

        var part = 1;
        var left = info.Length;
        while (left > 0)
        {
            var count = (int)Math.Min(LinkConstants.FilePartBytes, left);
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
            await PutAsync(transfer, name, part, left == 0, buffer.AsMemory(0, filled), cancellationToken);
            part++;
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

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_desk.State == WebSocketState.Open)
                await _desk.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { }
        _desk.Dispose();
        _http.Dispose();
        _send.Dispose();
        if (_read is not null)
            await Task.WhenAny(_read, Task.Delay(200));
    }
}
