using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using SIPSorcery.Net;

namespace WorkshopLink.Protocol;

public sealed class RelaySession : IAsyncDisposable
{
    private readonly TaskCompletionSource<Stream> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RTCIceCandidateInit> _early = [];
    private DirectoryClient? _directory;
    private RTCPeerConnection? _peer;
    private string _signalTo = "";
    private bool _remoteReady;
    private DataChannelStream? _stream;
    private RTCDataChannel? _picture;
    private readonly Channel<byte[]> _pictures = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    public Task<Stream> Opened => _opened.Task;

    public static async Task<RTCConfiguration> IceConfigurationAsync(DirectorySettings settings, bool relayOnly, string ticket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            throw new InvalidOperationException("The directory has not admitted this computer.");
        NetPreference.PreferIpv4();
        var root = settings.Url
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        var address = root + "/ice?site=" + Uri.EscapeDataString(settings.Site) + "&ticket=" + Uri.EscapeDataString(ticket);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await http.GetAsync(address, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var servers = new List<RTCIceServer>();
        if (document.RootElement.TryGetProperty("iceServers", out var list))
        {
            foreach (var entry in list.EnumerateArray())
            {
                var urls = new List<string>();
                if (entry.TryGetProperty("urls", out var urlValue))
                {
                    if (urlValue.ValueKind == JsonValueKind.String)
                        urls.Add(urlValue.GetString() ?? "");
                    else if (urlValue.ValueKind == JsonValueKind.Array)
                        urls.AddRange(urlValue.EnumerateArray().Select(item => item.GetString() ?? ""));
                }

                var username = entry.TryGetProperty("username", out var user) ? user.GetString() : null;
                var credential = entry.TryGetProperty("credential", out var secret) ? secret.GetString() : null;
                foreach (var url in urls.Where(item => item.Length > 0).OrderBy(Rank))
                {
                    servers.Add(new RTCIceServer
                    {
                        urls = url,
                        username = username,
                        credential = credential
                    });
                }
            }
        }

        if (servers.Count == 0)
            servers.Add(new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" });

        return new RTCConfiguration
        {
            iceServers = servers,
            iceTransportPolicy = relayOnly ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all,
            X_GatherTimeoutMs = 8000
        };
    }

    public async Task OfferAsync(DirectoryClient directory, string peerId, DirectorySettings settings, bool relayOnly, CancellationToken cancellationToken)
    {
        _directory = directory;
        _signalTo = peerId;
        directory.SignalReceived += OnSignal;
        var ticket = await directory.IceTicketAsync(cancellationToken);
        _peer = new RTCPeerConnection(await IceConfigurationAsync(settings, relayOnly, ticket, cancellationToken));
        WatchCandidates();
        var channel = await _peer.createDataChannel("screen", new RTCDataChannelInit { ordered = true });
        Attach(channel);
        var picture = await _peer.createDataChannel("picture", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
        Attach(picture);
        var offer = _peer.createOffer();
        await _peer.setLocalDescription(offer);
        await WaitForGatheringAsync(_peer, cancellationToken);
        await directory.SignalAsync(peerId, JsonSerializer.Serialize(new { kind = "offer", sdp = _peer.localDescription.sdp.ToString() }), cancellationToken);
    }

    public async Task AcceptAsync(DirectoryClient directory, string from, string sdp, DirectorySettings settings, bool relayOnly, CancellationToken cancellationToken)
    {
        _directory = directory;
        _signalTo = from;
        var ticket = await directory.IceTicketAsync(cancellationToken);
        _peer = new RTCPeerConnection(await IceConfigurationAsync(settings, relayOnly, ticket, cancellationToken));
        WatchCandidates();
        _peer.ondatachannel += Attach;
        var remote = _peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
        if (remote != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException("The screen offer was not accepted.");
        MarkRemoteReady();
        var answer = _peer.createAnswer();
        await _peer.setLocalDescription(answer);
        await WaitForGatheringAsync(_peer, cancellationToken);
        await directory.SignalAsync(from, JsonSerializer.Serialize(new { kind = "answer", sdp = _peer.localDescription.sdp.ToString() }), cancellationToken);
    }

    public bool TrySendPicture(byte[] data)
    {
        var channel = _picture;
        if (channel is null || channel.readyState != RTCDataChannelState.open || data.Length == 0 || channel.bufferedAmount > 500_000)
            return false;
        try
        {
            channel.send(data);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<byte[]> ReadPictureAsync(CancellationToken cancellationToken) =>
        _pictures.Reader.ReadAsync(cancellationToken).AsTask();

    public void AddCandidate(string candidate, string? sdpMid, ushort? line)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;
        var init = new RTCIceCandidateInit { candidate = candidate, sdpMid = sdpMid, sdpMLineIndex = line ?? 0 };
        lock (_early)
        {
            if (_peer is null || !_remoteReady)
            {
                _early.Add(init);
                return;
            }
        }

        _peer.addIceCandidate(init);
    }

    private void WatchCandidates()
    {
        _peer!.onicecandidate += candidate =>
        {
            if (string.IsNullOrWhiteSpace(candidate?.candidate) || _directory is null || string.IsNullOrWhiteSpace(_signalTo))
                return;
            var payload = JsonSerializer.Serialize(new
            {
                kind = "candidate",
                candidate = candidate.candidate,
                sdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex
            });
            _ = _directory.SignalAsync(_signalTo, payload, CancellationToken.None);
        };
    }

    private void MarkRemoteReady()
    {
        RTCIceCandidateInit[] waiting;
        lock (_early)
        {
            _remoteReady = true;
            waiting = _early.ToArray();
            _early.Clear();
        }

        if (_peer is null)
            return;
        foreach (var candidate in waiting)
            _peer.addIceCandidate(candidate);
    }

    private void OnSignal(string from, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : "";
            if (kind == "answer" && root.TryGetProperty("sdp", out var sdp))
            {
                _peer?.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp.GetString() });
                MarkRemoteReady();
            }
            else if (kind == "candidate" && root.TryGetProperty("candidate", out var candidate))
            {
                ushort? line = null;
                if (root.TryGetProperty("sdpMLineIndex", out var index) && index.TryGetUInt16(out var value))
                    line = value;
                var mid = root.TryGetProperty("sdpMid", out var midValue) ? midValue.GetString() : null;
                AddCandidate(candidate.GetString() ?? "", mid, line);
            }
        }
        catch (JsonException)
        {
        }
    }

    private void Attach(RTCDataChannel channel)
    {
        if (string.Equals(channel.label, "picture", StringComparison.Ordinal))
        {
            _picture = channel;
            channel.onmessage += (_, _, data) => _pictures.Writer.TryWrite(data);
            return;
        }

        _stream = new DataChannelStream(channel);
        channel.onopen += () => _opened.TrySetResult(_stream);
        channel.onclose += () => _stream.End();
        if (channel.readyState == RTCDataChannelState.open)
            _opened.TrySetResult(_stream);
    }

    private static async Task WaitForGatheringAsync(RTCPeerConnection peer, CancellationToken cancellationToken)
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
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await done.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            peer.onicegatheringstatechange -= Watch;
        }
    }

    private static int Rank(string url)
    {
        var value = url.ToLowerInvariant();
        if (value.StartsWith("turns:", StringComparison.Ordinal) && value.Contains(":443", StringComparison.Ordinal))
            return 0;
        if (value.StartsWith("turn:", StringComparison.Ordinal) && value.Contains(":443", StringComparison.Ordinal) && value.Contains("transport=tcp", StringComparison.Ordinal))
            return 1;
        if (value.StartsWith("turns:", StringComparison.Ordinal))
            return 2;
        if (value.StartsWith("turn:", StringComparison.Ordinal) && value.Contains("transport=tcp", StringComparison.Ordinal))
            return 3;
        if (value.StartsWith("turn:", StringComparison.Ordinal))
            return 4;
        return 5;
    }

    public async ValueTask DisposeAsync()
    {
        if (_directory is not null)
            _directory.SignalReceived -= OnSignal;
        _opened.TrySetException(new ObjectDisposedException(nameof(RelaySession)));
        _stream?.End();
        if (_peer is not null)
            _peer.Close("screen closed");
        await Task.CompletedTask;
    }

    private sealed class DataChannelStream : Stream
    {
        private readonly RTCDataChannel _channel;
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly MemoryStream _outgoing = new();
        private byte[] _current = [];
        private int _offset;

        public DataChannelStream(RTCDataChannel channel)
        {
            _channel = channel;
            channel.onmessage += (_, _, data) => _incoming.Writer.TryWrite(data);
        }

        public void End() => _incoming.Writer.TryComplete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            if (_outgoing.Length == 0)
                return;
            var payload = _outgoing.ToArray();
            _outgoing.SetLength(0);
            const int max = 200_000;
            for (var offset = 0; offset < payload.Length; offset += max)
            {
                var count = Math.Min(max, payload.Length - offset);
                var piece = new byte[count];
                Buffer.BlockCopy(payload, offset, piece, 0, count);
                _channel.send(piece);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                _current = await _incoming.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) => _outgoing.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
