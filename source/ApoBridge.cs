using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ViperPc;

public sealed class ApoBridge : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileMapping(IntPtr file, ref SecurityAttributes sa, uint protection, uint high, uint size, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint hi, uint lo, UIntPtr size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateEvent(ref SecurityAttributes sa, bool manualReset, bool initial, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetEvent(IntPtr ev);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    static unsafe int InterlockedExchange(IntPtr address, int value) => Interlocked.Exchange(ref *(int*)address, value);
    IntPtr paramsMap, statusMap, bulkMap, paramsView, statusView, bulkView, changedEvent, bulkEvent;
    uint sequence, ddcSequence, irSequence;
    readonly object gate = new();
    float[]? publishedDdc44, publishedDdc48, publishedIr;
    uint publishedKernelId;
    bool assetsPublished;
    public bool Connected => paramsView != IntPtr.Zero;
    public string Error { get; private set; } = "";
    public int NativeErrorCode { get; private set; }
    const int Magic = 0x534D3456;
    public ApoBridge(bool testOnly = false)
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            if (!testOnly && Process.GetProcessesByName("viper4windows").Length > 0) throw new InvalidOperationException("Закройте приложение ViPER4Windows из установщика, включая его значок в трее. Два пульта не могут одновременно управлять драйвером.");
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;WD)(A;;GA;;;BA)(A;;GA;;;SY)S:(ML;;NW;;;LW)", 1, out sd, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd };
            string prefix = testOnly ? "Local\\ViPER4Windows_RE_Test_" : "Global\\ViPER4Windows_";
            (paramsMap, paramsView) = Map(prefix + "Params", 4096, ref sa);
            (statusMap, statusView) = Map(prefix + "Status", 256, ref sa);
            (bulkMap, bulkView) = Map(prefix + "BulkData", 4 * 1024 * 1024, ref sa);
            changedEvent = CreateEvent(ref sa, false, false, prefix + "ParamsChanged");
            bulkEvent = CreateEvent(ref sa, false, false, prefix + "BulkDataReady");
            if (changedEvent == IntPtr.Zero || bulkEvent == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.WriteInt32(paramsView, 0, Magic); Marshal.WriteInt32(paramsView, 4, 2);
            sequence = (uint)Marshal.ReadInt32(paramsView, 12);
            ddcSequence = (uint)Marshal.ReadInt32(bulkView, 8);
            irSequence = (uint)Marshal.ReadInt32(bulkView, 2 * 1024 * 1024 + 8);
        }
        catch (Exception e) { NativeErrorCode = e is Win32Exception w ? w.NativeErrorCode : 0; Error = e.Message; Dispose(); }
        finally { if (sd != IntPtr.Zero) LocalFree(sd); }
    }
    static (IntPtr, IntPtr) Map(string name, uint size, ref SecurityAttributes sa)
    {
        var map = CreateFileMapping(new IntPtr(-1), ref sa, 4, 0, size, name);
        if (map == IntPtr.Zero) { int code = Marshal.GetLastWin32Error(); throw new Win32Exception(code, $"{name}: {new Win32Exception(code).Message} (код {code})."); }
        var view = MapViewOfFile(map, 0xF001F, 0, 0, (UIntPtr)size);
        if (view == IntPtr.Zero) { CloseHandle(map); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return (map, view);
    }
    public void Publish(DspEngine engine)
    {
        lock (gate)
        {
            if (!Connected) return;
            var data = engine.Snapshot(out bool enabled);
            if (data.Length != 1144 || 24 + 2 * data.Length > 4096) throw new InvalidOperationException("Параметры не соответствуют драйверу ViPER4Windows 2.0.1.");
            // Bulk assets are loaded before a snapshot can enable their effects.
            PublishAssetsCore(engine);
            int current = Marshal.ReadInt32(paramsView, 8) == 0 ? 0 : 1;
            int next = 1 - current;
            Marshal.Copy(data, 0, paramsView + 24 + next * data.Length, data.Length);
            Thread.MemoryBarrier();
            InterlockedExchange(paramsView + 8, next);
            InterlockedExchange(paramsView + 16, enabled ? 1 : 0);
            InterlockedExchange(paramsView + 12, unchecked((int)++sequence));
            if (!SetEvent(changedEvent)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    public void PublishAssets(DspEngine engine)
    {
        lock (gate) { if (Connected) PublishAssetsCore(engine); }
    }
    void PublishAssetsCore(DspEngine engine)
    {
        var assets = engine.SnapshotAssets();
        var a = assets.Ddc44; var b = assets.Ddc48; var ir = assets.Ir;
        ValidateBulkLength(a.Length + b.Length); ValidateBulkLength(ir.Length);
        if (!assetsPublished || !ReferenceEquals(publishedDdc44, a) || !ReferenceEquals(publishedDdc48, b))
        {
            var ddc = new float[a.Length + b.Length]; Array.Copy(a, ddc, a.Length); Array.Copy(b, 0, ddc, a.Length, b.Length);
            Bulk(0, 1, ddc, (uint)(a.Length / 5), 0, 0, ref ddcSequence);
            publishedDdc44 = a; publishedDdc48 = b;
        }
        if (!assetsPublished || !ReferenceEquals(publishedIr, ir) || publishedKernelId != assets.KernelId)
        {
            Bulk(2 * 1024 * 1024, 2, ir, (uint)(ir.Length / 2), 2, assets.KernelId, ref irSequence);
            publishedIr = ir; publishedKernelId = assets.KernelId;
        }
        assetsPublished = true;
    }
    static void ValidateBulkLength(int floatCount)
    {
        if (floatCount > (2 * 1024 * 1024 - 32) / 4) throw new InvalidOperationException("Для системного драйвера импульс должен занимать менее 2 МБ (стерео: около 5 секунд). Обработка WAV поддерживает более длинные импульсы.");
    }
    void Bulk(int offset, int command, float[] data, uint arg1, uint arg2, uint arg3, ref uint seq)
    {
        ValidateBulkLength(data.Length);
        var p = bulkView + offset;
        if (data.Length != 0) Marshal.Copy(data, 0, p + 32, data.Length);
        Marshal.WriteInt32(p, 0, Magic); Marshal.WriteInt32(p, 4, 2); Marshal.WriteInt32(p, 12, command); Marshal.WriteInt32(p, 16, data.Length * 4);
        Marshal.WriteInt32(p, 20, (int)arg1); Marshal.WriteInt32(p, 24, (int)arg2); Marshal.WriteInt32(p, 28, unchecked((int)arg3));
        Thread.MemoryBarrier(); InterlockedExchange(p + 8, unchecked((int)++seq)); if (!SetEvent(bulkEvent)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public (bool Configured, int SampleRate, long Frames, string Version) ReadStatus()
    {
        lock (gate)
        {
            if (statusView == IntPtr.Zero || Marshal.ReadInt32(statusView) != Magic || Marshal.ReadInt32(statusView, 4) != 2) return (false, 0, 0, "");
            return (Marshal.ReadInt32(statusView, 16) != 0, Marshal.ReadInt32(statusView, 20), Marshal.ReadInt64(statusView, 24), Marshal.PtrToStringAnsi(statusView + 32, 32)?.TrimEnd('\0') ?? "");
        }
    }
    public void Disable()
    {
        lock (gate)
        {
            if (!Connected) return;
            InterlockedExchange(paramsView + 16, 0);
            InterlockedExchange(paramsView + 12, unchecked((int)++sequence));
            SetEvent(changedEvent);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            foreach (var p in new[] { paramsView, statusView, bulkView }) if (p != IntPtr.Zero) UnmapViewOfFile(p);
            foreach (var h in new[] { paramsMap, statusMap, bulkMap, changedEvent, bulkEvent }) if (h != IntPtr.Zero) CloseHandle(h);
            paramsView = statusView = bulkView = paramsMap = statusMap = bulkMap = changedEvent = bulkEvent = IntPtr.Zero;
        }
    }
}
