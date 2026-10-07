using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ViperPc;

public sealed class DspEngine : IDisposable
{
    const string Dll = "ViperDsp.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vp_create(uint rate);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void vp_destroy(IntPtr h);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int vp_set(IntPtr h, int id, int a, int b, int c, int d);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int vp_process(IntPtr h, [In, Out] float[] samples, uint frames);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int vp_reset(IntPtr h);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vp_engine_version();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int vp_load_ir(IntPtr h, float[] samples, uint frames, uint channels);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int vp_load_ddc(IntPtr h, float[] c44, float[] c48, uint sections);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int vp_param_size();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int vp_snapshot(IntPtr h, [Out] byte[] target, uint capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint vp_latency_frames();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern uint vp_convolver_kernel_id(IntPtr h);
    readonly object gate = new();
    IntPtr handle;
    public uint SampleRate { get; }
    bool masterEnabled;
    public bool MasterEnabled { get { lock (gate) return masterEnabled; } set { lock (gate) masterEnabled = value; } }
    public uint LatencyFrames => vp_latency_frames();
    public string Version => Marshal.PtrToStringUTF8(vp_engine_version()) ?? "ViPERDSP";
    public string IrPath { get; private set; } = "";
    public string DdcPath { get; private set; } = "";
    public float[]? IrData { get; private set; }
    public float[]? Ddc44 { get; private set; }
    public float[]? Ddc48 { get; private set; }
    public uint IrKernelId { get; private set; }
    public DspEngine(uint sampleRate = 48000)
    {
        SampleRate = sampleRate; handle = vp_create(sampleRate);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Не удалось создать звуковой движок.");
    }
    public void Set(int id, int a = 0, int b = 0, int c = 0, int d = 0)
    {
        lock (gate) { EnsureOpen(); if (vp_set(handle, id, a, b, c, d) <= 0) throw new InvalidOperationException($"Движок отклонил параметр {id}."); }
    }
    public void Process(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if ((samples.Length & 1) != 0) throw new ArgumentException("Для стерео нужно чётное число отсчётов.", nameof(samples));
        lock (gate) { EnsureOpen(); if (!masterEnabled) return; if (vp_process(handle, samples, (uint)(samples.Length / 2)) <= 0) throw new InvalidOperationException("Ошибка обработки звука."); }
    }
    public void Reset() { lock (gate) { EnsureOpen(); if (vp_reset(handle) <= 0) throw new InvalidOperationException("Не удалось сбросить состояние звукового движка."); } }
    public byte[] Snapshot() => Snapshot(out _);
    public byte[] Snapshot(out bool enabled)
    {
        lock (gate) { EnsureOpen(); enabled = masterEnabled; var data = new byte[vp_param_size()]; int n = vp_snapshot(handle, data, (uint)data.Length); if (n != data.Length || n != 1144) throw new InvalidOperationException("Несовместимый формат параметров APO."); return data; }
    }
    public (float[] Ddc44, float[] Ddc48, float[] Ir, uint KernelId) SnapshotAssets()
    {
        lock (gate) { EnsureOpen(); return (Ddc44 ?? Array.Empty<float>(), Ddc48 ?? Array.Empty<float>(), IrData ?? Array.Empty<float>(), IrKernelId); }
    }
    void EnsureOpen() { if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(DspEngine)); }
    public void LoadIr(string path)
    {
        lock (gate) { EnsureOpen(); if (path == IrPath) return; }
        if (string.IsNullOrWhiteSpace(path)) { lock (gate) { EnsureOpen(); if (vp_load_ir(handle, Array.Empty<float>(), 0, 2) <= 0) throw new InvalidDataException("Не удалось удалить импульс."); IrPath = ""; IrData = null; IrKernelId = 0; } return; }
        var data = WaveFile.Read(path);
        if (data.Samples.Length == 0 || data.Samples.Length > data.SampleRate * 2L * 20 || data.Samples.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Импульс должен содержать конечные отсчёты и быть не длиннее 20 секунд.");
        // The standalone loader pads short kernels; publish the identical padded data to APO.
        var samples = ImpulseResampler.Resample(data.Samples, checked((uint)data.SampleRate), SampleRate);
        if (samples.Length < 32) { var padded = new float[32]; Array.Copy(samples, padded, samples.Length); samples = padded; }
        lock (gate) { EnsureOpen(); if (vp_load_ir(handle, samples, (uint)(samples.Length / 2), 2) <= 0) throw new InvalidDataException("Движок не смог загрузить импульс."); IrPath = path; IrData = samples; IrKernelId = vp_convolver_kernel_id(handle); }
    }
    public void LoadDdc(string path)
    {
        lock (gate) { EnsureOpen(); if (path == DdcPath) return; }
        if (string.IsNullOrWhiteSpace(path)) { lock (gate) { EnsureOpen(); if (vp_load_ddc(handle, Array.Empty<float>(), Array.Empty<float>(), 0) <= 0) throw new InvalidDataException("Не удалось удалить DDC."); DdcPath = ""; Ddc44 = Ddc48 = null; } return; }
        string[] lines = File.ReadAllLines(path);
        float[] Read(string prefix)
        {
            var line = lines.FirstOrDefault(x => x.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"В DDC нет {prefix}");
            return line[(line.IndexOf(':') + 1)..].Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }
        var a = Read("SR_44100:"); var b = Read("SR_48000:");
        if (a.Length == 0 || a.Length != b.Length || a.Length % 5 != 0 || a.Length > 5 * 1024 || a.Any(x => !float.IsFinite(x)) || b.Any(x => !float.IsFinite(x))) throw new InvalidDataException("DDC должен содержать одинаковые наборы конечных коэффициентов, по пять на секцию.");
        lock (gate) { EnsureOpen(); if (vp_load_ddc(handle, a, b, (uint)(a.Length / 5)) <= 0) throw new InvalidDataException("Ошибка загрузки DDC."); DdcPath = path; Ddc44 = a; Ddc48 = b; }
    }
    public void Dispose() { lock (gate) { if (handle != IntPtr.Zero) vp_destroy(handle); handle = IntPtr.Zero; } }
}
