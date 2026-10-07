using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ViperPc;

/// <summary>Float stereo loopback of a virtual render endpoint, processed and rendered to a different endpoint.</summary>
public sealed class SystemAudioHost : IDisposable
{
    Thread? worker;
    CancellationTokenSource? cancellation;
    readonly object gate = new();
    public bool IsRunning { get; private set; }
    public bool RawOutputEnabled { get; private set; }
    public long ProcessedFrames { get; private set; }
    public long RenderedFrames { get; private set; }
    public long Discontinuities { get; private set; }
    public long DroppedFrames { get; private set; }
    public long Underruns { get; private set; }
    public double QueueMilliseconds { get; private set; }
    public double DeviceLatencyMilliseconds { get; private set; }
    public float InputPeak { get; private set; }
    public float OutputPeak { get; private set; }
    public string Error { get; private set; } = "";
    public event Action<string>? Status;
    public void Start(string sourceId, string destinationId, Action<float[]> process)
    {
        lock (gate)
        {
            if (worker != null) throw new InvalidOperationException("Поток уже запущен.");
            if (sourceId == destinationId) throw new InvalidOperationException("Виртуальный вход и конечный выход должны быть разными: иначе возникнет звуковая петля.");
            var source = WindowsAudio.Outputs().Find(x => x.Id == sourceId);
            var output = WindowsAudio.Outputs().Find(x => x.Id == destinationId);
            if (source == null || !source.IsVirtual || output == null || output.IsVirtual) throw new InvalidOperationException("Выберите виртуальное устройство на входе и физические наушники или колонки на выходе.");
            if (System.Diagnostics.Process.GetProcessesByName("FxSound").Length > 0 && source.IsFxSound) throw new InvalidOperationException("Закройте FxSound перед запуском ViPER, чтобы звук обрабатывался один раз.");
            if (DriverSetup.Inspect(new OutputEndpoint(source.Id, source.Name, false)).Registered)
                throw new InvalidOperationException("На виртуальном устройстве назначен ViPER APO. Сначала отмените его назначение на странице «Драйвер», чтобы исключить двойную обработку.");
            cancellation = new CancellationTokenSource(); Error = ""; RawOutputEnabled = false;
            ProcessedFrames = RenderedFrames = Discontinuities = DroppedFrames = Underruns = 0;
            InputPeak = OutputPeak = 0;
            using var ready = new ManualResetEventSlim();
            var token = cancellation.Token;
            worker = new Thread(() => Run(sourceId, destinationId, process, token, ready)) { IsBackground = true, Name = "ViPER system audio", Priority = ThreadPriority.AboveNormal };
            worker.Start();
            // The worker always signals after initialization or failure; don't dispose its event while it runs.
            ready.Wait();
            if (!IsRunning) { worker.Join(); worker = null; cancellation.Dispose(); cancellation = null; throw new InvalidOperationException(Error); }
        }
    }
    void Run(string source, string destination, Action<float[]> process, CancellationToken token, ManualResetEventSlim ready)
    {
        int apartment = WindowsAudio.CoInitializeEx(IntPtr.Zero, 0);
        uint taskIndex = 0;
        IntPtr scheduling = AvSetMmThreadCharacteristics("Audio", ref taskIndex);
        var owned = new List<object>();
        IntPtr format = IntPtr.Zero;
        WindowsAudio.IAudioClient? captureAudio = null, renderAudio = null, keepAudio = null;
        bool signalled = false;
        try
        {
            var inputDevice = WindowsAudio.OpenRenderDevice(source); owned.Add(inputDevice);
            var outputDevice = WindowsAudio.OpenRenderDevice(destination); owned.Add(outputDevice);
            captureAudio = WindowsAudio.Activate(inputDevice); owned.Add(captureAudio);
            renderAudio = WindowsAudio.Activate(outputDevice); owned.Add(renderAudio);
            keepAudio = WindowsAudio.Activate(inputDevice); owned.Add(keepAudio);
            RawOutputEnabled = WindowsAudio.RawOutput(renderAudio);
            if (!RawOutputEnabled)
            {
                var diagnosis = DriverSetup.Inspect(new OutputEndpoint(destination, "", false));
                if (diagnosis.Registered && !diagnosis.EffectsDisabled)
                    throw new InvalidOperationException("Этот выход не поддерживает RAW, а на нём назначен ViPER APO. Отключите звуковые улучшения этого физического выхода в Windows или отмените его назначение APO, чтобы избежать повторной обработки.");
            }
            bool postVolume = WindowsAudio.PostVolumeCapture(captureAudio);
            var volume = WindowsAudio.Volume(inputDevice); owned.Add(volume);
            WindowsAudio.Check(volume.GetChannelCount(out uint channels), "Каналы громкости");
            format = WindowsAudio.FloatFormat();
            const uint convert = 0x88000000, noPersist = 0x00080000, rateAdjust = 0x00100000;
            WindowsAudio.Check(captureAudio.Initialize(0, convert | WindowsAudio.Loopback, 600000, 0, format, IntPtr.Zero), "Запуск захвата системного звука");
            WindowsAudio.Check(renderAudio.Initialize(0, convert | noPersist | rateAdjust, 600000, 0, format, IntPtr.Zero), "Запуск обработанного выхода");
            WindowsAudio.Check(keepAudio.Initialize(0, convert | noPersist, 600000, 0, format, IntPtr.Zero), "Запуск виртуального выхода");
            var iid = WindowsAudio.CaptureClientId; WindowsAudio.Check(captureAudio.GetService(ref iid, out object c), "Захват звука"); var capture = (WindowsAudio.IAudioCaptureClient)c; owned.Add(c);
            iid = WindowsAudio.RenderClientId; WindowsAudio.Check(renderAudio.GetService(ref iid, out object r), "Вывод звука"); var render = (WindowsAudio.IAudioRenderClient)r; owned.Add(r);
            WindowsAudio.Check(keepAudio.GetService(ref iid, out object k), "Виртуальный поток"); var keep = (WindowsAudio.IAudioRenderClient)k; owned.Add(k);
            iid = WindowsAudio.ClockAdjustmentId; WindowsAudio.Check(renderAudio.GetService(ref iid, out object a), "Синхронизация аудиоустройств"); var clock = (WindowsAudio.IAudioClockAdjustment)a; owned.Add(a);
            WindowsAudio.Check(renderAudio.GetBufferSize(out uint renderFrames), "Размер выходного буфера");
            int queueTarget = RawOutputEnabled ? 1440 : Math.Min(3840, Math.Max(1440, (int)renderFrames + 480));
            WindowsAudio.Check(keepAudio.GetBufferSize(out uint keepFrames), "Размер виртуального буфера");
            WindowsAudio.Check(renderAudio.GetStreamLatency(out long latency), "Задержка выхода");
            DeviceLatencyMilliseconds = latency / 10000.0;
            Silence(render, renderFrames); Silence(keep, keepFrames);
            WindowsAudio.Check(captureAudio.Start(), "Старт захвата"); WindowsAudio.Check(keepAudio.Start(), "Старт виртуального потока"); WindowsAudio.Check(renderAudio.Start(), "Старт конечного выхода");
            var queue = new StereoQueue(48000, RawOutputEnabled ? 4800 : Math.Max(4800, queueTarget + (int)renderFrames + 480));
            bool primed = false;
            long lastRateUpdate = 0;
            float rate = 48000;
            IsRunning = true; ready.Set(); signalled = true;
            Status?.Invoke("Виртуальный ViPER → выбранный аудиовыход · поток работает");
            while (!token.IsCancellationRequested)
            {
                WindowsAudio.Check(capture.GetNextPacketSize(out uint next), "Доступность системного звука");
                while (next != 0)
                {
                    WindowsAudio.Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "Чтение системного звука");
                    float[] samples = new float[checked((int)frames * 2)];
                    try { if ((flags & 2) == 0) Marshal.Copy(data, samples, 0, samples.Length); if ((flags & 1) != 0) Discontinuities++; }
                    finally { WindowsAudio.Check(capture.ReleaseBuffer(frames), "Освобождение входного буфера"); }
                    WindowsAudio.Check(volume.GetMute(out bool muted), "Отключение звука Windows");
                    if (!postVolume)
                    {
                        WindowsAudio.Check(volume.GetChannelVolumeLevel(0, out float leftDb), "Громкость левого канала");
                        WindowsAudio.Check(volume.GetChannelVolumeLevel(Math.Min(1u, channels - 1), out float rightDb), "Громкость правого канала");
                        float left = muted ? 0 : (float)Math.Pow(10, leftDb / 20.0), right = muted ? 0 : (float)Math.Pow(10, rightDb / 20.0);
                        for (int i = 0; i < samples.Length; i += 2) { samples[i] *= left; samples[i + 1] *= right; }
                    }
                    InputPeak = Peak(samples); process(samples); if (muted) Array.Clear(samples); OutputPeak = Peak(samples);
                    ProcessedFrames += frames; DroppedFrames += queue.Write(samples);
                    WindowsAudio.Check(capture.GetNextPacketSize(out next), "Следующий пакет звука");
                }
                WindowsAudio.Check(keepAudio.GetCurrentPadding(out uint keepPadding), "Состояние виртуального потока");
                if (keepPadding < keepFrames) Silence(keep, keepFrames - keepPadding);
                WindowsAudio.Check(renderAudio.GetCurrentPadding(out uint padding), "Состояние конечного выхода");
                uint available = renderFrames - Math.Min(renderFrames, padding);
                if (!primed && queue.Frames >= queueTarget)
                {
                    // Align the output buffer before consuming the queue. A USB/SRC
                    // output can request a full buffer immediately after activation.
                    if (!RawOutputEnabled)
                    {
                        if (available != 0) Silence(render, available);
                        available = 0;
                    }
                    primed = true;
                }
                if (available != 0)
                {
                    WindowsAudio.Check(render.GetBuffer(available, out var buffer), "Выходной буфер");
                    try
                    {
                        var samples = new float[checked((int)available * 2)];
                        int read = primed ? queue.Read(samples) : 0;
                        if (primed && read < available) { Underruns++; primed = false; }
                        Marshal.Copy(samples, 0, buffer, samples.Length); RenderedFrames += read;
                    }
                    finally { WindowsAudio.Check(render.ReleaseBuffer(available, 0), "Передача обработанного звука"); }
                }
                QueueMilliseconds = queue.Frames / 48.0;
                if (primed && Environment.TickCount64 - lastRateUpdate > 500)
                {
                    // Separate device clocks drift. WASAPI's quality SRC changes output consumption gradually.
                    float wanted = 48000 * (1 + (float)Math.Clamp((queue.Frames - queueTarget) / 48000.0 * .02, -.001, .001));
                    rate += (wanted - rate) * .1f; WindowsAudio.Check(clock.SetSampleRate(rate), "Синхронизация частоты"); lastRateUpdate = Environment.TickCount64;
                }
                token.WaitHandle.WaitOne(3);
            }
        }
        catch (Exception e) { Error = e.Message; Status?.Invoke("Системный поток остановлен: " + Error); }
        finally
        {
            IsRunning = false;
            try { renderAudio?.Stop(); captureAudio?.Stop(); keepAudio?.Stop(); } catch { }
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            for (int i = owned.Count - 1; i >= 0; i--) try { Marshal.ReleaseComObject(owned[i]); } catch { }
            if (apartment >= 0) WindowsAudio.CoUninitialize();
            if (scheduling != IntPtr.Zero) AvRevertMmThreadCharacteristics(scheduling);
            if (!signalled) ready.Set();
        }
    }
    static void Silence(WindowsAudio.IAudioRenderClient render, uint frames)
    {
        WindowsAudio.Check(render.GetBuffer(frames, out _), "Буфер тишины"); WindowsAudio.Check(render.ReleaseBuffer(frames, 2), "Поддержание потока");
    }
    static float Peak(float[] samples)
    {
        float peak = 0;
        for (int i = 0; i < samples.Length; i++) { if (!float.IsFinite(samples[i])) samples[i] = 0; peak = Math.Max(peak, Math.Abs(samples[i])); }
        return peak;
    }
    public void Stop()
    {
        lock (gate) { cancellation?.Cancel(); worker?.Join(); worker = null; cancellation?.Dispose(); cancellation = null; IsRunning = false; }
    }
    public void Dispose() => Stop();
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")] static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

internal sealed class StereoQueue
{
    readonly float[] samples;
    readonly int maximumBacklog;
    long read, write;
    internal int Frames => (int)(write - read);
    internal StereoQueue(int frames, int maximumBacklogFrames = 4800) { samples = new float[checked(frames * 2)]; maximumBacklog = Math.Min(frames, maximumBacklogFrames); }
    internal int Write(float[] input)
    {
        int frames = input.Length / 2;
        int drop = Math.Max(0, Frames + frames - samples.Length / 2);
        read += drop;
        for (int i = 0; i < input.Length; i++) samples[(int)((write * 2 + i) % samples.Length)] = input[i];
        write += frames;
        // Bound delay after scheduling stalls. USB/SRC startup can require a larger reserve.
        int backlog = Math.Max(0, Frames - maximumBacklog);
        read += backlog;
        return drop + backlog;
    }
    internal int Read(float[] output)
    {
        int frames = Math.Min(output.Length / 2, Frames);
        for (int i = 0; i < frames * 2; i++) output[i] = samples[(int)((read * 2 + i) % samples.Length)];
        read += frames; return frames;
    }
}
