using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace DriverTest;

internal static class SharedMaps
{
    const uint AllAccess = 0xf001f, Read = 4;
    internal const string Prefix = "Global\\ViPER4Windows_";
    internal static string[] Names = { "Params", "Status", "BulkData", "ParamsChanged", "BulkDataReady" };
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint high, uint low, UIntPtr size);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetEvent(IntPtr handle);

    internal static object[] Probe()
    {
        var results = new List<object>();
        foreach (string suffix in Names)
        {
            bool isEvent = suffix.EndsWith("Changed") || suffix.EndsWith("Ready");
            foreach (bool write in new[] { false, true })
            {
                uint access = isEvent ? write ? 0x100002u : 0x100000u : write ? AllAccess : Read;
                IntPtr handle = isEvent ? OpenEventW(access, false, Prefix + suffix) : OpenFileMappingW(access, false, Prefix + suffix);
                int error = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                results.Add(new { Name = Prefix + suffix, Access = write ? "read-write / signal" : "read / synchronize", Opened = handle != IntPtr.Zero, Win32Error = error, ErrorMessage = error == 0 ? "" : new Win32Exception(error).Message });
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
        return results.ToArray();
    }

    internal static object ReadStatus()
    {
        using var status = Open("Status", 256, false);
        if (status == null) return new { Available = false, Win32Error = Marshal.GetLastWin32Error() };
        return new
        {
            Available = true, Magic = $"0x{Marshal.ReadInt32(status.View):X8}", Protocol = Marshal.ReadInt32(status.View, 4),
            Configured = Marshal.ReadInt32(status.View, 16) != 0, SampleRate = Marshal.ReadInt32(status.View, 20),
            Frames = Marshal.ReadInt64(status.View, 24), Version = Marshal.PtrToStringAnsi(status.View + 32, 32)?.TrimEnd('\0')
        };
    }

    static Map? Open(string suffix, int size, bool write)
    {
        uint access = write ? AllAccess : Read;
        IntPtr handle = OpenFileMappingW(access, false, Prefix + suffix);
        if (handle == IntPtr.Zero) return null;
        IntPtr view = MapViewOfFile(handle, access, 0, 0, (UIntPtr)size);
        if (view == IntPtr.Zero) { int error = Marshal.GetLastWin32Error(); CloseHandle(handle); Marshal.SetLastPInvokeError(error); return null; }
        return new Map(handle, view, size);
    }

    sealed class Map : IDisposable
    {
        readonly IntPtr handle;
        internal readonly IntPtr View;
        internal readonly int Size;
        public Map(IntPtr handle, IntPtr view, int size) { this.handle = handle; View = view; Size = size; }
        internal byte[] Copy() { var bytes = new byte[Size]; Marshal.Copy(View, bytes, 0, Size); return bytes; }
        public void Dispose() { UnmapViewOfFile(View); CloseHandle(handle); }
    }

    internal sealed class SavedState : IDisposable
    {
        Map? parameters, bulk;
        readonly byte[]? parameterBytes, bulkBytes;
        public bool Available => parameters != null && bulk != null && parameterBytes != null &&
            BitConverter.ToInt32(parameterBytes, 0) == 0x534d3456 && BitConverter.ToInt32(parameterBytes, 4) == 2;
        public bool Restored { get; private set; }
        public string Error { get; private set; } = "";
        public SavedState()
        {
            parameters = Open("Params", 4096, true); bulk = Open("BulkData", 4 * 1024 * 1024, true);
            if (parameters != null) parameterBytes = parameters.Copy();
            if (bulk != null) bulkBytes = bulk.Copy();
            if (!Available) Error = "Existing parameter/bulk maps could not both be opened; test modes will be disabled at completion.";
        }
        public unsafe void Restore()
        {
            if (!Available || parameterBytes == null || bulkBytes == null) return;
            try
            {
                foreach (int offset in new[] { 0, 2 * 1024 * 1024 })
                {
                    var p = bulk!.View + offset;
                    int sequence = Marshal.ReadInt32(p, 8);
                    // Keep the live sequence while replacing payload/header; publish only after complete.
                    Marshal.Copy(bulkBytes, offset + 12, p + 12, 2 * 1024 * 1024 - 12);
                    Marshal.Copy(bulkBytes, offset, p, 8);
                    Thread.MemoryBarrier(); Interlocked.Exchange(ref *(int*)(p + 8), unchecked(sequence + 1));
                }
                Signal("BulkDataReady");
                int current = Marshal.ReadInt32(parameters!.View, 8) == 0 ? 0 : 1;
                int saved = BitConverter.ToInt32(parameterBytes, 8) == 0 ? 0 : 1;
                int next = 1 - current;
                Marshal.Copy(parameterBytes, 24 + saved * 1144, parameters.View + 24 + next * 1144, 1144);
                Thread.MemoryBarrier();
                Interlocked.Exchange(ref *(int*)(parameters.View + 8), next);
                Interlocked.Exchange(ref *(int*)(parameters.View + 16), BitConverter.ToInt32(parameterBytes, 16));
                Interlocked.Increment(ref *(int*)(parameters.View + 12));
                Signal("ParamsChanged"); Restored = true;
            }
            catch (Exception error) { Error = error.Message; }
        }
        static void Signal(string suffix)
        {
            IntPtr handle = OpenEventW(0x100002, false, Prefix + suffix);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot signal restored APO state.");
            try { if (!SetEvent(handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { CloseHandle(handle); }
        }
        public void Dispose() { parameters?.Dispose(); bulk?.Dispose(); parameters = bulk = null; }
    }
}
