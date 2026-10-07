using System.Diagnostics;
using System.Runtime.InteropServices;
using ViperPc;

namespace DriverTest;

internal static class BridgeTest
{
    internal static Analysis Run(string sourceId, string destinationId, string mode, string folder)
    {
        using var input = new WindowsAudio.DeviceLease(WindowsAudio.OpenRenderDevice(sourceId));
        using var output = new WindowsAudio.DeviceLease(WindowsAudio.OpenRenderDevice(destinationId));
        var source = CoreAudio.OpenRenderDevice(sourceId); var destination = CoreAudio.OpenRenderDevice(destinationId);
        var renderAudio = CoreAudio.Activate(source); var captureAudio = CoreAudio.Activate(destination);
        CoreAudio.IAudioRenderClient? render = null; CoreAudio.IAudioCaptureClient? capture = null;
        IntPtr renderFormat = IntPtr.Zero, captureFormat = IntPtr.Zero;
        try
        {
            CoreAudio.Check(renderAudio.GetMixFormat(out renderFormat), "Source format"); CoreAudio.Check(captureAudio.GetMixFormat(out captureFormat), "Physical capture format");
            var rf = WaveFormat.Parse(renderFormat); var cf = WaveFormat.Parse(captureFormat);
            CoreAudio.Check(renderAudio.Initialize(0, 0, 600000, 0, renderFormat, IntPtr.Zero), "Test source render");
            CoreAudio.Check(captureAudio.Initialize(0, CoreAudio.Loopback, 600000, 0, captureFormat, IntPtr.Zero), "Physical output loopback");
            var iid = CoreAudio.RenderClientId; CoreAudio.Check(renderAudio.GetService(ref iid, out var r), "Source renderer"); render = (CoreAudio.IAudioRenderClient)r;
            iid = CoreAudio.CaptureClientId; CoreAudio.Check(captureAudio.GetService(ref iid, out var c), "Physical loopback capture"); capture = (CoreAudio.IAudioCaptureClient)c;
            CoreAudio.Check(renderAudio.GetBufferSize(out uint bufferFrames), "Source buffer");
            int sourceFrames = (int)(rf.Rate * LoopbackTest.SignalSeconds), sent = 0, packets = 0, discontinuities = 0, timestamps = 0;
            ulong? first = null, qpc = null; var samples = new List<float>();
            CoreAudio.Check(captureAudio.Start(), "Start physical monitor");
            CoreAudio.Check(render.GetBuffer(bufferFrames, out var buffer), "Tone buffer"); LoopbackTest.Fill(buffer, bufferFrames, rf, sourceFrames, ref sent); CoreAudio.Check(render.ReleaseBuffer(bufferFrames, 0), "Tone submission");
            CoreAudio.Check(renderAudio.Start(), "Start virtual tone playback");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < LoopbackTest.SignalSeconds + .7)
            {
                LoopbackTest.Drain(capture, cf, samples, ref packets, ref discontinuities, ref timestamps, ref first, ref qpc);
                CoreAudio.Check(renderAudio.GetCurrentPadding(out uint padding), "Source padding");
                uint available = bufferFrames - Math.Min(bufferFrames, padding);
                if (available > 0) { CoreAudio.Check(render.GetBuffer(available, out buffer), "Source buffer"); LoopbackTest.Fill(buffer, available, rf, sourceFrames, ref sent); CoreAudio.Check(render.ReleaseBuffer(available, 0), "Source submission"); }
                Thread.Sleep(3);
            }
            LoopbackTest.Drain(capture, cf, samples, ref packets, ref discontinuities, ref timestamps, ref first, ref qpc);
            var data = samples.ToArray(); WaveFile.Write(Path.Combine(folder, mode + "-physical.wav"), cf.Rate, data);
            return LoopbackTest.Analyze(data, cf.Rate);
        }
        finally
        {
            renderAudio.Stop(); captureAudio.Stop();
            if (render != null) Marshal.ReleaseComObject(render); if (capture != null) Marshal.ReleaseComObject(capture);
            Marshal.ReleaseComObject(renderAudio); Marshal.ReleaseComObject(captureAudio); Marshal.ReleaseComObject(source); Marshal.ReleaseComObject(destination);
            if (renderFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(renderFormat); if (captureFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(captureFormat);
        }
    }
}
