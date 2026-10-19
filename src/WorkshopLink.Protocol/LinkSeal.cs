using System.Security.Cryptography;
using System.Text;

namespace WorkshopLink.Protocol;

public static class LinkSeal
{
    public static readonly byte[] Magic = "WLINK3\n"u8.ToArray();

    public static byte[] KeyFromPassword(string password) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(password));

    public static Task<Stream> OpenClientKeyAsync(Stream raw, byte[] pipeKey, CancellationToken cancellationToken) =>
        OpenClientCoreAsync(raw, pipeKey, cancellationToken);

    public static async Task<Stream> OpenClientAsync(Stream raw, string password, CancellationToken cancellationToken) =>
        await OpenClientCoreAsync(raw, KeyFromPassword(password), cancellationToken);

    private static async Task<Stream> OpenClientCoreAsync(Stream raw, byte[] pipeKey, CancellationToken cancellationToken)
    {
        if (pipeKey.Length != 32)
            throw new InvalidOperationException("This PC's command key is missing.");
        var clientNonce = RandomNumberGenerator.GetBytes(16);
        await raw.WriteAsync(Magic, cancellationToken);
        await raw.WriteAsync(clientNonce, cancellationToken);
        await raw.FlushAsync(cancellationToken);
        var serverNonce = new byte[16];
        await ReadExactAsync(raw, serverNonce, cancellationToken);
        return new SealedStream(raw, Derive(pipeKey, clientNonce, serverNonce));
    }

    public static async Task<Stream> OpenServerAsync(Stream raw, byte[]? pipeKey, CancellationToken cancellationToken)
    {
        var magic = new byte[Magic.Length];
        await ReadExactAsync(raw, magic, cancellationToken);
        if (magic.AsSpan().SequenceEqual(Magic))
        {
            if (pipeKey is null || pipeKey.Length != 32)
                throw new InvalidOperationException("This PC has no key for an encrypted command.");
            var clientNonce = new byte[16];
            await ReadExactAsync(raw, clientNonce, cancellationToken);
            var serverNonce = RandomNumberGenerator.GetBytes(16);
            await raw.WriteAsync(serverNonce, cancellationToken);
            await raw.FlushAsync(cancellationToken);
            return new SealedStream(raw, Derive(pipeKey, clientNonce, serverNonce));
        }

        throw new InvalidOperationException("This PC does not accept a clear command.");
    }

    private static byte[] Derive(byte[] pipeKey, byte[] clientNonce, byte[] serverNonce)
    {
        var salt = new byte[clientNonce.Length + serverNonce.Length];
        clientNonce.CopyTo(salt, 0);
        serverNonce.CopyTo(salt, clientNonce.Length);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, pipeKey, 32, salt, "wlink-pipe"u8.ToArray());
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new IOException("The connection closed before the message finished.");
            offset += read;
        }
    }

    private sealed class SealedStream(Stream inner, byte[] key) : Stream
    {
        private const int PlainLimit = 64 * 1024;
        private readonly byte[] _plain = new byte[PlainLimit];
        private int _plainStart;
        private int _plainEnd;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var count = Math.Min(PlainLimit, buffer.Length - offset);
                await WriteFrameAsync(buffer.Slice(offset, count), cancellationToken);
                offset += count;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
                return 0;
            if (_plainStart >= _plainEnd)
            {
                _plainStart = 0;
                _plainEnd = await ReadFrameAsync(cancellationToken);
                if (_plainEnd == 0)
                    return 0;
            }

            var count = Math.Min(buffer.Length, _plainEnd - _plainStart);
            _plain.AsSpan(_plainStart, count).CopyTo(buffer.Span);
            _plainStart += count;
            return count;
        }

        private async Task WriteFrameAsync(ReadOnlyMemory<byte> plain, CancellationToken cancellationToken)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length + 16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plain.Span, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length));
            var head = new byte[4];
            BitConverter.TryWriteBytes(head, cipher.Length);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(head);
            await inner.WriteAsync(head, cancellationToken);
            await inner.WriteAsync(nonce, cancellationToken);
            await inner.WriteAsync(cipher, cancellationToken);
        }

        private async Task<int> ReadFrameAsync(CancellationToken cancellationToken)
        {
            var head = new byte[4];
            try
            {
                await ReadExactAsync(inner, head, cancellationToken);
            }
            catch (IOException)
            {
                return 0;
            }

            if (!BitConverter.IsLittleEndian)
                Array.Reverse(head);
            var length = BitConverter.ToInt32(head, 0);
            if (length < 16 || length > PlainLimit + 16)
                throw new InvalidOperationException("The command center sent a message this agent will not accept.");
            var nonce = new byte[12];
            await ReadExactAsync(inner, nonce, cancellationToken);
            var cipher = new byte[length];
            await ReadExactAsync(inner, cipher, cancellationToken);
            var plainLength = length - 16;
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher.AsSpan(0, plainLength), cipher.AsSpan(plainLength), _plain.AsSpan(0, plainLength));
            return plainLength;
        }
    }
}
