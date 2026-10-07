using System.Runtime.InteropServices;

namespace DriverTest;

internal static class CoreAudio
{
    internal const uint Loopback = 0x00020000;
    internal static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static readonly Guid RenderClientId = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    internal static readonly Guid CaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    static readonly Guid EnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    internal static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException(operation + $" failed, HRESULT 0x{result:X8}", result);
    }

    internal static IMMDevice OpenRenderDevice(string? id)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorId, true)!)!;
        try
        {
            IMMDevice device;
            if (string.IsNullOrWhiteSpace(id) || id == "default") Check(enumerator.GetDefaultAudioEndpoint(0, 1, out device), "Get default multimedia render endpoint");
            else Check(enumerator.GetDevice(id, out device), "Get render endpoint by ID");
            Check(((IMMEndpoint)device).GetDataFlow(out int flow), "Get endpoint data flow");
            if (flow != 0) { Marshal.ReleaseComObject(device); throw new InvalidOperationException("Only render endpoints are permitted. Microphones and other recording endpoints are never opened."); }
            return device;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    internal static IAudioClient Activate(IMMDevice device)
    {
        Guid iid = AudioClientId;
        Check(device.Activate(ref iid, 23, IntPtr.Zero, out object client), "Activate IAudioClient");
        return (IAudioClient)client;
    }

    internal static EndpointInfo Describe(IMMDevice device)
    {
        Check(device.GetId(out string id), "Get endpoint ID");
        Check(device.GetState(out uint state), "Get endpoint state");
        string name = id;
        Check(device.OpenPropertyStore(0, out var store), "Open endpoint property store");
        try
        {
            var key = FriendlyName;
            if (store.GetValue(ref key, out var value) >= 0)
            {
                try { if (value.VariantType == 31) name = Marshal.PtrToStringUni(value.Pointer) ?? id; }
                finally { PropVariantClear(ref value); }
            }
        }
        finally { Marshal.ReleaseComObject(store); }
        var client = Activate(device);
        IntPtr pointer = IntPtr.Zero;
        try
        {
            Check(client.GetMixFormat(out pointer), "Get shared render mix format");
            var format = WaveFormat.Parse(pointer);
            Check(client.GetDevicePeriod(out long normal, out long minimum), "Get endpoint period");
            bool virtualEndpoint = name.Contains("FxSound", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("CABLE", StringComparison.OrdinalIgnoreCase);
            return new EndpointInfo(id, name, state, virtualEndpoint, format.Description, normal / 10000.0, minimum / 10000.0);
        }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); Marshal.ReleaseComObject(client); }
    }

    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey
    {
        public Guid FormatId; public uint Id;
        public PropertyKey(Guid formatId, uint id) { FormatId = formatId; Id = id; }
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint mode, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMEndpoint { [PreserveSig] int GetDataFlow(out int flow); }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

internal sealed record EndpointInfo(string Id, string Name, uint State, bool IsVirtual, object MixFormat, double DefaultPeriodMilliseconds, double MinimumPeriodMilliseconds);

internal sealed class WaveFormat
{
    public int Rate, Channels, Bits, Align, Encoding, ValidBits;
    public uint Mask;
    public object Description => new { SampleRate = Rate, Channels, BitsPerSample = Bits, ValidBits, BlockAlign = Align, ChannelMask = $"0x{Mask:X}", Encoding = Encoding == 3 ? "IEEE float" : "PCM" };
    public static WaveFormat Parse(IntPtr p)
    {
        var format = new WaveFormat
        {
            Encoding = (ushort)Marshal.ReadInt16(p), Channels = (ushort)Marshal.ReadInt16(p, 2), Rate = Marshal.ReadInt32(p, 4),
            Align = (ushort)Marshal.ReadInt16(p, 12), Bits = (ushort)Marshal.ReadInt16(p, 14)
        };
        format.ValidBits = format.Bits;
        if (format.Encoding == 0xfffe)
        {
            if ((ushort)Marshal.ReadInt16(p, 16) < 22) throw new InvalidDataException("Invalid extensible mix format.");
            format.ValidBits = (ushort)Marshal.ReadInt16(p, 18); format.Mask = (uint)Marshal.ReadInt32(p, 20);
            byte[] guid = new byte[16]; Marshal.Copy(p + 24, guid, 0, 16); var subtype = new Guid(guid);
            format.Encoding = subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? 3 : subtype == new Guid("00000001-0000-0010-8000-00aa00389b71") ? 1 : 0;
        }
        if (format.Channels < 1 || format.Channels > 32 || format.Rate < 8000 || format.Rate > 384000 || format.Align != format.Channels * format.Bits / 8 ||
            !(format.Encoding == 3 && format.Bits is 32 or 64 || format.Encoding == 1 && format.Bits is 8 or 16 or 24 or 32))
            throw new InvalidDataException("Unsupported shared-mode mix format.");
        return format;
    }
    public unsafe float Decode(IntPtr pointer, int frame, int channel)
    {
        byte* p = (byte*)pointer + frame * Align + channel * Bits / 8;
        if (Encoding == 3) return Bits == 32 ? *(float*)p : (float)*(double*)p;
        return Bits switch
        {
            8 => (*p - 128) / 128f,
            16 => *(short*)p / 32768f,
            24 => (((p[0] | p[1] << 8 | p[2] << 16) << 8) >> 8) / 8388608f,
            32 => (float)(*(int*)p / 2147483648.0), _ => 0
        };
    }
    public unsafe void Encode(IntPtr pointer, int frame, int channel, float sample)
    {
        byte* p = (byte*)pointer + frame * Align + channel * Bits / 8;
        float value = Math.Clamp(sample, -1f, 1f);
        if (Encoding == 3) { if (Bits == 32) *(float*)p = value; else *(double*)p = value; return; }
        switch (Bits)
        {
            case 8: *p = (byte)Math.Clamp((int)Math.Round(value * 128 + 128), 0, 255); break;
            case 16: *(short*)p = (short)Math.Clamp((int)Math.Round(value * 32768), -32768, 32767); break;
            case 24:
                int n = (int)Math.Clamp(Math.Round(value * 8388608.0), -8388608, 8388607);
                p[0] = (byte)n; p[1] = (byte)(n >> 8); p[2] = (byte)(n >> 16); break;
            case 32: *(int*)p = (int)Math.Clamp(Math.Round(value * 2147483648.0), -2147483648, 2147483647); break;
        }
    }
}
