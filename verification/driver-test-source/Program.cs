using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using ViperPc;

namespace DriverTest;

internal static class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    [STAThread]
    static int Main(string[] args)
    {
        string command = args.FirstOrDefault(x => !x.StartsWith("--")) ?? "inspect";
        string Option(string name, string fallback)
        {
            int index = Array.IndexOf(args, "--" + name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        string output = Path.GetFullPath(Option("output", Path.Combine(AppContext.BaseDirectory, "results")));
        Directory.CreateDirectory(output);
        int initialized = CoreAudio.CoInitializeEx(IntPtr.Zero, 2);
        CoreAudio.IMMDevice? device = null;
        try
        {
            CoreAudio.Check(initialized, "Initialize COM apartment");
            string endpoint = Option("endpoint", "default");
            if (Guid.TryParse(endpoint, out var endpointGuid)) endpoint = "{0.0.0.00000000}." + endpointGuid.ToString("B");
            device = CoreAudio.OpenRenderDevice(endpoint);
            var description = CoreAudio.Describe(device);
            using var identity = WindowsIdentity.GetCurrent();
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            var warnings = new List<string>
            {
                "Capture is speaker WASAPI loopback only. No microphone or other recording endpoint is activated.",
                "Loopback contains every application playing on this render endpoint. Pause unrelated audio before interpreting spectral comparisons.",
                "Endpoint and session volume are not modified. Generated source peak is 0.015 (approximately -36.5 dBFS)."
            };
            if (description.IsVirtual) warnings.Add("This is a virtual render endpoint. Its loopback can measure its own audio-engine/APO path but may omit downstream FxSound processing and the final physical speaker path.");
            if (command is "inspect" or "probe")
            {
                var report = new { TimestampUtc = DateTimeOffset.UtcNow, Command = command, Elevated = elevated, Endpoint = description,
                    SharedObjects = SharedMaps.Probe(), DriverStatus = SharedMaps.ReadStatus(), AudioPlayed = false, Warnings = warnings };
                Save(Path.Combine(output, "inspection.json"), report);
                Console.WriteLine(JsonSerializer.Serialize(report, Json)); return 0;
            }
            if (command == "prepare")
            {
                var report = LoopbackTest.Prepare(device); Save(Path.Combine(output, "preparation.json"), report);
                Console.WriteLine(JsonSerializer.Serialize(report, Json)); return 0;
            }
            if (command == "self-test")
            {
                var report = new { Tests = new[] { LoopbackTest.SelfTest(44100), LoopbackTest.SelfTest(48000) }, AudioPlayed = false };
                Save(Path.Combine(output, "self-test.json"), report); Console.WriteLine(JsonSerializer.Serialize(report, Json)); return 0;
            }
            if (command == "analyze")
            {
                string file = Option("file", "");
                var wave = WaveFile.Read(file); var analysis = LoopbackTest.Analyze(wave.Samples, wave.SampleRate);
                Save(Path.Combine(output, "analysis.json"), analysis); Console.WriteLine(JsonSerializer.Serialize(analysis, Json)); return 0;
            }
            if (command == "validate-settings")
            {
                string native = Path.GetFullPath(Option("dll", Path.Combine(AppContext.BaseDirectory, "../../../../../ViperDsp.dll")));
                if (!File.Exists(native)) throw new FileNotFoundException("Supply --dll pointing to the production ViperDsp.dll.", native);
                NativeLibrary.SetDllImportResolver(typeof(DspEngine).Assembly, (name, assembly, search) => name == "ViperDsp.dll" ? NativeLibrary.Load(native) : IntPtr.Zero);
                var checks = new List<object>();
                foreach (uint rate in new uint[] { 44100, 48000 })
                foreach (string mode in new[] { "baseline", "master", "bass", "eq", "baseline-after" })
                {
                    using var settings = MakeSettings(mode, rate);
                    var snapshot = settings.Snapshot(out bool master);
                    ValidateSnapshot(snapshot, mode, master);
                    var samples = new float[checked((int)rate * 2)];
                    for (int frame = 0; frame < samples.Length / 2; frame++) samples[frame * 2] = samples[frame * 2 + 1] = (float)(.015 * Math.Sin(2 * Math.PI * 63 * frame / rate));
                    settings.Process(samples);
                    if (samples.Any(x => !float.IsFinite(x)) || samples.Max(x => Math.Abs(x)) < .001f) throw new InvalidOperationException("Invalid offline output for " + mode);
                    checks.Add(new { SampleRate = rate, Mode = mode, Passed = true, Master = master, SnapshotBytes = snapshot.Length, Peak = samples.Max(x => Math.Abs(x)) });
                }
                var report = new { Checks = checks, AudioPlayed = false, CaptureStarted = false, SharedParametersModified = false };
                Save(Path.Combine(output, "settings-validation.json"), report); Console.WriteLine(JsonSerializer.Serialize(report, Json)); return 0;
            }
            if (command == "system-sequence")
            {
                string native = Path.GetFullPath(Option("dll", "ViperDsp.dll"));
                NativeLibrary.SetDllImportResolver(typeof(DspEngine).Assembly, (name, assembly, search) => name == "ViperDsp.dll" ? NativeLibrary.Load(native) : IntPtr.Zero);
                string sourceId = Option("source", "{0.0.0.00000000}.{df8b4c41-ca90-47f2-9875-739cac727724}");
                var systemModes = new[] { "unrouted", "baseline", "master", "bass", "eq", "baseline-after" };
                var systemResults = new List<object>();
                var analyses = new Dictionary<string, Analysis>();
                foreach (string mode in systemModes)
                {
                    using var engine = MakeSettings(mode == "unrouted" ? "baseline" : mode, 48000);
                    using var host = new SystemAudioHost();
                    if (mode != "unrouted") host.Start(sourceId, description.Id, engine.Process);
                    Thread.Sleep(250);
                    Console.WriteLine("Checking virtual input -> physical output: " + mode);
                    var analysis = BridgeTest.Run(sourceId, description.Id, mode, output);
                    analyses[mode] = analysis;
                    var result = new { Mode = mode, Analysis = analysis, host.RawOutputEnabled, host.ProcessedFrames, host.RenderedFrames, host.Discontinuities, host.DroppedFrames, host.Underruns, host.QueueMilliseconds, host.Error };
                    systemResults.Add(result); Console.WriteLine(JsonSerializer.Serialize(result, Json));
                    host.Stop();
                    if (mode != "unrouted" && (host.ProcessedFrames < 200000 || host.Error.Length > 0 || !analysis.SignalDetected)) throw new InvalidOperationException("Virtual-to-physical stream failed: " + mode);
                }
                var delta = new[] { "master", "bass", "eq", "baseline-after" }.ToDictionary(x => x, x => analyses[x].Tones.Zip(analyses["baseline"].Tones, (wet, dry) => new { wet.FrequencyHz, DifferenceDb = wet.Amplitude > 1e-9 && dry.Amplitude > 1e-9 ? (double?)(20 * Math.Log10(wet.Amplitude / dry.Amplitude)) : null }).ToArray());
                bool noDry = analyses["unrouted"].Peak < .0001;
                bool bass = delta["bass"][0].DifferenceDb > 10;
                bool eq = delta["eq"][0].DifferenceDb > 5;
                var route = new VirtualAudioRoute(Path.Combine(output, "route"));
                var defaults = Enumerable.Range(0, 3).Select(WindowsAudio.DefaultId).ToArray();
                bool routingPassed = false;
                try { route.Start(sourceId); routingPassed = Enumerable.Range(0, 3).All(x => WindowsAudio.DefaultId(x) == sourceId); }
                finally { route.Restore(); }
                bool restorationPassed = Enumerable.Range(0, 3).All(x => WindowsAudio.DefaultId(x) == defaults[x]);
                var report = new { Passed = noDry && bass && eq && routingPassed && restorationPassed, NoUnprocessedBypass = noDry, BassChangedOnPhysicalOutput = bass, EqChangedOnPhysicalOutput = eq, RoutingPassed = routingPassed, RestorationPassed = restorationPassed, Results = systemResults, Comparison = delta, SourceEndpoint = sourceId, PhysicalEndpoint = description, TimestampUtc = DateTimeOffset.UtcNow };
                Save(Path.Combine(output, "system-summary.json"), report); return report.Passed ? 0 : 1;
            }
            if (command == "system-volume")
            {
                string native = Path.GetFullPath(Option("dll", "ViperDsp.dll"));
                NativeLibrary.SetDllImportResolver(typeof(DspEngine).Assembly, (name, assembly, search) => name == "ViperDsp.dll" ? NativeLibrary.Load(native) : IntPtr.Zero);
                string sourceId = Option("source", "{0.0.0.00000000}.{df8b4c41-ca90-47f2-9875-739cac727724}");
                using var sourceLease = new WindowsAudio.DeviceLease(WindowsAudio.OpenRenderDevice(sourceId));
                var volume = WindowsAudio.Volume(sourceLease.Device);
                WindowsAudio.Check(volume.GetMasterVolumeLevel(out float oldDb), "Original volume"); WindowsAudio.Check(volume.GetMute(out bool oldMute), "Original mute");
                using var engine = MakeSettings("baseline", 48000); using var host = new SystemAudioHost();
                var measurements = new Dictionary<string, Analysis>(); bool restored = false;
                try
                {
                    host.Start(sourceId, description.Id, engine.Process);
                    foreach (string mode in new[] { "original", "attenuated", "muted", "restored" })
                    {
                        WindowsAudio.Check(volume.SetMute(mode == "muted", IntPtr.Zero), "Test mute");
                        WindowsAudio.Check(volume.SetMasterVolumeLevel(mode == "attenuated" ? Math.Max(-90, oldDb - 12) : oldDb, IntPtr.Zero), "Test volume");
                        Thread.Sleep(350); measurements[mode] = BridgeTest.Run(sourceId, description.Id, mode, output); Console.WriteLine(mode + ": " + measurements[mode].Peak);
                    }
                }
                finally { host.Stop(); volume.SetMasterVolumeLevel(oldDb, IntPtr.Zero); volume.SetMute(oldMute, IntPtr.Zero); volume.GetMasterVolumeLevel(out float db); volume.GetMute(out bool mute); restored = Math.Abs(db - oldDb) < .05 && mute == oldMute; Marshal.ReleaseComObject(volume); }
                double delta = 20 * Math.Log10(measurements["attenuated"].Tones[1].Amplitude / measurements["original"].Tones[1].Amplitude);
                var report = new { Passed = restored && measurements["muted"].Peak < .00001 && Math.Abs(delta + 12) < .4, Restored = restored, OriginalVolumeDb = oldDb, OriginalMute = oldMute, AttenuationDb = delta, MutedPeak = measurements["muted"].Peak, Measurements = measurements, host.DroppedFrames, host.Underruns, host.Error };
                Save(Path.Combine(output, "volume-summary.json"), report); return report.Passed ? 0 : 1;
            }
            if (command == "system-soak")
            {
                string native = Path.GetFullPath(Option("dll", "ViperDsp.dll"));
                NativeLibrary.SetDllImportResolver(typeof(DspEngine).Assembly, (name, assembly, search) => name == "ViperDsp.dll" ? NativeLibrary.Load(native) : IntPtr.Zero);
                string sourceId = Option("source", "{0.0.0.00000000}.{df8b4c41-ca90-47f2-9875-739cac727724}");
                using var engine = MakeSettings("master", 48000); using var host = new SystemAudioHost();
                host.Start(sourceId, description.Id, engine.Process);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var samples = new List<object>();
                while (timer.Elapsed.TotalSeconds < 120)
                {
                    Thread.Sleep(1000);
                    if (!host.IsRunning) throw new InvalidOperationException(host.Error);
                    if ((int)timer.Elapsed.TotalSeconds % 20 == 0) { samples.Add(new { Seconds = timer.Elapsed.TotalSeconds, host.ProcessedFrames, host.QueueMilliseconds, host.DroppedFrames, host.Underruns }); Console.WriteLine($"{timer.Elapsed.TotalSeconds:0}s: {host.ProcessedFrames} frames, queue {host.QueueMilliseconds:0.0}ms, drops {host.DroppedFrames}, underruns {host.Underruns}"); }
                }
                host.Stop();
                bool passed = host.ProcessedFrames > 5700000 && host.DroppedFrames == 0 && host.Underruns == 0 && host.Error.Length == 0;
                Save(Path.Combine(output, "soak-summary.json"), new { Passed = passed, DurationSeconds = timer.Elapsed.TotalSeconds, host.ProcessedFrames, host.RenderedFrames, host.DroppedFrames, host.Underruns, host.Discontinuities, host.Error, Samples = samples }); return passed ? 0 : 1;
            }
            if (command is not "capture" and not "sequence") throw new ArgumentException("Commands: inspect, probe, prepare, self-test, validate-settings, capture, sequence, analyze.");
            string[] modes = command == "sequence" ? new[] { "baseline", "master", "bass", "eq", "baseline-after" } : new[] { Option("mode", "untouched") };
            if (modes.Any(x => x is not "untouched" and not "baseline" and not "master" and not "bass" and not "eq" and not "baseline-after")) throw new ArgumentException("Modes: untouched, baseline, master, bass, eq, baseline-after.");
            bool configure = modes.Any(x => x != "untouched");
            string nativePath = Path.GetFullPath(Option("dll", Path.Combine(AppContext.BaseDirectory, "../../../../../ViperDsp.dll")));
            if (configure)
            {
                if (!File.Exists(nativePath)) throw new FileNotFoundException("Supply --dll pointing to the production ViperDsp.dll.", nativePath);
                NativeLibrary.SetDllImportResolver(typeof(DspEngine).Assembly, (name, assembly, search) => name == "ViperDsp.dll" ? NativeLibrary.Load(nativePath) : IntPtr.Zero);
            }
            var inspectionBefore = SharedMaps.Probe();
            using var saved = configure ? new SharedMaps.SavedState() : null;
            using var bridge = configure ? new ApoBridge() : null;
            if (configure && bridge?.Connected != true)
            {
                var connection = new { Connected = false, BridgeError = bridge?.Error, SharedObjects = SharedMaps.Probe() };
                Save(Path.Combine(output, "connection-failure.json"), connection);
                throw new InvalidOperationException("APO connection failed: " + bridge?.Error + ". Inspect connection-failure.json for exact Win32 error codes (5 means access denied). No audio was played.");
            }
            var results = new List<TestResult>();
            object restoration;
            try
            {
                foreach (string mode in modes)
                {
                    if (configure)
                    {
                        using var settings = MakeSettings(mode, ReadRate(description));
                        bridge!.Publish(settings); Thread.Sleep(350);
                    }
                    Console.WriteLine("Running speaker-only loopback test: " + mode);
                    TestResult result = LoopbackTest.Run(device, mode, output, bridge == null ? SharedMaps.ReadStatus : () => DriverState(bridge));
                    results.Add(result); Save(Path.Combine(output, mode + ".json"), result);
                    Console.WriteLine($"Captured {result.CapturedFrames} frames; peak {result.Measurements.Peak:F6}; detected={result.Measurements.SignalDetected}.");
                }
            }
            finally
            {
                if (configure)
                {
                    if (saved!.Available) saved.Restore();
                    if (!saved.Restored) bridge!.Disable();
                    restoration = new { PreviousStateSaved = saved.Available, PreviousStateRestored = saved.Restored, FinalMasterDisabled = !saved.Restored, Error = saved.Error };
                }
                else restoration = new { PreviousStateSaved = false, PreviousStateRestored = false, FinalMasterDisabled = false, Error = "Parameters were not modified." };
                Save(Path.Combine(output, "restoration.json"), restoration);
            }
            var baseline = results.FirstOrDefault(x => x.Mode == "baseline");
            var comparison = results.Where(x => baseline != null && x.Mode != "baseline").Select(result => new
            {
                result.Mode, result.Measurements.SignalDetected,
                Tones = result.Measurements.Tones.Zip(baseline!.Measurements.Tones, (changed, original) => new
                {
                    changed.FrequencyHz, BaselineAmplitude = original.Amplitude, ChangedAmplitude = changed.Amplitude,
                    DifferenceDb = changed.Amplitude > 1e-9 && original.Amplitude > 1e-9 ? (double?)(20 * Math.Log10(changed.Amplitude / original.Amplitude)) : null,
                    changed.TonePurity
                }).ToArray()
            }).ToArray();
            var summary = new { TimestampUtc = DateTimeOffset.UtcNow, Command = command, Elevated = elevated, Endpoint = description, AudioPlayed = true,
                SourceDurationSeconds = LoopbackTest.SignalSeconds, SourcePeakAmplitude = LoopbackTest.PeakAmplitude,
                SharedObjectsBefore = inspectionBefore, Results = results, Comparison = comparison, Restoration = restoration, Warnings = warnings };
            Save(Path.Combine(output, "summary.json"), summary);
            Console.WriteLine("Saved " + Path.Combine(output, "summary.json")); return 0;
        }
        catch (Exception error)
        {
            var report = new { TimestampUtc = DateTimeOffset.UtcNow, Command = command, ErrorType = error.GetType().FullName, error.Message,
                HResult = $"0x{error.HResult:X8}", Win32Error = error is Win32Exception win32 ? (int?)win32.NativeErrorCode : null, SharedObjects = SharedMaps.Probe() };
            Save(Path.Combine(output, "failure.json"), report); Console.Error.WriteLine(JsonSerializer.Serialize(report, Json)); return 1;
        }
        finally { if (device != null) Marshal.ReleaseComObject(device); if (initialized >= 0) CoreAudio.CoUninitialize(); }
    }

    static uint ReadRate(EndpointInfo endpoint)
    {
        var value = JsonSerializer.SerializeToElement(endpoint.MixFormat).GetProperty("SampleRate").GetUInt32();
        if (value is not 44100 and not 48000) throw new InvalidOperationException("The production APO test is restricted to 44.1/48 kHz; current mix rate is " + value);
        return value;
    }
    internal static DspEngine MakeSettings(string mode, uint sampleRate)
    {
        var engine = new DspEngine(sampleRate);
        try
        {
            foreach (int enable in new[] { 65538, 65544, 65546, 65548, 65551, 65553, 65557, 65559, 65565, 65569, 65574, 65578, 65581, 65583, 65584, 65610, 65603 }) engine.Set(enable, 0);
            engine.Set(65586, 100); engine.Set(65587, 0); engine.Set(65588, 100);
            for (int band = 0; band < 10; band++) engine.Set(65552, band, 0);
            engine.MasterEnabled = mode is not "baseline" and not "baseline-after";
            if (mode == "bass") { engine.Set(65575, 0); engine.Set(65576, 120); engine.Set(65577, 600); engine.Set(65574, 1); }
            if (mode == "eq") { for (int band = 0; band < 3; band++) engine.Set(65552, band, 900); engine.Set(65551, 1); }
            return engine;
        }
        catch { engine.Dispose(); throw; }
    }
    static void ValidateSnapshot(byte[] data, string mode, bool master)
    {
        if (data.Length != 1144 || master != (mode is not "baseline" and not "baseline-after")) throw new InvalidOperationException("Invalid master/snapshot ABI.");
        // Offsets verified by offsetof() against the pinned production ViPERParams.h.
        int[] enableOffsets = { 12, 28, 44, 112, 132, 152, 172, 184, 316, 324, 328, 344, 364, 388, 396, 420, 452, 464, 472, 476, 484, 488, 856 };
        foreach (int offset in enableOffsets)
        {
            byte expected = (mode == "bass" && offset == 112 || mode == "eq" && offset == 184) ? (byte)1 : (byte)0;
            if (data[offset] != expected) throw new InvalidOperationException($"Unexpected effect enable at snapshot byte {offset} for {mode}.");
        }
        if (BitConverter.ToSingle(data, 0) != 1 || BitConverter.ToSingle(data, 4) != 1 || BitConverter.ToSingle(data, 8) != 0) throw new InvalidOperationException("Invalid neutral output/limiter/pan.");
        if (BitConverter.ToUInt32(data, 188) != 10) throw new InvalidOperationException("Expected original ten-band EQ.");
        for (int band = 0; band < 10; band++) if (BitConverter.ToSingle(data, 192 + band * 4) != (mode == "eq" && band < 3 ? 9 : 0)) throw new InvalidOperationException("Unexpected EQ band level.");
        if (mode == "bass" && (BitConverter.ToInt32(data, 116) != 0 || BitConverter.ToUInt32(data, 120) != 120 || BitConverter.ToSingle(data, 124) != 6)) throw new InvalidOperationException("Unexpected bass snapshot settings.");
    }
    static object DriverState(ApoBridge bridge)
    {
        var state = bridge.ReadStatus(); return new { bridge.Connected, state.Configured, state.SampleRate, state.Frames, state.Version };
    }
    static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}
