using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WorkshopAgent;

internal static class H264Encoder
{
    private static int _ready = -1;

    public static bool CanEncode
    {
        get
        {
            if (_ready < 0)
                _ready = Probe() ? 1 : 0;
            return _ready == 1;
        }
    }

    public static bool TryEncode(Bitmap frame, out byte[] nal)
    {
        nal = [];
        if (!CanEncode)
            return false;
        try
        {
            nal = Encode(frame);
            return nal.Length > 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Win32Exception)
        {
            _ready = 0;
            return false;
        }
    }

    private static bool Probe()
    {
        try
        {
            if (MFStartup(0x00020070, 0) != 0)
                return false;
            using var sample = new Bitmap(64, 64);
            return Encode(sample).Length > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static byte[] Encode(Bitmap frame)
    {
        var encoder = ActivateEncoder();
        try
        {
            SetType(encoder, false, frame.Width, frame.Height);
            SetType(encoder, true, frame.Width, frame.Height);
            encoder.ProcessMessage(0x10000001, IntPtr.Zero);
            var sample = CreateSample(frame);
            try
            {
                var input = encoder.ProcessInput(0, sample, 0);
                if (input < 0)
                    throw new COMException("The H.264 encoder did not accept the frame.", input);
                return Drain(encoder);
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(encoder);
        }
    }

    private static IMFTransform ActivateEncoder()
    {
        var result = MFTEnumEx(new Guid("f79eac7d-e545-4387-bdee-3cee3e9d3d2d"), 0x00000023, IntPtr.Zero, IntPtr.Zero, out var activates, out var count);
        if (result < 0 || count < 1 || activates == IntPtr.Zero)
            throw new InvalidOperationException("Windows did not offer an H.264 encoder.");
        try
        {
            var activate = Marshal.GetObjectForIUnknown(Marshal.ReadIntPtr(activates));
            var pointer = IntPtr.Zero;
            var hr = ((IMFActivate)activate).ActivateObject(typeof(IMFTransform).GUID, out pointer);
            if (hr < 0 || pointer == IntPtr.Zero)
                throw new COMException("The H.264 encoder did not start.", hr);
            return (IMFTransform)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            for (var index = 0; index < count; index++)
                Marshal.Release(Marshal.ReadIntPtr(activates, index * IntPtr.Size));
            Marshal.FreeCoTaskMem(activates);
        }
    }

    private static void SetType(IMFTransform encoder, bool input, int width, int height)
    {
        MFCreateMediaType(out var type);
        type.SetGUID(new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), input
            ? new Guid("3231564e-0000-0010-8000-00aa00389b71")
            : new Guid("34363248-0000-0010-8000-00aa00389b71"));
        type.SetUINT64(new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), ((ulong)(uint)width << 32) | (uint)height);
        var hr = input ? encoder.SetInputType(0, type, 0) : encoder.SetOutputType(0, type, 0);
        Marshal.ReleaseComObject(type);
        if (hr < 0)
            throw new COMException(input ? "The H.264 input picture was rejected." : "The H.264 output picture was rejected.", hr);
    }

    private static IMFSample CreateSample(Bitmap frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        var nv12 = ToNv12(frame);
        MFCreateMemoryBuffer(nv12.Length, out var buffer);
        buffer.Lock(out var data, out _, out _);
        Marshal.Copy(nv12, 0, data, nv12.Length);
        buffer.Unlock();
        buffer.SetCurrentLength(nv12.Length);
        MFCreateSample(out var sample);
        sample.AddBuffer(buffer);
        Marshal.ReleaseComObject(buffer);
        return sample;
    }

    private static byte[] Drain(IMFTransform encoder)
    {
        var output = new List<byte>();
        while (true)
        {
            var info = new MFT_OUTPUT_STREAM_INFO();
            encoder.GetOutputStreamInfo(0, ref info);
            MFCreateMemoryBuffer((int)Math.Max(info.cbSize, 64 * 1024), out var buffer);
            MFCreateSample(out var sample);
            sample.AddBuffer(buffer);
            var data = new MFT_OUTPUT_DATA_BUFFER { pSample = sample };
            var hr = encoder.ProcessOutput(0, 1, ref data, out _);
            if (hr == unchecked((int)0xC00D6D60))
            {
                Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buffer);
                break;
            }
            if (hr < 0)
            {
                Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buffer);
                throw new COMException("The H.264 encoder produced no picture.", hr);
            }
            buffer.Lock(out var pointer, out _, out var length);
            var chunk = new byte[length];
            Marshal.Copy(pointer, chunk, 0, length);
            buffer.Unlock();
            output.AddRange(chunk);
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }

        return output.ToArray();
    }

    private static byte[] ToNv12(Bitmap frame)
    {
        var width = frame.Width & ~1;
        var height = frame.Height & ~1;
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

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFMediaType type);

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
        int GetInputAvailableType(int index, int typeIndex, out IMFMediaType type);
        int GetOutputAvailableType(int index, int typeIndex, out IMFMediaType type);
        int SetInputType(int index, IMFMediaType type, int flags);
        int SetOutputType(int index, IMFMediaType type, int flags);
        int GetInputCurrentType(int index, out IMFMediaType type);
        int GetOutputCurrentType(int index, out IMFMediaType type);
        int GetInputStatus(int index, out int flags);
        int GetOutputStatus(out int flags);
        int SetOutputBounds(long lower, long upper);
        int ProcessEvent(int index, IntPtr mediaEvent);
        int ProcessMessage(int message, IntPtr param);
        int ProcessInput(int index, IMFSample sample, int flags);
        int ProcessOutput(int flags, int count, ref MFT_OUTPUT_DATA_BUFFER buffers, out int status);
    }

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        int GetItem(Guid key, IntPtr value);
        int GetItemType(Guid key, out int type);
        int CompareItem(Guid key, IntPtr value, out int result);
        int Compare(IntPtr other, int flags, out int result);
        int GetUINT32(Guid key, out int value);
        int GetUINT64(Guid key, out ulong value);
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
        int SetBlob(Guid key, nint buffer, int size);
        int SetUnknown(Guid key, nint unknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out int count);
        int GetItemByIndex(int index, out Guid key, IntPtr value);
        int CopyAllItems(nint destination);
        int GetMajorType(out Guid type);
        int IsCompressedFormat(out int compressed);
        int IsEqual(IntPtr other, out int flags);
        int GetRepresentation(Guid guid, out nint representation);
        int FreeRepresentation(Guid guid, nint representation);
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
        int GetUINT64(Guid key, out ulong value);
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
        int SetBlob(Guid key, nint buffer, int size);
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
        int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
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
    private interface IMFActivate
    {
        int GetItem(Guid key, IntPtr value);
        int GetItemType(Guid key, out int type);
        int CompareItem(Guid key, IntPtr value, out int result);
        int Compare(IntPtr other, int flags, out int result);
        int GetUINT32(Guid key, out int value);
        int GetUINT64(Guid key, out ulong value);
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
        int SetBlob(Guid key, nint buffer, int size);
        int SetUnknown(Guid key, nint unknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out int count);
        int GetItemByIndex(int index, out Guid key, IntPtr value);
        int CopyAllItems(nint destination);
        int ActivateObject(Guid iid, out IntPtr instance);
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
        public object? pSample;
        public int dwStatus;
        public IntPtr pEvents;
    }
}
