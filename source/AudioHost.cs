using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ViperPc;

public sealed record AudioDevice(int Id, string Name);

/// <summary>Processes a selected recording device and sends it to a playback device.</summary>
public sealed class AudioHost : IDisposable
{
    public const int SampleRate = 48000;
    public const int BlockFrames = 480;
    readonly object gate = new();
    Session? session;
    public event Action<string>? Status;
    public IReadOnlyList<AudioDevice> Inputs => Enumerate(true);
    public IReadOnlyList<AudioDevice> Outputs => Enumerate(false);
    public bool IsRunning => Volatile.Read(ref session)?.Running == true;
    public float InputPeak => Volatile.Read(ref session)?.InputPeak ?? 0;
    public float OutputPeak => Volatile.Read(ref session)?.OutputPeak ?? 0;
    public double LatencyMilliseconds => Volatile.Read(ref session)?.LatencyMilliseconds ?? 0;
    public long ProcessedFrames => Volatile.Read(ref session)?.ProcessedFrames ?? 0;
    public long DroppedBlocks => Volatile.Read(ref session)?.DroppedBlocks ?? 0;

    public void Start(int input, int output, Action<float[]> process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (gate)
        {
            StopCore();
            var next = new Session(input, output, process, Fault);
            session = next;
            try { next.Start(); }
            catch { session = null; next.Dispose(); throw; }
        }
        Report("Обработка звука запущена: 48 кГц, стерео.");
    }

    public void Stop()
    {
        bool stopped;
        lock (gate) { stopped = session != null; StopCore(); }
        if (stopped) Report("Обработка звука остановлена.");
    }

    void StopCore() { var old = session; session = null; old?.Dispose(); }
    void Fault(Session failed, Exception error)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (gate)
            {
                if (!ReferenceEquals(session, failed)) return;
                StopCore();
            }
            Report("Ошибка аудиопотока: " + error.Message);
        });
    }
    void Report(string message) { try { Status?.Invoke(message); } catch { /* A UI observer must not stop a native callback. */ } }
    public void Dispose() => Stop();

    static IReadOnlyList<AudioDevice> Enumerate(bool input)
    {
        var result = new List<AudioDevice> { new(-1, "Устройство по умолчанию") };
        uint count = input ? Native.waveInGetNumDevs() : Native.waveOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            if (input)
            {
                if (Native.waveInGetDevCapsW((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<Native.InputCaps>()) == 0)
                    result.Add(new((int)i, caps.Name));
            }
            else if (Native.waveOutGetDevCapsW((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<Native.OutputCaps>()) == 0)
                result.Add(new((int)i, caps.Name));
        }
        return result;
    }

    sealed class Session : IDisposable
    {
        const int BufferBytes = BlockFrames * 2 * 2;
        readonly int inputId, outputId;
        readonly Action<float[]> process;
        readonly Action<Session, Exception> fault;
        readonly CancellationTokenSource cancel = new();
        readonly BlockingCollection<float[]> captured = new(12);
        readonly BlockingCollection<Buffer> available = new(10);
        readonly ConcurrentQueue<IntPtr> returnedInputs = new();
        readonly AutoResetEvent inputReady = new(false);
        readonly Dictionary<IntPtr, Buffer> inputBuffers = new();
        readonly Dictionary<IntPtr, Buffer> outputBuffers = new();
        readonly Native.WaveCallback inputCallback, outputCallback;
        IntPtr inputHandle, outputHandle;
        Thread? worker, captureWorker;
        volatile bool stopping;
        volatile bool running;
        float inputPeak, outputPeak;
        long processedFrames, droppedBlocks;
        int outstanding, activeCallbacks, queuedBlocks, disposed;
        public bool Running => running && !stopping;
        public float InputPeak => Volatile.Read(ref inputPeak);
        public float OutputPeak => Volatile.Read(ref outputPeak);
        public long ProcessedFrames => Interlocked.Read(ref processedFrames);
        public long DroppedBlocks => Interlocked.Read(ref droppedBlocks);
        // Queue latency is measurable; hardware and Windows' mixer add their own latency.
        public double LatencyMilliseconds => (Math.Max(0, Volatile.Read(ref queuedBlocks)) + Math.Max(0, Volatile.Read(ref outstanding))) * (1000.0 * BlockFrames / SampleRate);

        public Session(int inputId, int outputId, Action<float[]> process, Action<Session, Exception> fault)
        {
            this.inputId = inputId; this.outputId = outputId; this.process = process; this.fault = fault;
            inputCallback = OnInput; outputCallback = OnOutput;
        }

        public void Start()
        {
            var format = new Native.WaveFormat
            {
                FormatTag = 1, Channels = 2, SamplesPerSecond = SampleRate,
                AverageBytesPerSecond = SampleRate * 4, BlockAlign = 4, BitsPerSample = 16, ExtraSize = 0
            };
            Check(Native.waveOutOpen(out outputHandle, unchecked((uint)outputId), ref format, outputCallback, IntPtr.Zero, Native.CallbackFunction), false,
                "Не удалось открыть устройство воспроизведения (48 кГц, стерео)");
            Check(Native.waveInOpen(out inputHandle, unchecked((uint)inputId), ref format, inputCallback, IntPtr.Zero, Native.CallbackFunction), true,
                "Не удалось открыть устройство записи (48 кГц, стерео)");
            for (int i = 0; i < 10; i++)
            {
                var buffer = new Buffer(BufferBytes); outputBuffers.Add(buffer.Header, buffer);
                Check(Native.waveOutPrepareHeader(outputHandle, buffer.Header, Native.HeaderSize), false, "Подготовка выходного буфера");
                buffer.Prepared = true; available.Add(buffer);
            }
            for (int i = 0; i < 6; i++)
            {
                var buffer = new Buffer(BufferBytes); inputBuffers.Add(buffer.Header, buffer);
                Check(Native.waveInPrepareHeader(inputHandle, buffer.Header, Native.HeaderSize), true, "Подготовка входного буфера");
                buffer.Prepared = true;
                Check(Native.waveInAddBuffer(inputHandle, buffer.Header, Native.HeaderSize), true, "Запуск входного буфера");
            }
            running = true;
            worker = new Thread(ProcessLoop) { IsBackground = true, Name = "ViPER audio", Priority = ThreadPriority.AboveNormal };
            captureWorker = new Thread(CaptureLoop) { IsBackground = true, Name = "ViPER capture", Priority = ThreadPriority.AboveNormal };
            worker.Start();
            captureWorker.Start();
            Check(Native.waveInStart(inputHandle), true, "Запуск записи");
        }

        void OnInput(IntPtr device, uint message, IntPtr instance, IntPtr headerPointer, IntPtr parameter)
        {
            if (message != Native.InputData) return;
            Interlocked.Increment(ref activeCallbacks);
            try
            {
                if (stopping) return;
                returnedInputs.Enqueue(headerPointer);
                inputReady.Set();
            }
            catch (Exception error) { if (!stopping) { stopping = true; cancel.Cancel(); fault(this, error); } }
            finally { Interlocked.Decrement(ref activeCallbacks); }
        }

        // WinMM callbacks only signal work; native wave APIs run on a separate thread.
        void CaptureLoop()
        {
            try
            {
                while (!stopping)
                {
                    inputReady.WaitOne();
                    while (!stopping && returnedInputs.TryDequeue(out var headerPointer))
                    {
                        if (!inputBuffers.TryGetValue(headerPointer, out var buffer)) continue;
                        var header = Marshal.PtrToStructure<Native.Header>(headerPointer);
                        int sampleCount = (int)Math.Min((uint)BufferBytes, header.BytesRecorded) / 4 * 2;
                        if (sampleCount > 0)
                        {
                            var samples = new float[sampleCount];
                            unsafe
                            {
                                short* source = (short*)buffer.Data;
                                for (int i = 0; i < sampleCount; i++) samples[i] = source[i] / 32768f;
                            }
                            Interlocked.Increment(ref queuedBlocks);
                            if (!captured.TryAdd(samples))
                            {
                                Interlocked.Decrement(ref queuedBlocks);
                                Interlocked.Increment(ref droppedBlocks);
                            }
                        }
                        if (!stopping)
                        {
                            uint error = Native.waveInAddBuffer(inputHandle, headerPointer, Native.HeaderSize);
                            if (error != 0 && !stopping) throw AudioError(error, true, "Повтор входного буфера");
                        }
                    }
                }
            }
            catch (Exception error) { if (!stopping) { stopping = true; cancel.Cancel(); fault(this, error); } }
        }

        void OnOutput(IntPtr device, uint message, IntPtr instance, IntPtr headerPointer, IntPtr parameter)
        {
            if (message != Native.OutputDone) return;
            Interlocked.Increment(ref activeCallbacks);
            try
            {
                if (!stopping && outputBuffers.TryGetValue(headerPointer, out var buffer) && Interlocked.Exchange(ref buffer.Pending, 0) == 1)
                {
                    Interlocked.Decrement(ref outstanding);
                    available.TryAdd(buffer);
                }
            }
            catch (Exception error) { if (!stopping) { stopping = true; cancel.Cancel(); fault(this, error); } }
            finally { Interlocked.Decrement(ref activeCallbacks); }
        }

        void ProcessLoop()
        {
            try
            {
                foreach (var samples in captured.GetConsumingEnumerable(cancel.Token))
                {
                    Interlocked.Decrement(ref queuedBlocks);
                    Volatile.Write(ref inputPeak, Peak(samples));
                    process(samples);
                    Volatile.Write(ref outputPeak, Peak(samples));
                    var buffer = available.Take(cancel.Token);
                    unsafe
                    {
                        short* target = (short*)buffer.Data;
                        for (int i = 0; i < samples.Length; i++)
                        {
                            float value = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0;
                            target[i] = (short)Math.Clamp((int)MathF.Round(value * 32768f), -32768, 32767);
                        }
                    }
                    var header = Marshal.PtrToStructure<Native.Header>(buffer.Header);
                    header.BufferLength = (uint)(samples.Length * 2);
                    Marshal.StructureToPtr(header, buffer.Header, false);
                    Interlocked.Exchange(ref buffer.Pending, 1);
                    Interlocked.Increment(ref outstanding);
                    uint result = Native.waveOutWrite(outputHandle, buffer.Header, Native.HeaderSize);
                    if (result != 0) throw AudioError(result, false, "Воспроизведение буфера");
                    Interlocked.Add(ref processedFrames, samples.Length / 2);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!stopping) { stopping = true; cancel.Cancel(); fault(this, error); } }
        }

        static float Peak(float[] samples)
        {
            float maximum = 0;
            foreach (float value in samples) if (float.IsFinite(value)) maximum = Math.Max(maximum, Math.Abs(value));
            return maximum;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopping = true; running = false; cancel.Cancel(); inputReady.Set();
            if (inputHandle != IntPtr.Zero) Native.waveInStop(inputHandle);
            if (captureWorker?.IsAlive == true && Thread.CurrentThread != captureWorker) captureWorker.Join();
            if (inputHandle != IntPtr.Zero) Native.waveInReset(inputHandle);
            if (worker?.IsAlive == true && Thread.CurrentThread != worker) worker.Join();
            if (outputHandle != IntPtr.Zero) Native.waveOutReset(outputHandle);
            // Reset returns queued buffers. Close the devices before freeing callback storage.
            if (inputHandle != IntPtr.Zero)
            {
                foreach (var buffer in inputBuffers.Values) if (buffer.Prepared) Native.waveInUnprepareHeader(inputHandle, buffer.Header, Native.HeaderSize);
                Native.waveInClose(inputHandle); inputHandle = IntPtr.Zero;
            }
            if (outputHandle != IntPtr.Zero)
            {
                foreach (var buffer in outputBuffers.Values) if (buffer.Prepared) Native.waveOutUnprepareHeader(outputHandle, buffer.Header, Native.HeaderSize);
                Native.waveOutClose(outputHandle); outputHandle = IntPtr.Zero;
            }
            var spinner = new SpinWait();
            while (Volatile.Read(ref activeCallbacks) > 0) spinner.SpinOnce();
            foreach (var buffer in inputBuffers.Values) buffer.Dispose();
            foreach (var buffer in outputBuffers.Values) buffer.Dispose();
            inputBuffers.Clear(); outputBuffers.Clear();
            captured.Dispose(); available.Dispose(); inputReady.Dispose(); cancel.Dispose();
            GC.KeepAlive(inputCallback); GC.KeepAlive(outputCallback);
        }
    }

    sealed class Buffer : IDisposable
    {
        public IntPtr Data { get; private set; }
        public IntPtr Header { get; private set; }
        public bool Prepared;
        public int Pending;
        public Buffer(int bytes)
        {
            Data = Marshal.AllocHGlobal(bytes);
            Header = Marshal.AllocHGlobal((int)Native.HeaderSize);
            Marshal.StructureToPtr(new Native.Header { Data = Data, BufferLength = (uint)bytes }, Header, false);
        }
        public void Dispose()
        {
            if (Header != IntPtr.Zero) { Marshal.FreeHGlobal(Header); Header = IntPtr.Zero; }
            if (Data != IntPtr.Zero) { Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; }
        }
    }

    static void Check(uint code, bool input, string context) { if (code != 0) throw AudioError(code, input, context); }
    static Exception AudioError(uint code, bool input, string context)
    {
        var text = new System.Text.StringBuilder(256);
        if (input) Native.waveInGetErrorTextW(code, text, 256); else Native.waveOutGetErrorTextW(code, text, 256);
        return new InvalidOperationException($"{context}: {text} (код {code}).");
    }

    static class Native
    {
        public const uint CallbackFunction = 0x30000, InputData = 0x3c0, OutputDone = 0x3bd;
        public static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate void WaveCallback(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2);
        [StructLayout(LayoutKind.Sequential, Pack = 2)] public struct WaveFormat
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSecond, AverageBytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }
        [StructLayout(LayoutKind.Sequential)] public struct Header
        {
            public IntPtr Data;
            public uint BufferLength, BytesRecorded;
            public IntPtr User;
            public uint Flags, Loops;
            public IntPtr Next, Reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct InputCaps
        {
            public ushort Manufacturer, Product;
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct OutputCaps
        {
            public ushort Manufacturer, Product;
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
            public uint Support;
        }
        [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll")] public static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveInGetDevCapsW(UIntPtr id, out InputCaps caps, uint size);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveOutGetDevCapsW(UIntPtr id, out OutputCaps caps, uint size);
        [DllImport("winmm.dll")] public static extern uint waveInOpen(out IntPtr handle, uint id, ref WaveFormat format, WaveCallback callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern uint waveOutOpen(out IntPtr handle, uint id, ref WaveFormat format, WaveCallback callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveInStop(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveInClose(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveOutClose(IntPtr handle);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveInGetErrorTextW(uint error, System.Text.StringBuilder text, uint capacity);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveOutGetErrorTextW(uint error, System.Text.StringBuilder text, uint capacity);
    }
}
