using System.Threading.Channels;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal sealed class PipeHost
{
    private readonly DirectoryClient _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, Seat> _seats = new(StringComparer.OrdinalIgnoreCase);

    public PipeHost(DirectoryClient directory)
    {
        _directory = directory;
        directory.PipeReceived += OnPipe;
    }

    private void OnPipe(string from, string payload, bool close)
    {
        if (string.IsNullOrWhiteSpace(from))
            return;
        byte[]? bytes = null;
        if (payload.Length > 0)
        {
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                return;
            }
        }

        Seat? created = null;
        lock (_gate)
        {
            _seats.TryGetValue(from, out var seat);
            var fresh = bytes is { Length: >= 7 } && bytes.AsSpan(0, LinkSeal.Magic.Length).SequenceEqual(LinkSeal.Magic);
            if (seat is not null && fresh)
            {
                seat.Retire();
                _seats.Remove(from);
                seat = null;
            }

            if (seat is null)
            {
                if (close || bytes is null || bytes.Length == 0)
                    return;
                seat = new Seat(from, this);
                _seats[from] = seat;
                created = seat;
            }

            if (bytes is { Length: > 0 })
                seat.Push(bytes);
            if (close)
            {
                seat.Retire();
                _seats.Remove(from);
            }
        }

        created?.Start();
    }

    private void Drop(Seat seat)
    {
        lock (_gate)
        {
            if (_seats.TryGetValue(seat.From, out var current) && ReferenceEquals(current, seat))
                _seats.Remove(seat.From);
        }
    }

    private async Task SendAsync(Seat seat, byte[] bytes)
    {
        if (seat.Retired)
            return;
        const int max = 18000;
        for (var offset = 0; offset < bytes.Length; offset += max)
        {
            if (seat.Retired)
                return;
            var count = Math.Min(max, bytes.Length - offset);
            var piece = new byte[count];
            Buffer.BlockCopy(bytes, offset, piece, 0, count);
            await _directory.PipeAsync(seat.From, Convert.ToBase64String(piece), false, CancellationToken.None);
        }
    }

    private sealed class Seat
    {
        private readonly PipeHost _host;
        private readonly SocketPipeStream _stream;
        private int _retired;

        public Seat(string from, PipeHost host)
        {
            From = from;
            _host = host;
            _stream = new SocketPipeStream(bytes => host.SendAsync(this, bytes));
        }

        public string From { get; }

        public bool Retired => Volatile.Read(ref _retired) != 0;

        public void Push(byte[] bytes) => _stream.Push(bytes);

        public void Retire()
        {
            Interlocked.Exchange(ref _retired, 1);
            _stream.End();
        }

        public void Start()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    var stream = await LinkSeal.OpenServerAsync(_stream, LinkWorker.ActivePipeKey(), limit.Token);
                    await LinkWorker.ServeAsync(stream, limit.Token, null, _host._directory, LinkWorker.TrustsLocalNetwork());
                }
                catch (Exception)
                {
                }
                finally
                {
                    Retire();
                    _host.Drop(this);
                }
            });
        }
    }

    private sealed class SocketPipeStream : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Func<byte[], Task> _send;
        private readonly object _write = new();
        private readonly MemoryStream _outgoing = new();
        private byte[] _current = [];
        private int _offset;

        public SocketPipeStream(Func<byte[], Task> send) => _send = send;

        public void Push(byte[] data) => _incoming.Writer.TryWrite(data);

        public void End() => _incoming.Writer.TryComplete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => FlushAsync().GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            byte[] payload;
            lock (_write)
            {
                if (_outgoing.Length == 0)
                    return;
                payload = _outgoing.ToArray();
                _outgoing.SetLength(0);
            }

            await _send(payload);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                try
                {
                    _current = await _incoming.Reader.ReadAsync(cancellationToken);
                    _offset = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_write)
                _outgoing.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_write)
                _outgoing.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
