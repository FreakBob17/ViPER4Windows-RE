using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ViperPc;

internal static class WindowsAudio
{
    internal const uint Loopback = 0x00020000;
    internal static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static readonly Guid RenderClientId = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    internal static readonly Guid CaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    internal static readonly Guid ClockAdjustmentId = new("F6E4C0A0-46D9-4FB8-BE21-57A3EF2B626C");
    static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    static readonly Guid EnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    internal static List<RenderEndpoint> Outputs()
    {
        var result = new List<RenderEndpoint>();
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorId, true)!)!;
        IMMDeviceCollection? devices = null;
        try
        {
            Check(enumerator.EnumAudioEndpoints(0, 1, out devices), "Перечисление аудиовыходов");
            Check(devices.GetCount(out uint count), "Число аудиовыходов");
            for (uint i = 0; i < count; i++)
            {
                Check(devices.Item(i, out var device), "Аудиовыход");
                try
                {
                    Check(device.GetId(out string id), "Имя аудиовыхода");
                    Check(device.OpenPropertyStore(0, out var store), "Свойства аудиовыхода");
                    string name;
                    try { name = ReadString(store, FriendlyName); } finally { Marshal.ReleaseComObject(store); }
                    string adapter = "", hardware = "";
                    using (var registry = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + id[(id.LastIndexOf('.') + 1)..] + @"\Properties"))
                    {
                        adapter = registry?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string ?? "";
                        hardware = registry?.GetValue("{a8b865dd-2e3d-4094-ad97-e593a70c75d6},8") as string ?? "";
                    }
                    bool fxSound = adapter.Contains("FxSound", StringComparison.OrdinalIgnoreCase) || hardware.Equals("Root\\FXVAD", StringComparison.OrdinalIgnoreCase);
                    bool virtualDevice = fxSound || name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || name.Contains("Voicemod", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
                    result.Add(new RenderEndpoint(id, name.Length == 0 ? id : name, virtualDevice, fxSound, adapter));
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            return result;
        }
        finally { if (devices != null) Marshal.ReleaseComObject(devices); Marshal.ReleaseComObject(enumerator); }
    }
    internal static string ReadString(IPropertyStore store, PropertyKey key)
    {
        Check(store.GetValue(ref key, out var value), "Чтение свойства выхода");
        try { return value.VariantType == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
        finally { PropVariantClear(ref value); }
    }
    internal static string DefaultId(int role)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorId, true)!)!;
        try { Check(enumerator.GetDefaultAudioEndpoint(0, role, out var device), "Выход по умолчанию"); try { Check(device.GetId(out string id), "ID выхода"); return id; } finally { Marshal.ReleaseComObject(device); } }
        finally { Marshal.ReleaseComObject(enumerator); }
    }
    internal static void SetDefault(string id, int role)
    {
        using var check = new DeviceLease(OpenRenderDevice(id));
        var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!)!;
        try { Check(policy.SetDefaultEndpoint(id, role), "Назначение выхода Windows"); }
        finally { Marshal.ReleaseComObject(policy); }
        if (DefaultId(role) != id) throw new InvalidOperationException("Windows не переключил выход по умолчанию.");
    }
    internal static void Rename(string id, string name)
    {
        if (name.Length is < 1 or > 128) throw new ArgumentException("Недопустимое имя выхода.");
        using var lease = new DeviceLease(OpenRenderDevice(id));
        // Modern Windows uses the bFxStore argument on this compatibility interface.
        var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!)!;
        var value = new PropVariant { VariantType = 31, Pointer = Marshal.StringToCoTaskMemUni(name) };
        try { var key = new PropertyKey(FriendlyName.FormatId, 2); Check(policy.SetPropertyValue(id, false, ref key, ref value), "Имя виртуального устройства"); }
        finally { PropVariantClear(ref value); Marshal.ReleaseComObject(policy); }
        Check(lease.Device.OpenPropertyStore(0, out var store), "Проверка имени");
        try { if (ReadString(store, new PropertyKey(FriendlyName.FormatId, 2)) != name) throw new InvalidOperationException("Windows не сохранил имя виртуального устройства."); }
        finally { Marshal.ReleaseComObject(store); }
    }
    internal sealed class DeviceLease : IDisposable
    {
        internal IMMDevice Device;
        internal DeviceLease(IMMDevice device) { Device = device; }
        public void Dispose() { Marshal.ReleaseComObject(Device); }
    }
    internal static IntPtr FloatFormat()
    {
        var p = Marshal.AllocCoTaskMem(18); Marshal.Copy(new byte[18], 0, p, 18);
        Marshal.WriteInt16(p, 3); Marshal.WriteInt16(p, 2, 2); Marshal.WriteInt32(p, 4, 48000);
        Marshal.WriteInt32(p, 8, 384000); Marshal.WriteInt16(p, 12, 8); Marshal.WriteInt16(p, 14, 32);
        return p;
    }

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
    internal static bool RawOutput(IAudioClient client)
    {
        // RAW skips stream and mode effects; endpoint/hardware processing can remain.
        var properties = new AudioClientProperties { Size = 16, Category = 1, Options = 1 };
        var extended = (IAudioClient2)client;
        int result = extended.SetClientProperties(ref properties);
        if (result == unchecked((int)0x88890027)) // AUDCLNT_E_RAW_MODE_UNSUPPORTED
        {
            // Some USB interfaces expose only the standard shared-mode path.
            properties.Options = 0;
            Check(extended.SetClientProperties(ref properties), "Обычный выход Windows");
            return false;
        }
        Check(result, "Прямой выход Windows");
        return true;
    }
    internal static bool PostVolumeCapture(IAudioClient client)
    {
        var properties = new AudioClientProperties { Size = 16, Category = 1, Options = 8 };
        int result = ((IAudioClient2)client).SetClientProperties(ref properties);
        if (result == unchecked((int)0x88890043) || result == unchecked((int)0x80070057)) return false;
        Check(result, "Захват с учётом громкости Windows"); return true;
    }
    internal static IAudioEndpointVolume Volume(IMMDevice device)
    {
        var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        Check(device.Activate(ref iid, 23, IntPtr.Zero, out var service), "Громкость устройства"); return (IAudioEndpointVolume)service;
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
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int mode, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int mode, IntPtr normal, IntPtr minimum);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
    [ComImport, Guid("F6E4C0A0-46D9-4FB8-BE21-57A3EF2B626C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClockAdjustment { [PreserveSig] int SetSampleRate(float rate); }
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
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientProperties { public uint Size; public int Offload; public int Category; public uint Options; }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, IntPtr context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float scalar, IntPtr context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float scalar);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, IntPtr context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float scalar, IntPtr context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float scalar);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(IntPtr context);
        [PreserveSig] int VolumeStepDown(IntPtr context);
        [PreserveSig] int QueryHardwareSupport(out uint support);
        [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }
    [ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient2
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
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
        [PreserveSig] int IsOffloadCapable(int category, [MarshalAs(UnmanagedType.Bool)] out bool capable);
        [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minimum, out long maximum);
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

public sealed record RenderEndpoint(string Id, string Name, bool IsVirtual, bool IsFxSound, string Adapter)
{
    public override string ToString() => Name;
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
