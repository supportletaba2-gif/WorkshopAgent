using System.Threading.Channels;

namespace WorkshopLink.Protocol;

public sealed class DirectoryPipe : Stream
{
    private const int ChunkBytes = 18000;
    private readonly DirectoryClient _directory;
    private readonly string _to;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly object _write = new();
    private readonly MemoryStream _outgoing = new();
    private byte[] _current = [];
    private int _offset;
    private int _closed;

    public DirectoryPipe(DirectoryClient directory, string to)
    {
        _directory = directory;
        _to = to;
        directory.PipeReceived += OnPipe;
    }

    public string PeerId => _to;

    private void OnPipe(string from, string payload, bool close)
    {
        if (!string.Equals(from, _to, StringComparison.OrdinalIgnoreCase))
            return;
        if (payload.Length > 0)
        {
            try
            {
                _incoming.Writer.TryWrite(Convert.FromBase64String(payload));
            }
            catch (FormatException)
            {
            }
        }

        if (close)
            _incoming.Writer.TryComplete();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

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

        for (var offset = 0; offset < payload.Length; offset += ChunkBytes)
        {
            var count = Math.Min(ChunkBytes, payload.Length - offset);
            var piece = new byte[count];
            Buffer.BlockCopy(payload, offset, piece, 0, count);
            await _directory.PipeAsync(_to, Convert.ToBase64String(piece), false, cancellationToken);
        }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _directory.PipeReceived -= OnPipe;
        _directory.ReleasePipe(this);
        _incoming.Writer.TryComplete();
        try
        {
            await FlushAsync();
            await _directory.PipeAsync(_to, "", true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
        }
    }
}
