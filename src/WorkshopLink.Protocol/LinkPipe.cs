using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WorkshopLink.Protocol;

public static class LinkPipe
{
    public static readonly byte[] Magic = "WLINK2\n"u8.ToArray();
    public const int MaxPasswordBytes = 512;
    public const int MaxScriptBytes = 2_000_000;
    public const int MaxValueBytes = 64_000;
    public const int MaxBlobBytes = 512 * 1024 * 1024;
    public const byte ScreenFile = 7;
    public const byte ScreenSecure = 8;
    public const byte ScreenHeartbeat = 9;
    public const byte ScreenLogoff = 10;
    public const byte ScreenStatus = 11;
    public const byte ScreenMode = 12;
    public const byte ScreenPaste = 13;
    public const byte ScreenDisplay = 14;
    public const byte ScreenClip = 15;
    public const byte PictureH264 = 1;
    public const byte PictureAv1 = 4;
    public const byte PictureJpeg = 3;
    public const int MaxOutputBytes = 8 * 1024 * 1024;

    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily != AddressFamily.InterNetwork || bytes.Length != 4)
            return false;
        if (bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 0)
            return true;
        return bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 216;
    }

    public static async Task WriteRequestAsync(
        Stream stream,
        string password,
        string script,
        IReadOnlyDictionary<string, string> environment,
        byte[]? blob,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        await stream.WriteAsync(Magic, cancellationToken);
        await WriteBytesAsync(stream, Encoding.UTF8.GetBytes(password), cancellationToken);
        await WriteBytesAsync(stream, Encoding.UTF8.GetBytes(script), cancellationToken);
        var pairs = environment.ToArray();
        await WriteIntAsync(stream, pairs.Length, cancellationToken);
        foreach (var pair in pairs)
        {
            var key = Encoding.UTF8.GetBytes(pair.Key);
            var value = Encoding.UTF8.GetBytes(pair.Value ?? "");
            if (key.Length > 64 || value.Length > MaxValueBytes)
                throw new InvalidOperationException("That command cannot be sent.");
            await WriteBytesAsync(stream, key, cancellationToken);
            await WriteBytesAsync(stream, value, cancellationToken);
        }

        await WriteBlockAsync(stream, blob ?? [], progress, cancellationToken);
    }

    public static async Task WriteBlockAsync(Stream stream, byte[] blob, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        await WriteIntAsync(stream, blob.Length, cancellationToken);
        var offset = 0;
        while (offset < blob.Length)
        {
            var count = Math.Min(128 * 1024, blob.Length - offset);
            await stream.WriteAsync(blob.AsMemory(offset, count), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            offset += count;
            if (blob.Length > 0)
                progress?.Report(offset * 100 / blob.Length);
        }

        if (blob.Length == 0)
            await stream.FlushAsync(cancellationToken);
    }

    public static async Task<(string Password, string Script, Dictionary<string, string> Environment, byte[] Blob)> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var magic = new byte[Magic.Length];
        await ReadExactAsync(stream, magic, cancellationToken);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidOperationException("This agent does not speak the command center protocol.");

        var password = Encoding.UTF8.GetString(await ReadBlockAsync(stream, MaxPasswordBytes, cancellationToken));
        var script = Encoding.UTF8.GetString(await ReadBlockAsync(stream, MaxScriptBytes, cancellationToken));
        var count = await ReadIntAsync(stream, cancellationToken);
        if (count is < 0 or > 16)
            throw new InvalidOperationException("That command cannot be run.");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = Encoding.UTF8.GetString(await ReadBlockAsync(stream, 64, cancellationToken));
            var value = Encoding.UTF8.GetString(await ReadBlockAsync(stream, MaxValueBytes, cancellationToken));
            environment[key] = value;
        }

        var blob = await ReadBlockAsync(stream, MaxBlobBytes, cancellationToken);
        return (password, script, environment, blob);
    }

    public const byte ScreenFrame = 1;
    public const byte ScreenMouse = 2;
    public const byte ScreenKey = 3;
    public const byte ScreenBye = 4;
    public const byte ScreenPrivacy = 5;
    public const byte ScreenTile = 6;

    public static async Task WriteScreenAsync(Stream stream, byte kind, byte[] payload, CancellationToken cancellationToken)
    {
        var head = new byte[5];
        head[0] = kind;
        BitConverter.TryWriteBytes(head.AsSpan(1), payload.Length);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(head, 1, 4);
        await stream.WriteAsync(head, cancellationToken);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<(byte Kind, byte[] Payload)> ReadScreenAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new byte[5];
        await ReadExactAsync(stream, head, cancellationToken);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(head, 1, 4);
        var length = BitConverter.ToInt32(head, 1);
        if (length < 0 || length > MaxBlobBytes)
            throw new InvalidOperationException("The screen picture was larger than this program accepts.");
        var payload = new byte[length];
        if (length > 0)
            await ReadExactAsync(stream, payload, cancellationToken);
        return (head[0], payload);
    }

    public static async Task WriteResultAsync(Stream stream, int code, string output, string error, byte[]? file, CancellationToken cancellationToken)
    {
        await WriteIntAsync(stream, code, cancellationToken);
        await WriteBytesAsync(stream, Encoding.UTF8.GetBytes(output ?? ""), MaxOutputBytes, cancellationToken);
        await WriteBytesAsync(stream, Encoding.UTF8.GetBytes(error ?? ""), MaxOutputBytes, cancellationToken);
        await WriteBytesAsync(stream, file ?? [], cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<(int Code, string Output, string Error, byte[] File)> ReadResultAsync(Stream stream, CancellationToken cancellationToken)
    {
        var code = await ReadIntAsync(stream, cancellationToken);
        var output = Encoding.UTF8.GetString(await ReadBlockAsync(stream, MaxOutputBytes, cancellationToken));
        var error = Encoding.UTF8.GetString(await ReadBlockAsync(stream, MaxOutputBytes, cancellationToken));
        var file = await ReadBlockAsync(stream, MaxBlobBytes, cancellationToken);
        return (code, output, error, file);
    }

    private static async Task WriteBytesAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken) =>
        await WriteBytesAsync(stream, bytes, int.MaxValue, cancellationToken);

    private static async Task WriteBytesAsync(Stream stream, byte[] bytes, int max, CancellationToken cancellationToken)
    {
        if (bytes.Length > max)
            bytes = bytes.AsSpan(0, max).ToArray();
        await WriteIntAsync(stream, bytes.Length, cancellationToken);
        if (bytes.Length > 0)
            await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<byte[]> ReadBlockAsync(Stream stream, int max, CancellationToken cancellationToken)
    {
        var length = await ReadIntAsync(stream, cancellationToken);
        if (length < 0 || length > max)
            throw new InvalidOperationException("The command center sent a message this agent will not accept.");
        var bytes = new byte[length];
        if (length > 0)
            await ReadExactAsync(stream, bytes, cancellationToken);
        return bytes;
    }

    private static async Task WriteIntAsync(Stream stream, int value, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<int> ReadIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        await ReadExactAsync(stream, bytes, cancellationToken);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToInt32(bytes);
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
}
