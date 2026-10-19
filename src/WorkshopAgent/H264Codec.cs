using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WorkshopLink.Screen;

public static class H264Codec
{
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Av1 = new("31305641-0000-0010-8000-00aa00389b71");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid ForceKey = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly object Gate = new();
    private static IMFTransform? _encoder;
    private static IMFTransform? _decoder;
    private static int _width;
    private static int _height;
    private static int _encWidth;
    private static int _encHeight;
    private static bool _trace;
    private static int _frame;
    private static long _clock;
    private static long _decodeClock;
    private static bool _started;
    private static int _ready = -1;
    private static IMFTransform? _av1Encoder;
    private static IMFTransform? _av1Decoder;
    private static int _av1Width;
    private static int _av1Height;
    private static int _av1EncWidth;
    private static int _av1EncHeight;
    private static int _av1Frame;
    private static long _av1Clock;
    private static long _av1DecodeClock;

    public static string LastError { get; private set; } = "";

    public static bool TryEncode(Bitmap frame, out byte[] nal)
    {
        nal = [];
        lock (Gate)
        {
            try
            {
                nal = Encode(frame);
                return nal.Length > 0;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or OutOfMemoryException)
            {
                LastError = ex.Message;
                Trace("encode " + ex.GetType().Name + " " + ex.Message);
                ResetEncoder();
                return false;
            }
        }
    }

    public static Bitmap? TryDecode(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 5)
            return null;
        lock (Gate)
        {
            try
            {
                return Decode(nal.ToArray());
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or OutOfMemoryException)
            {
                LastError = ex.Message;
                Trace("decode " + ex.Message);
                ResetDecoder();
                return null;
            }
        }
    }

    public static bool TryEncodeAv1(Bitmap frame, out byte[] obu)
    {
        if (Av1Native.TryEncode(frame, out obu))
            return true;
        LastError = Av1Native.LastError;
        obu = [];
        lock (Gate)
        {
            try
            {
                obu = EncodeAv1(frame);
                return obu.Length > 0;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or OutOfMemoryException)
            {
                LastError = ex.Message;
                Trace("av1 encode " + ex.Message);
                ResetAv1Encoder();
                return false;
            }
        }
    }

    public static Bitmap? TryDecodeAv1(ReadOnlySpan<byte> obu)
    {
        var native = Av1Native.TryDecode(obu);
        if (native is not null)
            return native;
        LastError = Av1Native.LastError;
        if (obu.Length < 2)
            return null;
        lock (Gate)
        {
            try
            {
                return DecodeAv1(obu.ToArray());
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or OutOfMemoryException)
            {
                LastError = ex.Message;
                Trace("av1 decode " + ex.Message);
                ResetAv1Decoder();
                return null;
            }
        }
    }

    private static byte[] EncodeAv1(Bitmap frame)
    {
        Startup();
        var width = frame.Width & ~1;
        var height = frame.Height & ~1;
        if (width < 2 || height < 2)
            return [];
        EnsureAv1Encoder(width, height);
        if (_av1Encoder is null)
            return [];
        if ((_av1Frame % 15) == 0)
            ForceKeyframe(_av1Encoder);
        var sample = CreateSample(frame, width, height);
        try
        {
            sample.SetSampleTime(_av1Clock);
            sample.SetSampleDuration(10_000_000 / 15);
            _av1Clock += 10_000_000 / 15;
            _av1Frame++;
            var input = _av1Encoder.ProcessInput(0, sample, 0);
            if (input < 0 && input != NeedMoreInput)
                throw new COMException("The AV1 encoder did not accept the frame.", input);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }

        return Drain(_av1Encoder);
    }

    private static Bitmap? DecodeAv1(byte[] obu)
    {
        Startup();
        EnsureAv1Decoder();
        if (_av1Decoder is null)
            return null;
        MFCreateMemoryBuffer(obu.Length, out var buffer);
        buffer.Lock(out var data, out _, out _);
        Marshal.Copy(obu, 0, data, obu.Length);
        buffer.Unlock();
        buffer.SetCurrentLength(obu.Length);
        MFCreateSample(out var sample);
        sample.AddBuffer(buffer);
        sample.SetSampleTime(_av1DecodeClock);
        sample.SetSampleDuration(10_000_000 / 15);
        _av1DecodeClock += 10_000_000 / 15;
        Marshal.ReleaseComObject(buffer);
        try
        {
            var input = _av1Decoder.ProcessInput(0, sample, 0);
            if (input < 0 && input != NeedMoreInput)
                throw new COMException("The AV1 decoder did not accept the frame.", input);
            var raw = DrainAv1(_av1Decoder);
            if (_av1Width < 2 || _av1Height < 2 || raw.Length < _av1Width * _av1Height * 3 / 2)
                return null;
            return FromNv12(raw, _av1Width, _av1Height);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    private static void EnsureAv1Encoder(int width, int height)
    {
        if (_av1Encoder is not null && _av1EncWidth == width && _av1EncHeight == height)
            return;
        ResetAv1Encoder();
        _av1Encoder = ActivateAv1(encoder: true);
        SetLowLatency(_av1Encoder);
        SetType(_av1Encoder, output: true, width, height, Av1);
        SetType(_av1Encoder, output: false, width, height, Nv12);
        _av1Encoder.ProcessMessage(0x10000000, IntPtr.Zero);
        _av1Encoder.ProcessMessage(0x10000003, IntPtr.Zero);
        _av1EncWidth = width;
        _av1EncHeight = height;
        _av1Frame = 0;
        _av1Clock = 0;
    }

    private static void EnsureAv1Decoder()
    {
        if (_av1Decoder is not null)
            return;
        _av1Decoder = ActivateAv1(encoder: false);
        MFCreateMediaType(out var type);
        type.SetGUID(Major, Video);
        type.SetGUID(Subtype, Av1);
        var hr = _av1Decoder.SetInputType(0, type, 0);
        Marshal.ReleaseComObject(type);
        if (hr < 0)
            throw new COMException("The AV1 decoder input was rejected.", hr);
        _av1Decoder.ProcessMessage(0x10000000, IntPtr.Zero);
        _av1Decoder.ProcessMessage(0x10000003, IntPtr.Zero);
    }

    private static IMFTransform ActivateAv1(bool encoder)
    {
        var category = encoder
            ? new Guid("f79eac7d-e545-4387-bdee-3cee3e9d3d2d")
            : new Guid("d6c02d4b-6833-45b4-971a-05a4b04bab91");
        var info = new MftRegisterType { Major = Video, Subtype = Av1 };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<MftRegisterType>());
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            var input = encoder ? IntPtr.Zero : pointer;
            var output = encoder ? pointer : IntPtr.Zero;
            var result = -1;
            var activates = IntPtr.Zero;
            var count = 0;
            foreach (var flags in new[] { 0x00000041, 0x0000003F })
            {
                result = MFTEnumEx(category, flags, input, output, out activates, out count);
                if (result >= 0 && count > 0 && activates != IntPtr.Zero)
                    break;
            }
            if (result < 0 || count < 1 || activates == IntPtr.Zero)
                throw new InvalidOperationException(encoder
                    ? "Windows did not offer an AV1 encoder."
                    : "Windows did not offer an AV1 decoder. Install the AV1 Video Extension.");
            try
            {
                var activate = Marshal.GetObjectForIUnknown(Marshal.ReadIntPtr(activates));
                var hr = ((IMFActivate)activate).ActivateObject(typeof(IMFTransform).GUID, out var created);
                if (hr < 0 || created == IntPtr.Zero)
                    throw new COMException("The AV1 transform did not start.", hr);
                return (IMFTransform)Marshal.GetObjectForIUnknown(created);
            }
            finally
            {
                for (var index = 0; index < count; index++)
                    Marshal.Release(Marshal.ReadIntPtr(activates, index * IntPtr.Size));
                Marshal.FreeCoTaskMem(activates);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static byte[] DrainAv1(IMFTransform transform)
    {
        var raw = Drain(transform);
        if (transform.GetOutputCurrentType(0, out var type) >= 0 && type is not null)
        {
            if (type.GetUINT64(FrameSize, out var size) >= 0)
            {
                _av1Width = (int)(size >> 32);
                _av1Height = (int)(size & 0xffffffff);
            }
            Marshal.ReleaseComObject(type);
        }
        return raw;
    }

    private static void ResetAv1Encoder()
    {
        if (_av1Encoder is null)
            return;
        Marshal.ReleaseComObject(_av1Encoder);
        _av1Encoder = null;
    }

    private static void ResetAv1Decoder()
    {
        if (_av1Decoder is null)
            return;
        Marshal.ReleaseComObject(_av1Decoder);
        _av1Decoder = null;
        _av1Width = 0;
        _av1Height = 0;
    }

    private static void ForceKeyframe(IMFTransform encoder)
    {
        if (encoder.GetAttributes(out var attributes) < 0 || attributes == IntPtr.Zero)
            return;
        var store = (IMFAttributes)Marshal.GetObjectForIUnknown(attributes);
        store.SetUINT32(ForceKey, 1);
        Marshal.ReleaseComObject(store);
    }

    public static string RoundTrip()
    {
        _trace = true;
        Trace("start");
        Startup();
        Trace("startup");
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Blue);
            graphics.FillRectangle(Brushes.Red, 8, 8, 24, 24);
        }

        byte[] encoded = [];
        Bitmap? decoded = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (!TryEncode(bitmap, out var nal) || nal.Length == 0)
                continue;
            encoded = nal;
            decoded = TryDecode(nal) ?? decoded;
            if (decoded is not null)
                break;
        }

        Trace("encoded " + encoded.Length);
        if (encoded.Length == 0)
            return "encode failed " + LastError;
        if (decoded is null)
            return "decode failed " + encoded.Length + " " + LastError;
        var pixel = decoded.GetPixel(16, 16);
        decoded.Dispose();
        var annex = encoded.Length > 4 && encoded[0] == 0 && encoded[1] == 0 && (encoded[2] == 1 || encoded[3] == 1);
        return (annex && pixel.R > pixel.B ? "ok " : "weak ") + encoded.Length + " " + pixel.R + "," + pixel.G + "," + pixel.B;
    }

    private static byte[] Encode(Bitmap frame)
    {
        Startup();
        var width = frame.Width & ~1;
        var height = frame.Height & ~1;
        if (width < 2 || height < 2)
            return [];
        EnsureEncoder(width, height);
        if (_encoder is null)
            return [];
        if ((_frame % 15) == 0)
            ForceKeyframe();
        Trace("sample");
        var sample = CreateSample(frame, width, height);
        Trace("sample ready");
        try
        {
            Trace("time");
            sample.SetSampleTime(_clock);
            sample.SetSampleDuration(10_000_000 / 15);
            _clock += 10_000_000 / 15;
            _frame++;
            Trace("input");
            var input = _encoder.ProcessInput(0, sample, 0);
            Trace("input " + input.ToString("X"));
            if (input < 0 && input != NeedMoreInput)
                throw new COMException("The H.264 encoder did not accept the frame.", input);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }

        return AnnexB(Drain(_encoder));
    }

    private static Bitmap? Decode(byte[] nal)
    {
        Startup();
        var annex = AnnexB(nal);
        if (TryReadSps(annex, out var streamWidth, out var streamHeight))
        {
            if (_decoder is not null && (_width != streamWidth || _height != streamHeight))
                ResetDecoder();
            _width = streamWidth;
            _height = streamHeight;
            Trace("sps " + streamWidth + "x" + streamHeight);
        }
        EnsureDecoder();
        if (_decoder is null)
            return null;
        MFCreateMemoryBuffer(annex.Length, out var buffer);
        buffer.Lock(out var data, out _, out _);
        Marshal.Copy(annex, 0, data, annex.Length);
        buffer.Unlock();
        buffer.SetCurrentLength(annex.Length);
        MFCreateSample(out var sample);
        sample.AddBuffer(buffer);
        sample.SetSampleTime(_decodeClock);
        sample.SetSampleDuration(10_000_000 / 15);
        _decodeClock += 10_000_000 / 15;
        Marshal.ReleaseComObject(buffer);
        try
        {
            var input = _decoder.ProcessInput(0, sample, 0);
            Trace("din " + input.ToString("X") + " n " + annex.Length);
            if (input < 0 && input != NeedMoreInput)
                throw new COMException("The H.264 decoder did not accept the frame.", input);
            return FinishDecode(Drain(_decoder));
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    private static Bitmap? FinishDecode(byte[] raw)
    {
        Trace("raw " + raw.Length + " " + _width + "x" + _height);
        if (raw.Length < 6)
            return null;
        var width = _width > 0 ? _width : 0;
        var height = _height > 0 ? _height : 0;
        if (width < 2 || height < 2 || raw.Length < width * height * 3 / 2)
            return null;
        return FromNv12(raw, width, height);
    }

    private static void EnsureEncoder(int width, int height)
    {
        if (_encoder is not null && _encWidth == width && _encHeight == height)
            return;
        ResetEncoder();
        Trace("activate encoder");
        _encoder = Activate(new Guid("f79eac7d-e545-4387-bdee-3cee3e9d3d2d"));
        Trace("encoder alive");
        SetLowLatency(_encoder);
        Trace("low latency");
        SetType(_encoder, output: true, width, height, H264);
        Trace("output type");
        SetType(_encoder, output: false, width, height, Nv12);
        Trace("input type");
        Trace("messages");
        _encoder.ProcessMessage(0x10000000, IntPtr.Zero);
        _encoder.ProcessMessage(0x10000003, IntPtr.Zero);
        _encWidth = width;
        _encHeight = height;
        _frame = 0;
        _clock = 0;
    }

    private static void EnsureDecoder()
    {
        if (_decoder is not null)
            return;
        _decoder = Activate(new Guid("d6c02d4b-6833-45b4-971a-05a4b04bab91"));
        MFCreateMediaType(out var type);
        type.SetGUID(Major, Video);
        type.SetGUID(Subtype, H264);
        if (_width >= 2 && _height >= 2)
            type.SetUINT64(FrameSize, ((ulong)(uint)_width << 32) | (uint)_height);
        var hr = _decoder.SetInputType(0, type, 0);
        Marshal.ReleaseComObject(type);
        Trace("decoder input " + hr.ToString("X"));
        if (hr < 0)
            throw new COMException("The H.264 decoder input was rejected.", hr);
        _decoder.ProcessMessage(0x10000000, IntPtr.Zero);
        _decoder.ProcessMessage(0x10000003, IntPtr.Zero);
    }

    private static IMFTransform Activate(Guid category)
    {
        var clsid = category == new Guid("d6c02d4b-6833-45b4-971a-05a4b04bab91")
            ? new Guid("62ce7e72-4c71-4d20-b15d-452831a87d9d")
            : new Guid("6ca50344-051a-4ded-9779-a43305165e35");
        var iid = typeof(IMFTransform).GUID;
        var created = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var createdPointer);
        Trace("cocreate " + created.ToString("X") + " " + (createdPointer == IntPtr.Zero ? "empty" : "object"));
        if (created >= 0 && createdPointer != IntPtr.Zero)
            return (IMFTransform)Marshal.GetObjectForIUnknown(createdPointer);

        var result = -1;
        var activates = IntPtr.Zero;
        var count = 0;
        foreach (var flags in new[] { 0x00000001, 0x0000003F })
        {
            result = MFTEnumEx(category, flags, IntPtr.Zero, IntPtr.Zero, out activates, out count);
            Trace("enum " + flags.ToString("X") + " hr " + result.ToString("X") + " count " + count);
            if (result >= 0 && count > 0 && activates != IntPtr.Zero)
                break;
        }
        if (result < 0 || count < 1 || activates == IntPtr.Zero)
            throw new InvalidOperationException("Windows did not offer an H.264 transform (" + created.ToString("X") + ").");
        try
        {
            var activate = Marshal.GetObjectForIUnknown(Marshal.ReadIntPtr(activates));
            var hr = ((IMFActivate)activate).ActivateObject(typeof(IMFTransform).GUID, out var pointer);
            if (hr < 0 || pointer == IntPtr.Zero)
                throw new COMException("The H.264 transform did not start.", hr);
            return (IMFTransform)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            for (var index = 0; index < count; index++)
                Marshal.Release(Marshal.ReadIntPtr(activates, index * IntPtr.Size));
            Marshal.FreeCoTaskMem(activates);
        }
    }

    private static void SetType(IMFTransform transform, bool output, int width, int height, Guid subtype)
    {
        MFCreateMediaType(out var type);
        type.SetGUID(Major, Video);
        type.SetGUID(Subtype, subtype);
        type.SetUINT64(FrameSize, ((ulong)(uint)width << 32) | (uint)height);
        type.SetUINT64(FrameRate, ((ulong)15 << 32) | 1);
        type.SetUINT32(Interlace, 2);
        if (output && subtype == H264)
        {
            type.SetUINT32(Bitrate, 2_500_000);
            type.SetUINT32(new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036"), 66);
        }
        var hr = output ? transform.SetOutputType(0, type, 0) : transform.SetInputType(0, type, 0);
        Marshal.ReleaseComObject(type);
        if (hr < 0)
            throw new COMException(output ? "The H.264 output picture was rejected." : "The H.264 input picture was rejected.", hr);
    }

    private static void SetLowLatency(IMFTransform transform)
    {
        if (transform.GetAttributes(out var attributes) < 0 || attributes == IntPtr.Zero)
            return;
        var store = (IMFAttributes)Marshal.GetObjectForIUnknown(attributes);
        store.SetUINT32(LowLatency, 1);
        Marshal.ReleaseComObject(store);
    }

    private static void ForceKeyframe()
    {
        if (_encoder is null || _encoder.GetAttributes(out var attributes) < 0 || attributes == IntPtr.Zero)
            return;
        var store = (IMFAttributes)Marshal.GetObjectForIUnknown(attributes);
        store.SetUINT32(ForceKey, 1);
        Marshal.ReleaseComObject(store);
    }

    private static IMFSample CreateSample(Bitmap frame, int width, int height)
    {
        var nv12 = ToNv12(frame, width, height);
        Trace("nv12 " + nv12.Length);
        var bufferHr = MFCreateMemoryBuffer(nv12.Length, out var buffer);
        Trace("buffer " + bufferHr.ToString("X"));
        if (bufferHr < 0)
            throw new COMException("The picture buffer was not created.", bufferHr);
        var lockHr = buffer.Lock(out var data, out _, out _);
        Trace("lock " + lockHr.ToString("X"));
        Marshal.Copy(nv12, 0, data, nv12.Length);
        var unlock = buffer.Unlock();
        Trace("unlock " + unlock.ToString("X"));
        var lengthHr = buffer.SetCurrentLength(nv12.Length);
        Trace("length " + lengthHr.ToString("X"));
        var sampleHr = MFCreateSample(out var sample);
        Trace("samplehr " + sampleHr.ToString("X"));
        var addHr = sample.AddBuffer(buffer);
        Trace("add " + addHr.ToString("X"));
        Marshal.ReleaseComObject(buffer);
        return sample;
    }

    private static byte[] Drain(IMFTransform transform)
    {
        var output = new List<byte>();
        for (var pass = 0; pass < 8; pass++)
        {
            var info = new MFT_OUTPUT_STREAM_INFO();
            transform.GetOutputStreamInfo(0, ref info);
            var provides = (info.dwFlags & 0x100) != 0;
            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;
            if (!provides)
            {
                MFCreateSample(out sample);
                MFCreateMemoryBuffer((int)Math.Max(info.cbSize, 64 * 1024), out buffer);
                sample.AddBuffer(buffer);
            }
            var data = new MFT_OUTPUT_DATA_BUFFER { pSample = sample };
            try
            {
                var hr = transform.ProcessOutput(0, 1, ref data, out _);
                Trace("process " + hr.ToString("X") + " flags " + info.dwFlags.ToString("X") + " size " + info.cbSize);
                if (provides && data.pSample is not null)
                    sample = data.pSample;
                if (hr == NeedMoreInput)
                    break;
                if (hr == StreamChange || hr == unchecked((int)0xC00D6D60))
                {
                    if (!ApplyOutputType(transform))
                        break;
                    transform.ProcessMessage(0x10000003, IntPtr.Zero);
                    continue;
                }
                if (hr < 0)
                    throw new COMException("The H.264 transform produced no picture.", hr);
                if (provides && sample is not null)
                {
                    sample.ConvertToContiguousBuffer(out buffer);
                }
                if (buffer is null)
                    break;
                buffer.Lock(out var pointer, out _, out var length);
                var chunk = new byte[length];
                Marshal.Copy(pointer, chunk, 0, length);
                buffer.Unlock();
                if (output.Count == 0 && chunk.Length > 0)
                    Trace("hex " + Convert.ToHexString(chunk, 0, Math.Min(24, chunk.Length)));
                Trace("chunk " + length);
                output.AddRange(chunk);
            }
            finally
            {
                if (data.pEvents != IntPtr.Zero)
                    Marshal.Release(data.pEvents);
                if (sample is not null)
                    Marshal.ReleaseComObject(sample);
                if (buffer is not null)
                    Marshal.ReleaseComObject(buffer);
            }
        }

        return output.ToArray();
    }

    private static bool ApplyOutputType(IMFTransform transform)
    {
        IMFAttributes? match = null;
        for (var index = 0; index < 8; index++)
        {
            if (transform.GetOutputAvailableType(0, index, out var offered) < 0)
                break;
            offered.GetUINT64(FrameSize, out var size);
            var offeredWidth = (int)(size >> 32);
            var offeredHeight = (int)(size & 0xffffffff);
            Trace("offered " + offeredWidth + "x" + offeredHeight);
            if (match is null && (offeredWidth == _width && offeredHeight == _height || index == 0))
                match = offered;
            else
                Marshal.ReleaseComObject(offered);
        }

        if (match is null)
            return false;
        var hr = transform.SetOutputType(0, match, 0);
        Trace("output set " + hr.ToString("X"));
        Marshal.ReleaseComObject(match);
        return hr >= 0;
    }

    private static byte[] SequenceHeader(byte[] annex)
    {
        var header = new List<byte>();
        foreach (var unit in Units(annex))
        {
            if (unit.Length < 5)
                continue;
            var kind = unit[4] & 0x1f;
            if (kind is not (7 or 8))
                continue;
            var skip = unit[2] == 1 ? 3 : 4;
            for (var index = skip; index < unit.Length; index++)
                header.Add(unit[index]);
        }

        return header.ToArray();
    }

    private static List<byte[]> Units(byte[] annex)
    {
        var units = new List<byte[]>();
        var offset = 0;
        while (offset + 4 < annex.Length)
        {
            var start = FindStart(annex, offset);
            if (start < 0)
                break;
            var next = FindStart(annex, start + 3);
            var end = next < 0 ? annex.Length : next;
            var unit = new byte[end - start];
            Buffer.BlockCopy(annex, start, unit, 0, unit.Length);
            units.Add(unit);
            if (next < 0)
                break;
            offset = next;
        }

        return units;
    }

    private static bool TryReadSps(byte[] annex, out int width, out int height)
    {
        width = height = 0;
        var offset = 0;
        while (offset + 5 < annex.Length)
        {
            var start = FindStart(annex, offset);
            if (start < 0)
                return false;
            var header = annex[start + 2] == 1 ? 3 : 4;
            var nal = start + header;
            var next = FindStart(annex, nal);
            var end = next < 0 ? annex.Length : next;
            if (nal < annex.Length && (annex[nal] & 0x1f) == 7 && ReadSps(annex, nal + 1, end, out width, out height))
                return true;
            offset = end;
        }

        return false;
    }

    private static int FindStart(byte[] data, int offset)
    {
        for (var index = offset; index + 3 < data.Length; index++)
        {
            if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
                return index;
            if (index + 4 < data.Length && data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 1)
                return index;
        }

        return -1;
    }

    private static bool ReadSps(byte[] data, int start, int end, out int width, out int height)
    {
        width = height = 0;
        try
        {
            var bits = new BitReader(data, start, end - start);
            var profile = bits.Unsigned(8);
            bits.Unsigned(8);
            bits.Unsigned(8);
            bits.Exp();
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134)
            {
                var chroma = bits.Exp();
                if (chroma == 3)
                    bits.Unsigned(1);
                bits.Exp();
                bits.Exp();
                bits.Unsigned(1);
                if (bits.Unsigned(1) == 1)
                    SkipScaling(bits, chroma == 3 ? 12 : 8);
            }

            bits.Exp();
            var order = bits.Exp();
            if (order == 0)
                bits.Exp();
            else if (order == 1)
            {
                bits.Unsigned(1);
                bits.Signed();
                bits.Signed();
                var count = bits.Exp();
                for (var index = 0; index < count; index++)
                    bits.Signed();
            }

            bits.Exp();
            bits.Unsigned(1);
            var macroWidth = bits.Exp() + 1;
            var macroHeight = bits.Exp() + 1;
            var frameOnly = bits.Unsigned(1);
            if (frameOnly == 0)
                bits.Unsigned(1);
            bits.Unsigned(1);
            var cropLeft = 0;
            var cropRight = 0;
            var cropTop = 0;
            var cropBottom = 0;
            if (bits.Unsigned(1) == 1)
            {
                cropLeft = bits.Exp();
                cropRight = bits.Exp();
                cropTop = bits.Exp();
                cropBottom = bits.Exp();
            }

            width = macroWidth * 16 - (cropLeft + cropRight) * 2;
            height = (frameOnly == 1 ? macroHeight : macroHeight * 2) * 16 - (cropTop + cropBottom) * 2;
            return width is >= 2 and <= 4096 && height is >= 2 and <= 4096;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void SkipScaling(BitReader bits, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (bits.Unsigned(1) != 1)
                continue;
            var size = index < 6 ? 16 : 64;
            var last = 8;
            var next = 8;
            for (var item = 0; item < size; item++)
            {
                if (next != 0)
                    next = (last + bits.Signed() + 256) % 256;
                last = next == 0 ? last : next;
            }
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _byte;
        private int _bit = 8;

        public BitReader(byte[] data, int start, int length)
        {
            _data = data;
            _byte = start;
            _end = start + length;
        }

        public int Unsigned(int count)
        {
            var value = 0;
            for (var index = 0; index < count; index++)
                value = (value << 1) | Next();
            return value;
        }

        public int Exp()
        {
            var zeros = 0;
            while (Next() == 0)
                zeros++;
            return ((1 << zeros) - 1) + (zeros == 0 ? 0 : Unsigned(zeros));
        }

        public int Signed()
        {
            var value = Exp();
            return (value & 1) == 0 ? -(value >> 1) : (value + 1) >> 1;
        }

        private int Next()
        {
            if (_bit == 8)
            {
                if (_byte >= _end)
                    throw new InvalidOperationException();
                _bit = 0;
            }

            var bit = (_data[_byte] >> (7 - _bit)) & 1;
            if (++_bit == 8)
                _byte++;
            return bit;
        }
    }

    private static byte[] AnnexB(byte[] data)
    {
        if (data.Length < 4)
            return data;
        if (data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1)))
            return data;
        var output = new List<byte>(data.Length + 16);
        var offset = 0;
        var converted = false;
        while (offset + 4 <= data.Length)
        {
            var length = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            if (length <= 0 || offset + 4 + length > data.Length)
                break;
            output.Add(0);
            output.Add(0);
            output.Add(0);
            output.Add(1);
            for (var index = 0; index < length; index++)
                output.Add(data[offset + 4 + index]);
            offset += 4 + length;
            converted = true;
        }

        if (!converted || offset != data.Length)
        {
            var wrapped = new byte[data.Length + 4];
            wrapped[3] = 1;
            Buffer.BlockCopy(data, 0, wrapped, 4, data.Length);
            return wrapped;
        }

        return output.ToArray();
    }

    private static byte[] ToNv12(Bitmap frame, int width, int height)
    {
        var ySize = width * height;
        var nv12 = new byte[ySize + ySize / 2];
        var bits = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = Marshal.ReadInt32(bits.Scan0, y * bits.Stride + x * 4);
                    var b = pixel & 255;
                    var g = (pixel >> 8) & 255;
                    var r = (pixel >> 16) & 255;
                    nv12[y * width + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                    if ((y & 1) == 0 && (x & 1) == 0)
                    {
                        var uv = ySize + (y / 2) * width + x;
                        nv12[uv] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                        nv12[uv + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                    }
                }
            }
        }
        finally
        {
            frame.UnlockBits(bits);
        }

        return nv12;
    }

    private static Bitmap FromNv12(byte[] nv12, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ySize = width * height;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var luma = nv12[y * width + x] - 16;
                    var uv = ySize + (y / 2) * width + (x & ~1);
                    var d = nv12[uv] - 128;
                    var e = nv12[uv + 1] - 128;
                    var red = Math.Clamp((298 * luma + 409 * e + 128) >> 8, 0, 255);
                    var green = Math.Clamp((298 * luma - 100 * d - 208 * e + 128) >> 8, 0, 255);
                    var blue = Math.Clamp((298 * luma + 516 * d + 128) >> 8, 0, 255);
                    Marshal.WriteInt32(bits.Scan0, y * bits.Stride + x * 4, (red << 16) | (green << 8) | blue | unchecked((int)0xff000000));
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }

        return bitmap;
    }

    private static void Trace(string step)
    {
        if (!_trace)
            return;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "wlink-h264.txt"), step + Environment.NewLine); }
        catch (IOException) { }
    }

    private static void Startup()
    {
        if (_started)
            return;
        if (MFStartup(0x00020070, 0) < 0)
            throw new InvalidOperationException("Windows Media Foundation did not start.");
        _started = true;
        _ready = 1;
    }

    private static void ResetEncoder()
    {
        if (_encoder is not null)
        {
            try { Marshal.ReleaseComObject(_encoder); } catch (InvalidComObjectException) { }
            _encoder = null;
        }
    }

    private static void ResetDecoder()
    {
        if (_decoder is not null)
        {
            try { Marshal.ReleaseComObject(_decoder); } catch (InvalidComObjectException) { }
            _decoder = null;
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFAttributes type);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMemoryBuffer(int max, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(Guid category, int flags, IntPtr input, IntPtr output, out IntPtr activates, out int count);

    [ComImport]
    [Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        int GetStreamLimits(out int inputMin, out int inputMax, out int outputMin, out int outputMax);
        int GetStreamCount(out int inputs, out int outputs);
        int GetStreamIDs(int inputs, IntPtr inputIds, int outputs, IntPtr outputIds);
        int GetInputStreamInfo(int id, IntPtr info);
        int GetOutputStreamInfo(int id, ref MFT_OUTPUT_STREAM_INFO info);
        int GetAttributes(out IntPtr attributes);
        int GetInputStreamAttributes(int id, out IntPtr attributes);
        int GetOutputStreamAttributes(int id, out IntPtr attributes);
        int DeleteInputStream(int id);
        int AddInputStreams(int count, IntPtr ids);
        [PreserveSig] int GetInputAvailableType(int index, int typeIndex, out IMFAttributes type);
        [PreserveSig] int GetOutputAvailableType(int index, int typeIndex, out IMFAttributes type);
        [PreserveSig] int SetInputType(int index, IMFAttributes type, int flags);
        [PreserveSig] int SetOutputType(int index, IMFAttributes type, int flags);
        int GetInputCurrentType(int index, out IMFAttributes type);
        [PreserveSig] int GetOutputCurrentType(int index, out IMFAttributes type);
        int GetInputStatus(int index, out int flags);
        int GetOutputStatus(out int flags);
        int SetOutputBounds(long lower, long upper);
        int ProcessEvent(int index, IntPtr mediaEvent);
        [PreserveSig] int ProcessMessage(int message, IntPtr param);
        [PreserveSig] int ProcessInput(int index, IMFSample sample, int flags);
        [PreserveSig] int ProcessOutput(int flags, int count, ref MFT_OUTPUT_DATA_BUFFER buffers, out int status);
    }

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        int GetItem(Guid key, IntPtr value);
        int GetItemType(Guid key, out int type);
        int CompareItem(Guid key, IntPtr value, out int result);
        int Compare(IntPtr other, int flags, out int result);
        int GetUINT32(Guid key, out int value);
        [PreserveSig] int GetUINT64(Guid key, out ulong value);
        int GetDouble(Guid key, out double value);
        int GetGUID(Guid key, out Guid value);
        int GetStringLength(Guid key, out int length);
        int GetString(Guid key, nint buffer, int length, out int written);
        int GetAllocatedString(Guid key, out nint buffer, out int length);
        int GetBlobSize(Guid key, out int size);
        int GetBlob(Guid key, nint buffer, int size, out int written);
        int GetAllocatedBlob(Guid key, out nint buffer, out int size);
        int GetUnknown(Guid key, Guid iid, out nint unknown);
        int SetItem(Guid key, IntPtr value);
        int DeleteItem(Guid key);
        int DeleteAllItems();
        int SetUINT32(Guid key, int value);
        int SetUINT64(Guid key, ulong value);
        int SetDouble(Guid key, double value);
        int SetGUID(Guid key, Guid value);
        int SetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(Guid key, nint buffer, int size);
        int SetUnknown(Guid key, nint unknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out int count);
        int GetItemByIndex(int index, out Guid key, IntPtr value);
        int CopyAllItems(nint destination);
    }

    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        int GetItem(Guid key, IntPtr value);
        int GetItemType(Guid key, out int type);
        int CompareItem(Guid key, IntPtr value, out int result);
        int Compare(IntPtr other, int flags, out int result);
        int GetUINT32(Guid key, out int value);
        [PreserveSig] int GetUINT64(Guid key, out ulong value);
        int GetDouble(Guid key, out double value);
        int GetGUID(Guid key, out Guid value);
        int GetStringLength(Guid key, out int length);
        int GetString(Guid key, nint buffer, int length, out int written);
        int GetAllocatedString(Guid key, out nint buffer, out int length);
        int GetBlobSize(Guid key, out int size);
        int GetBlob(Guid key, nint buffer, int size, out int written);
        int GetAllocatedBlob(Guid key, out nint buffer, out int size);
        int GetUnknown(Guid key, Guid iid, out nint unknown);
        int SetItem(Guid key, IntPtr value);
        int DeleteItem(Guid key);
        int DeleteAllItems();
        int SetUINT32(Guid key, int value);
        int SetUINT64(Guid key, long value);
        int SetDouble(Guid key, double value);
        int SetGUID(Guid key, Guid value);
        int SetString(Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(Guid key, nint buffer, int size);
        int SetUnknown(Guid key, nint unknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out int count);
        int GetItemByIndex(int index, out Guid key, IntPtr value);
        int CopyAllItems(nint destination);
        int GetSampleFlags(out int flags);
        int SetSampleFlags(int flags);
        int GetSampleTime(out long time);
        int SetSampleTime(long time);
        int GetSampleDuration(out long duration);
        int SetSampleDuration(long duration);
        int GetBufferCount(out int count);
        int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        int AddBuffer(IMFMediaBuffer buffer);
        int RemoveBufferByIndex(int index);
        int RemoveAllBuffers();
        int GetTotalLength(out int length);
        int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        int Lock(out IntPtr data, out int max, out int current);
        int Unlock();
        int GetCurrentLength(out int length);
        int SetCurrentLength(int length);
        int GetMaxLength(out int max);
    }

    [ComImport]
    [Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate : IMFAttributes
    {
        int ActivateObject(Guid iid, out IntPtr instance);
        int ShutdownObject();
        int DetachObject();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterType
    {
        public Guid Major;
        public Guid Subtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_STREAM_INFO
    {
        public int dwFlags;
        public int cbSize;
        public int cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_DATA_BUFFER
    {
        public int dwStreamID;
        public IMFSample? pSample;
        public int dwStatus;
        public IntPtr pEvents;
    }
}
