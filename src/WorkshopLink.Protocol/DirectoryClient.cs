using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WorkshopLink.Protocol;

public sealed class DirectoryClient : IAsyncDisposable
{
    private readonly DirectorySettings _settings;
    private readonly string _role;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _pipes = new();
    private readonly Dictionary<string, DirectoryPipe> _openPipes = new(StringComparer.OrdinalIgnoreCase);
    private ClientWebSocket? _socket;
    private string _iceTicket = "";
    private bool _admitted;

    public DirectoryClient(DirectorySettings settings, string role)
    {
        _settings = settings;
        _role = role;
    }

    public IReadOnlyList<BeaconMessage> Peers { get; private set; } = [];
    public string Status { get; private set; } = "The directory is starting.";

    public event Action<string, string>? SignalReceived;
    public event Action<string, string, bool>? PipeReceived;
    public event Action<string>? SasReceived;
    public event Action<string>? StatusChanged;
    public event Action<string, string, string>? ConsoleWaiting;
    public event Action<string, string, string>? AgentWaiting;

    private void Note(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public async Task<string> IceTicketAsync(CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        while (string.IsNullOrEmpty(_iceTicket))
        {
            if (Environment.TickCount64 - started > 15_000)
                throw new InvalidOperationException("The directory has not admitted this computer.");
            await Task.Delay(200, cancellationToken);
        }

        return _iceTicket;
    }

    public async Task RunAsync(BeaconMessage self, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
            return;
        NetPreference.PreferIpv4();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                socket.Options.Proxy = HttpClient.DefaultProxy;
                var room = _settings.Url.TrimEnd('/') + "/room/" + _settings.Site;
                using var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectLimit.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(new Uri(room.Replace("https://", "wss://").Replace("http://", "ws://")), connectLimit.Token);
                _socket = socket;
                _iceTicket = "";
                _admitted = false;
                var buffer = new byte[262144];
                var hello = await ReceiveAsync(socket, buffer, cancellationToken);
                if (hello is null)
                    throw new InvalidOperationException("The directory closed before it asked for this computer's key.");
                var nonce = ReadNonce(hello);
                var announce = JsonSerializer.Serialize(new
                {
                    type = "announce",
                    id = self.Id,
                    hostname = self.Hostname,
                    os = self.Os,
                    arch = self.Arch,
                    user = self.User,
                    ip = self.Ip,
                    wan = self.Wan,
                    warp = self.Warp,
                    version = self.Version,
                    cpu = self.Cpu.ToString(),
                    ram = self.Ram.ToString(),
                    disk = self.Disk.ToString(),
                    up = self.Up,
                    protect = self.Protect,
                    mac = self.Mac ?? "",
                    publicKey = PeerIdentity.PublicKey,
                    signature = PeerIdentity.Sign(nonce),
                    role = _role,
                    place = self.Place == "offsite" ? "offsite" : "lan",
                    group = self.Group ?? ""
                });
                await SendAsync(socket, announce, cancellationToken);
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var text = await ReceiveAsync(socket, buffer, cancellationToken);
                    if (text is null)
                        break;
                    Handle(socket, text);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Note(ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task SasAsync(string to, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return;
        await SendAsync(socket, JsonSerializer.Serialize(new { type = "sas", to }), cancellationToken);
    }

    public async Task EnrollAsync(string id, string publicKey, bool console, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("The directory is not connected.");
        await SendAsync(socket, JsonSerializer.Serialize(new
        {
            type = "enroll",
            id,
            publicKey,
            role = console ? "console" : "agent"
        }), cancellationToken);
    }

    public async Task SignalAsync(string to, string payload, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("The directory is not connected.");
        await SendAsync(socket, JsonSerializer.Serialize(new { type = "signal", to, payload }), cancellationToken);
    }

    private ClientWebSocket? ConnectedSocket(TimeSpan wait)
    {
        var until = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            var socket = _socket;
            if (_admitted && socket is not null && socket.State == WebSocketState.Open)
                return socket;
            if (Environment.TickCount64 >= until)
                return null;
            Thread.Sleep(250);
        }
    }

    public DirectoryPipe OpenPipe(string to)
    {
        if (string.IsNullOrWhiteSpace(to))
            throw new InvalidOperationException("That PC has not joined the directory.");
        var socket = ConnectedSocket(TimeSpan.FromSeconds(12));
        if (socket is null)
            throw new InvalidOperationException("The directory is not connected.");
        var pipe = new DirectoryPipe(this, to);
        DirectoryPipe? previous;
        lock (_pipes)
        {
            _openPipes.TryGetValue(to, out previous);
            _openPipes[to] = pipe;
        }

        previous?.Dispose();
        return pipe;
    }

    internal void ReleasePipe(DirectoryPipe pipe)
    {
        lock (_pipes)
        {
            if (_openPipes.TryGetValue(pipe.PeerId, out var current) && ReferenceEquals(current, pipe))
                _openPipes.Remove(pipe.PeerId);
        }
    }

    public void PostHealth(int cpu, int ram, int disk, string up, string protect)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return;
        var text = JsonSerializer.Serialize(new
        {
            type = "health",
            cpu = cpu.ToString(),
            ram = ram.ToString(),
            disk = disk.ToString(),
            up,
            protect
        });
        _ = SendQuiet(socket, text);
    }

    private async Task SendQuiet(ClientWebSocket socket, string text)
    {
        try
        {
            await SendAsync(socket, text, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private static int ReadNumber(JsonElement peer, string name)
    {
        if (!peer.TryGetProperty(name, out var value))
            return 0;
        return value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : int.TryParse(value.GetString(), out var parsed) ? parsed : 0;
    }

    public async Task PipeAsync(string to, string payload, bool close, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("The directory is not connected.");
        await SendAsync(socket, JsonSerializer.Serialize(new { type = "pipe", to, payload, close = close ? "1" : "" }), cancellationToken);
    }

    private static string ReadNonce(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "challenge"
            || !root.TryGetProperty("nonce", out var nonce)
            || string.IsNullOrWhiteSpace(nonce.GetString()))
            throw new InvalidOperationException("The directory did not ask for this computer's key.");
        return nonce.GetString()!;
    }

    private void Handle(ClientWebSocket socket, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : "";
            if (type == "admitted")
            {
                _admitted = true;
                _iceTicket = root.TryGetProperty("ticket", out var ticket) ? ticket.GetString() ?? "" : "";
                Note("connected");
                return;
            }

            if (type == "pending")
            {
                var role = root.TryGetProperty("role", out var roleValue) ? roleValue.GetString() ?? "" : "";
                var pendingId = root.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "" : "";
                var pendingKey = root.TryGetProperty("publicKey", out var keyValue) ? keyValue.GetString() ?? "" : "";
                var pendingHost = root.TryGetProperty("hostname", out var hostValue) ? hostValue.GetString() ?? "" : "";
                var pendingUser = root.TryGetProperty("user", out var userValue) ? userValue.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(pendingUser)
                    && !pendingUser.Equals("browser", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(pendingHost) || pendingHost.Equals("Web command center", StringComparison.OrdinalIgnoreCase)))
                    pendingHost = pendingUser;
                else if (!string.IsNullOrWhiteSpace(pendingUser)
                    && !pendingUser.Equals("browser", StringComparison.OrdinalIgnoreCase)
                    && !pendingHost.Contains(pendingUser, StringComparison.OrdinalIgnoreCase))
                    pendingHost = pendingUser + " · " + pendingHost;
                if (pendingId.Length > 0 && pendingKey.Length > 0 && role == "console")
                    ConsoleWaiting?.Invoke(pendingId, pendingKey, pendingHost);
                else if (pendingId.Length > 0 && pendingKey.Length > 0 && role != "console")
                    AgentWaiting?.Invoke(pendingId, pendingKey, pendingHost);
                else if (_role != "console")
                    Note("Waiting for the command center to admit this computer.");
                return;
            }

            if (type == "rejected")
            {
                var reason = root.TryGetProperty("error", out var error) ? error.GetString() : "";
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
                    ? "The directory rejected this computer's key."
                    : reason);
            }

            if (type == "sas" && root.TryGetProperty("from", out var sasFrom))
            {
                SasReceived?.Invoke(sasFrom.GetString() ?? "");
                return;
            }

            if (type == "signal"
                && root.TryGetProperty("from", out var from)
                && root.TryGetProperty("payload", out var payload))
            {
                SignalReceived?.Invoke(from.GetString() ?? "", payload.GetString() ?? "");
                return;
            }

            if (type == "pipe" && root.TryGetProperty("from", out var pipeFrom))
            {
                var pipePayload = root.TryGetProperty("payload", out var pipeBody) ? pipeBody.GetString() ?? "" : "";
                var pipeClose = root.TryGetProperty("close", out var pipeCloseBody) && pipeCloseBody.GetString() == "1";
                PipeReceived?.Invoke(pipeFrom.GetString() ?? "", pipePayload, pipeClose);
                return;
            }

            if (!root.TryGetProperty("peers", out var peers) || peers.ValueKind != JsonValueKind.Array)
                return;
            var list = new List<BeaconMessage>();
            foreach (var peer in peers.EnumerateArray())
            {
                var host = peer.TryGetProperty("hostname", out var hostValue) ? hostValue.GetString() ?? "" : "";
                var user = peer.TryGetProperty("user", out var userValue) ? userValue.GetString() ?? "" : "";
                if (string.Equals(user, "browser", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(host, "Web command center", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(new BeaconMessage
                {
                    Hostname = host,
                    Os = peer.TryGetProperty("os", out var os) ? os.GetString() ?? "" : "",
                    Arch = peer.TryGetProperty("arch", out var arch) ? arch.GetString() ?? "" : "",
                    User = user,
                    Ip = peer.GetProperty("ip").GetString() ?? "",
                    Wan = peer.TryGetProperty("wan", out var wan) ? wan.GetString() ?? "" : "",
                    Warp = peer.TryGetProperty("warp", out var warp) ? warp.GetString() ?? "" : "",
                    Id = peer.GetProperty("id").GetString() ?? "",
                    Version = peer.GetProperty("version").GetString() ?? "",
                    Place = peer.TryGetProperty("place", out var place) ? place.GetString() ?? "lan" : "lan",
                    Group = peer.TryGetProperty("group", out var groupKey) ? groupKey.GetString() ?? "" : "",
                    Cpu = ReadNumber(peer, "cpu"),
                    Ram = ReadNumber(peer, "ram"),
                    Disk = ReadNumber(peer, "disk"),
                    Up = peer.TryGetProperty("up", out var up) ? up.GetString() ?? "" : "",
                    Protect = peer.TryGetProperty("protect", out var protect) ? protect.GetString() ?? "" : "",
                    Mac = peer.TryGetProperty("mac", out var mac) ? mac.GetString() : null,
                    Link = true,
                    Capabilities = "desktop,inventory"
                });
            }
            Peers = list;
        }
        catch (JsonException)
        {
        }
    }

    private async Task SendAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _send.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _send.Release();
        }
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is not null)
            _socket.Dispose();
        _send.Dispose();
        await Task.CompletedTask;
    }
}
