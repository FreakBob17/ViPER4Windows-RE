using System.Diagnostics;
using System.Runtime.InteropServices;
using ViperPc;

namespace DriverTest;

internal static class LoopbackTest
{
    internal static readonly double[] Frequencies = { 63, 125, 250, 1000, 4000, 8000 };
    internal const double LeadingSeconds = .35, ToneSeconds = .60, TailSeconds = .60, PeakAmplitude = .015;
    internal const double SignalSeconds = LeadingSeconds + ToneSeconds * 6 + TailSeconds;

    internal static object Prepare(CoreAudio.IMMDevice device)
    {
        var render = CoreAudio.Activate(device); var capture = CoreAudio.Activate(device); IntPtr mix = IntPtr.Zero;
        try
        {
            CoreAudio.Check(render.GetMixFormat(out mix), "Get preparation format");
            CoreAudio.Check(render.Initialize(0, 0, 1000000, 0, mix, IntPtr.Zero), "Prepare shared render without starting playback");
            CoreAudio.Check(capture.Initialize(0, CoreAudio.Loopback, 1000000, 0, mix, IntPtr.Zero), "Prepare speaker loopback without starting capture");
            CoreAudio.Check(render.GetBufferSize(out uint renderFrames), "Get prepared render frames");
            CoreAudio.Check(capture.GetBufferSize(out uint captureFrames), "Get prepared loopback frames");
            return new { Prepared = true, RenderBufferFrames = renderFrames, LoopbackBufferFrames = captureFrames, AudioPlayed = false, CaptureStarted = false };
        }
        finally { if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix); Marshal.ReleaseComObject(capture); Marshal.ReleaseComObject(render); }
    }

    internal static object SelfTest(int rate)
    {
        var samples = new float[(int)Math.Round(rate * SignalSeconds) * 2];
        for (int i = 0; i < samples.Length / 2; i++) samples[i * 2] = samples[i * 2 + 1] = Signal(i, rate);
        var measured = Analyze(samples, rate);
        if (!measured.SignalDetected || measured.Tones.Any(x => Math.Abs(x.Amplitude - PeakAmplitude) > PeakAmplitude * .03 || x.TonePurity < .97))
            throw new InvalidOperationException("Synthetic spectral measurement validation failed.");
        return new { Passed = true, SampleRate = rate, AudioPlayed = false, GeneratedOnlyInMemory = true, Measurements = measured };
    }

    internal static TestResult Run(CoreAudio.IMMDevice device, string mode, string destination, Func<object>? driverStatus)
    {
        var endpoint = CoreAudio.Describe(device);
        var renderAudio = CoreAudio.Activate(device);
        var captureAudio = CoreAudio.Activate(device);
        CoreAudio.IAudioRenderClient? render = null;
        CoreAudio.IAudioCaptureClient? capture = null;
        IntPtr mix = IntPtr.Zero;
        bool renderStarted = false, captureStarted = false;
        object? before = driverStatus?.Invoke();
        try
        {
            CoreAudio.Check(renderAudio.GetMixFormat(out mix), "Get render mix format");
            var format = WaveFormat.Parse(mix);
            CoreAudio.Check(renderAudio.Initialize(0, 0, 1000000, 0, mix, IntPtr.Zero), "Initialize shared render");
            // Capture only the selected SPEAKER endpoint's loopback. No capture device is opened.
            CoreAudio.Check(captureAudio.Initialize(0, CoreAudio.Loopback, 1000000, 0, mix, IntPtr.Zero), "Initialize speaker loopback");
            var iid = CoreAudio.RenderClientId;
            CoreAudio.Check(renderAudio.GetService(ref iid, out object renderService), "Get IAudioRenderClient"); render = (CoreAudio.IAudioRenderClient)renderService;
            iid = CoreAudio.CaptureClientId;
            CoreAudio.Check(captureAudio.GetService(ref iid, out object captureService), "Get IAudioCaptureClient"); capture = (CoreAudio.IAudioCaptureClient)captureService;
            CoreAudio.Check(renderAudio.GetBufferSize(out uint bufferFrames), "Get render buffer size");
            CoreAudio.Check(renderAudio.GetStreamLatency(out long renderLatency), "Get render latency");
            int sourceFrames = (int)Math.Round(format.Rate * SignalSeconds);
            var source = new float[sourceFrames * 2];
            for (int frame = 0; frame < sourceFrames; frame++) source[frame * 2] = source[frame * 2 + 1] = Signal(frame, format.Rate);
            var captured = new List<float>((sourceFrames + format.Rate) * 2);
            int sentFrames = 0, packetCount = 0, discontinuities = 0, timestampErrors = 0;
            ulong? firstDevicePosition = null, firstQpc = null;
            CoreAudio.Check(captureAudio.Start(), "Start speaker loopback"); captureStarted = true;
            CoreAudio.Check(render.GetBuffer(bufferFrames, out IntPtr first), "Get initial render buffer");
            Fill(first, bufferFrames, format, sourceFrames, ref sentFrames);
            CoreAudio.Check(render.ReleaseBuffer(bufferFrames, 0), "Release initial render buffer");
            CoreAudio.Check(renderAudio.Start(), "Start deterministic tone playback"); renderStarted = true;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < SignalSeconds + .4)
            {
                Drain(capture, format, captured, ref packetCount, ref discontinuities, ref timestampErrors, ref firstDevicePosition, ref firstQpc);
                CoreAudio.Check(renderAudio.GetCurrentPadding(out uint padding), "Get shared render padding");
                uint available = bufferFrames - Math.Min(bufferFrames, padding);
                if (available != 0)
                {
                    CoreAudio.Check(render.GetBuffer(available, out IntPtr buffer), "Get render buffer");
                    Fill(buffer, available, format, sourceFrames, ref sentFrames);
                    CoreAudio.Check(render.ReleaseBuffer(available, 0), "Release render buffer");
                }
                Thread.Sleep(3);
            }
            Drain(capture, format, captured, ref packetCount, ref discontinuities, ref timestampErrors, ref firstDevicePosition, ref firstQpc);
            CoreAudio.Check(renderAudio.Stop(), "Stop test playback"); renderStarted = false;
            CoreAudio.Check(captureAudio.Stop(), "Stop speaker loopback"); captureStarted = false;
            object? after = driverStatus?.Invoke();
            float[] samples = captured.ToArray();
            string sourcePath = Path.Combine(destination, "source.wav"), capturePath = Path.Combine(destination, mode + "-loopback.wav");
            WaveFile.Write(sourcePath, format.Rate, source); WaveFile.Write(capturePath, format.Rate, samples);
            var analysis = Analyze(samples, format.Rate);
            return new TestResult(mode, endpoint, format.Rate, samples.Length / 2, sourceFrames, PeakAmplitude, renderLatency / 10000.0,
                packetCount, discontinuities, timestampErrors, firstDevicePosition, firstQpc, sourcePath, capturePath, before, after, analysis);
        }
        finally
        {
            if (renderStarted) renderAudio.Stop(); if (captureStarted) captureAudio.Stop();
            if (capture != null) Marshal.ReleaseComObject(capture); if (render != null) Marshal.ReleaseComObject(render);
            Marshal.ReleaseComObject(captureAudio); Marshal.ReleaseComObject(renderAudio); if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
        }
    }

    internal static void Fill(IntPtr data, uint frames, WaveFormat format, int sourceFrames, ref int position)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            float sample = position < sourceFrames ? Signal(position, format.Rate) : 0;
            for (int channel = 0; channel < format.Channels; channel++) format.Encode(data, frame, channel, channel < 2 ? sample : 0);
            position++;
        }
    }

    static float Signal(int frame, int rate)
    {
        double time = (double)frame / rate - LeadingSeconds;
        if (time < 0 || time >= ToneSeconds * Frequencies.Length) return 0;
        int slot = (int)(time / ToneSeconds);
        double local = time - slot * ToneSeconds;
        double fade = Math.Min(1, Math.Min(local / .015, (ToneSeconds - local) / .015));
        return (float)(PeakAmplitude * Math.Max(0, fade) * Math.Sin(2 * Math.PI * Frequencies[slot] * local));
    }

    internal static void Drain(CoreAudio.IAudioCaptureClient capture, WaveFormat format, List<float> output, ref int packets, ref int discontinuities,
        ref int timestampErrors, ref ulong? firstPosition, ref ulong? firstQpc)
    {
        CoreAudio.Check(capture.GetNextPacketSize(out uint next), "Get loopback packet size");
        while (next > 0)
        {
            CoreAudio.Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc), "Get speaker loopback packet");
            try
            {
                packets++; if ((flags & 1) != 0) discontinuities++; if ((flags & 4) != 0) timestampErrors++;
                firstPosition ??= position; firstQpc ??= qpc;
                bool silent = (flags & 2) != 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    float left = silent ? 0 : format.Decode(data, frame, 0);
                    float right = silent ? 0 : format.Decode(data, frame, Math.Min(1, format.Channels - 1));
                    output.Add(float.IsFinite(left) ? left : 0); output.Add(float.IsFinite(right) ? right : 0);
                }
            }
            finally { CoreAudio.Check(capture.ReleaseBuffer(frames), "Release loopback packet"); }
            CoreAudio.Check(capture.GetNextPacketSize(out next), "Get next loopback packet size");
        }
    }

    internal static Analysis Analyze(float[] samples, int rate)
    {
        int frames = samples.Length / 2, window = Math.Max(1, rate / 100);
        float[] mono = new float[frames];
        double sum = 0, peak = 0, strongestWindow = 0;
        for (int i = 0; i < frames; i++) { mono[i] = (samples[i * 2] + samples[i * 2 + 1]) / 2; sum += mono[i] * mono[i]; peak = Math.Max(peak, Math.Abs(mono[i])); }
        for (int i = 0; i + window <= frames; i += window)
        {
            double power = 0; for (int j = 0; j < window; j++) power += mono[i + j] * mono[i + j];
            strongestWindow = Math.Max(strongestWindow, Math.Sqrt(power / window));
        }
        int firstActive = -1;
        for (int i = 0; i + window <= frames; i += window)
        {
            double power = 0; for (int j = 0; j < window; j++) power += mono[i + j] * mono[i + j];
            if (strongestWindow > 1e-9 && Math.Sqrt(power / window) > strongestWindow * .15) { firstActive = i; break; }
        }
        double offset = firstActive < 0 ? 0 : (double)firstActive / rate - LeadingSeconds;
        var bands = new List<ToneMeasurement>();
        for (int slot = 0; slot < Frequencies.Length; slot++)
        {
            int first = (int)Math.Round(rate * (offset + LeadingSeconds + slot * ToneSeconds + .15));
            int last = (int)Math.Round(rate * (offset + LeadingSeconds + slot * ToneSeconds + .45));
            first = Math.Clamp(first, 0, frames); last = Math.Clamp(last, first, frames);
            double sine = 0, cosine = 0, power = 0;
            for (int i = first; i < last; i++)
            {
                double phase = 2 * Math.PI * Frequencies[slot] * i / rate;
                sine += mono[i] * Math.Sin(phase); cosine += mono[i] * Math.Cos(phase); power += mono[i] * mono[i];
            }
            int count = last - first;
            double amplitude = count > 0 ? 2 * Math.Sqrt(sine * sine + cosine * cosine) / count : 0;
            double rms = count > 0 ? Math.Sqrt(power / count) : 0;
            bands.Add(new ToneMeasurement(Frequencies[slot], amplitude, amplitude > 0 ? 20 * Math.Log10(amplitude) : null, rms, rms > 1e-12 ? amplitude / Math.Sqrt(2) / rms : 0));
        }
        return new Analysis(frames > 0 ? Math.Sqrt(sum / frames) : 0, peak, firstActive >= 0, offset * 1000, bands.ToArray());
    }
}

internal sealed record ToneMeasurement(double FrequencyHz, double Amplitude, double? PeakDbfs, double Rms, double TonePurity);
internal sealed record Analysis(double Rms, double Peak, bool SignalDetected, double EstimatedStartOffsetMilliseconds, ToneMeasurement[] Tones);
internal sealed record TestResult(string Mode, EndpointInfo Endpoint, int SampleRate, int CapturedFrames, int SourceFrames, double SourcePeakAmplitude,
    double ReportedRenderLatencyMilliseconds, int Packets, int Discontinuities, int TimestampErrors, ulong? FirstDevicePosition, ulong? FirstQpc100ns,
    string SourcePath, string CapturePath, object? DriverBefore, object? DriverAfter, Analysis Measurements);
