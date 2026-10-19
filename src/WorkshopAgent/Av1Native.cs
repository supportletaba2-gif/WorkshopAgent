using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WorkshopLink.Screen;

public static class Av1Native
{
    private static readonly object Gate = new();
    private static IntPtr _encoder;
    private static IntPtr _decoder;
    private static int _width;
    private static int _height;
    private static int _frame;
    private static bool _ready;
    private static bool _failed;

    public static string LastError { get; private set; } = "";

    public static bool TryEncode(Bitmap frame, out byte[] obu)
    {
        obu = [];
        lock (Gate)
        {
            try
            {
                if (!Ensure())
                    return false;
                var width = frame.Width & ~1;
                var height = frame.Height & ~1;
                if (width < 2 || height < 2)
                    return false;
                if (_encoder == IntPtr.Zero || _width != width || _height != height)
                {
                    if (_encoder != IntPtr.Zero)
                        wlink_encoder_close(_encoder);
                    _encoder = wlink_encoder_open(width, height, 15, 2400);
                    _width = width;
                    _height = height;
                    _frame = 0;
                    if (_encoder == IntPtr.Zero)
                    {
                        LastError = "The bundled AV1 encoder did not start.";
                        return false;
                    }
                }

                var bgra = ToBgra(frame, width, height, out var stride);
                var output = new byte[Math.Max(width * height, 256 * 1024)];
                var keyframe = _frame++ % 15 == 0 ? 1 : 0;
                var written = wlink_encoder_encode(_encoder, bgra, stride, keyframe, output, output.Length);
                if (written <= 0)
                {
                    LastError = "The bundled AV1 encoder produced no picture.";
                    return false;
                }

                obu = output[..written];
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                LastError = "wlinkav1.dll was not installed next to the program.";
                _failed = true;
                return false;
            }
        }
    }

    public static Bitmap? TryDecode(ReadOnlySpan<byte> obu)
    {
        if (obu.Length < 2)
            return null;
        lock (Gate)
        {
            try
            {
                if (!Ensure())
                    return null;
                if (_decoder == IntPtr.Zero)
                {
                    _decoder = wlink_decoder_open();
                    if (_decoder == IntPtr.Zero)
                    {
                        LastError = "The bundled AV1 decoder did not start.";
                        return null;
                    }
                }

                var source = obu.ToArray();
                var stride = 2560 * 4;
                var cap = stride * 1600;
                var bgra = new byte[cap];
                var result = wlink_decoder_decode(_decoder, source, source.Length, bgra, bgra.Length, stride, out var width, out var height);
                if (result != 0)
                {
                    wlink_decoder_close(_decoder);
                    _decoder = wlink_decoder_open();
                    if (_decoder == IntPtr.Zero)
                    {
                        LastError = "The bundled AV1 decoder did not start.";
                        return null;
                    }
                    result = wlink_decoder_decode(_decoder, source, source.Length, bgra, bgra.Length, stride, out width, out height);
                }
                if (result != 0 || width < 2 || height < 2 || width > 2560 || height > 1600)
                {
                    LastError = "The bundled AV1 decoder rejected the picture.";
                    return null;
                }

                return FromBgra(bgra, width, height, stride);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                LastError = "wlinkav1.dll was not installed next to the program.";
                _failed = true;
                return null;
            }
        }
    }

    private static bool Ensure()
    {
        if (_ready)
            return true;
        if (_failed)
            return false;
        foreach (var folder in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) })
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            var path = Path.Combine(folder, "wlinkav1.dll");
            if (!File.Exists(path))
                continue;
            NativeLibrary.Load(path);
            _ready = true;
            return true;
        }

        LastError = "wlinkav1.dll was not installed next to the program.";
        _failed = true;
        return false;
    }

    private static byte[] ToBgra(Bitmap frame, int width, int height, out int stride)
    {
        using var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(copy))
            graphics.DrawImage(frame, 0, 0, width, height);
        var bits = copy.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = width * 4;
            var bgra = new byte[stride * height];
            for (var row = 0; row < height; row++)
                Marshal.Copy(bits.Scan0 + row * bits.Stride, bgra, row * stride, stride);
            return bgra;
        }
        finally
        {
            copy.UnlockBits(bits);
        }
    }

    private static Bitmap FromBgra(byte[] bgra, int width, int height, int stride)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(bgra, row * stride, bits.Scan0 + row * bits.Stride, width * 4);
            return bitmap;
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
    }

    [DllImport("wlinkav1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wlink_encoder_open(int width, int height, int fps, int bitrateKbps);

    [DllImport("wlinkav1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int wlink_encoder_encode(IntPtr encoder, byte[] bgra, int stride, int keyframe, byte[] output, int outputCap);

    [DllImport("wlinkav1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void wlink_encoder_close(IntPtr encoder);

    [DllImport("wlinkav1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wlink_decoder_open();

    [DllImport("wlinkav1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int wlink_decoder_decode(IntPtr decoder, byte[] obu, int obuLength, byte[] bgra, int bgraCap, int bgraStride, out int width, out int height);

    [DllImport("wlinkav1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void wlink_decoder_close(IntPtr decoder);
}
