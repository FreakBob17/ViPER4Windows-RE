using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ViperPc;

public sealed record OutputEndpoint(string Id, string Name, bool Headphones);

/// <summary>Reads the Windows multimedia output identity without opening or changing audio.</summary>
[SupportedOSPlatform("windows")]
public static class CoreAudioProbe
{
    static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    static readonly PropertyKey FormFactor = new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);

    public static OutputEndpoint? DefaultOutput()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        IntPtr idPointer = IntPtr.Zero;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClass, throwOnError: false);
            if (type == null) return null;
            enumerator = Activator.CreateInstance(type) as IMMDeviceEnumerator;
            // EDataFlow.eRender = 0, ERole.eMultimedia = 1.
            if (enumerator == null || enumerator.GetDefaultAudioEndpoint(0, 1, out device) < 0 || device == null) return null;
            if (device.GetId(out idPointer) < 0 || idPointer == IntPtr.Zero) return null;
            string id = Marshal.PtrToStringUni(idPointer) ?? "";
            if (string.IsNullOrEmpty(id)) return null;
            string name = id;
            bool headphones = false;
            if (device.OpenPropertyStore(0, out store) >= 0 && store != null)
            {
                name = ReadProperty(store, FriendlyName) as string ?? id;
                object? factor = ReadProperty(store, FormFactor);
                // EndpointFormFactor.Headphones = 3, Headset = 5.
                headphones = factor is uint value && (value == 3 || value == 5);
            }
            return new OutputEndpoint(id, name, headphones);
        }
        catch (Exception)
        {
            // Endpoint removal and unavailable audio services are normal during polling.
            return null;
        }
        finally
        {
            if (idPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(idPointer);
            Release(store); Release(device); Release(enumerator);
        }
    }

    static object? ReadProperty(IPropertyStore store, PropertyKey key)
    {
        // PROPVARIANT has an 8-byte header and an architecture-dependent union.
        int size = IntPtr.Size == 8 ? 24 : 16;
        IntPtr variant = Marshal.AllocCoTaskMem(size);
        Marshal.Copy(new byte[size], 0, variant, size);
        try
        {
            if (store.GetValue(ref key, variant) < 0) return null;
            int type = (ushort)Marshal.ReadInt16(variant);
            return type switch
            {
                31 => Marshal.PtrToStringUni(Marshal.ReadIntPtr(variant, 8)), // VT_LPWSTR
                8 => Marshal.PtrToStringBSTR(Marshal.ReadIntPtr(variant, 8)), // VT_BSTR
                19 => unchecked((uint)Marshal.ReadInt32(variant, 8)), // VT_UI4
                _ => null
            };
        }
        finally
        {
            PropVariantClear(variant);
            Marshal.FreeCoTaskMem(variant);
        }
    }

    static void Release(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); } catch (InvalidComObjectException) { }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern int PropVariantClear(IntPtr variant);

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid Format;
        public uint Id;
        public PropertyKey(Guid format, uint id) { Format = format; Id = id; }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint context, IntPtr parameters, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int Commit();
    }
}
