using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace ViperPc;

public static class Verification
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder); var results = new List<object>(); int failed = 0;
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, passed = true }); }
            catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); }
        }
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var catalog = new FeatureCatalog(); var state = new SettingsState(); state.Reset(catalog);
        Check("Exact APK catalog", () => { Require(catalog.Effects.Count == 18, "18 effects required"); Require(catalog.Effects.Sum(x => x.Controls.Count) == 45, "45 controls required"); Require(state.Values.Count == 63, "63 settings required"); Require(catalog.Array("equalizer_preset_values").Length == 12, "12 EQ presets required"); Require(catalog.Array("dynamicsystem_outputs_values").Length == 23, "23 dynamic system presets required"); });
        Check("Android XML roundtrip", () => { var s = state.Clone(); s.Set("36868", true); s.Set("65586", 16); s.Set("65552", "1;2;3;4;5;6;7;8;9;10;"); var path = Path.Combine(folder, "roundtrip.xml"); s.Export(path); var restored = new SettingsState(); restored.Import(path, catalog); Require(restored.Values.All(x => s.Values.TryGetValue(x.Key, out var v) && v.ToString() == x.Value.ToString()), "Preset fields differ"); });
        Check("JSON roundtrip", () => { var path = Path.Combine(folder, "roundtrip.json"); state.Export(path); var restored = new SettingsState(); restored.Import(path, catalog); Require(restored.Values.Count == 63, "Missing fields"); });
        Check("Legacy raw volume import", () => { var path = Path.Combine(folder, "legacy.xml"); File.WriteAllText(path, "<map><int name=\"65586\" value=\"100\"/><int name=\"65588\" value=\"100\"/></map>"); var s = new SettingsState(); s.Import(path, catalog); Require(s.Int("65586") == 11 && s.Int("65588") == 5, "Legacy volume/threshold not normalized"); });
        Check("Preset XXE rejection", () => { var path = Path.Combine(folder, "xxe.xml"); File.WriteAllText(path, "<!DOCTYPE map [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><map><string name='65547'>&x;</string></map>"); bool rejected = false; try { new SettingsState().Import(path, catalog); } catch (System.Xml.XmlException) { rejected = true; } Require(rejected, "External entities accepted"); });
        using var engine = new DspEngine();
        Check("All original defaults reach native DLL", () => { ParameterMapping.Apply(engine, state); Require(engine.Snapshot().Length == 1144, "Wrong APO ABI"); });
        Check("Disabled master is exact bypass", () => { var x = new float[960]; for (int i = 0; i < x.Length; i++) x[i] = MathF.Sin(i * 0.02f) * 0.4f; var expected = (float[])x.Clone(); engine.MasterEnabled = false; engine.Process(x); Require(x.SequenceEqual(expected), "Bypass modifies data"); });
        Check("Actual bass processing changes samples", () =>
        {
            var sample = new float[48000 * 2]; for (int i = 0; i < sample.Length / 2; i++) sample[i * 2] = sample[i * 2 + 1] = MathF.Sin(i * 2 * MathF.PI * 60 / 48000) * 0.025f;
            var s = state.Clone(); s.Set("36868", true); s.Set("65574", true); s.Set("65575", "0"); s.Set("65576", 80); s.Set("65577", 6);
            ParameterMapping.Apply(engine, s); engine.Reset();
            var result = (float[])sample.Clone(); for (int i = 0; i < result.Length; i += 960) { var block = result.Skip(i).Take(Math.Min(960, result.Length - i)).ToArray(); engine.Process(block); Array.Copy(block, 0, result, i, block.Length); }
            Require(result.All(float.IsFinite), "Nonfinite output"); Require(result.Skip(2000).Max(MathF.Abs) > 0.026f, "Bass has no effect");
            WaveFile.Write(Path.Combine(folder, "bass-input.wav"), new WaveData(48000, sample)); WaveFile.Write(Path.Combine(folder, "bass-processed.wav"), new WaveData(48000, result));
        });
        Check("IR and DDC asset loaders", () =>
        {
            var ir = new float[960]; ir[0] = ir[1] = 0.5f; string ip = Path.Combine(folder, "half-gain.irs"); WaveFile.Write(ip, new WaveData(48000, ir)); engine.LoadIr(ip); Require(engine.IrData?.Length == 960, "IR failed");
            string dp = Path.Combine(folder, "half-gain.vdc"); File.WriteAllText(dp, "SR_44100:0.5,0,0,0,0\nSR_48000:0.5,0,0,0,0\n"); engine.LoadDdc(dp); Require(engine.Ddc44?.Length == 5, "DDC failed");
            engine.LoadIr(""); engine.LoadDdc(""); Require(engine.IrData == null && engine.Ddc44 == null, "Asset reset failed");
        });
        Check("44.1 kHz impulse adapts to 48 kHz", () =>
        {
            string ip = Path.Combine(folder, "44100-impulse.irs"); var data = new float[200]; data[0] = 0.5f; data[1] = 0.25f; WaveFile.Write(ip, new WaveData(44100, data)); engine.LoadIr(ip);
            var ir = engine.IrData!; double left = 0, right = 0; for (int i = 0; i < ir.Length; i += 2) { left += ir[i]; right += ir[i + 1]; }
            Require(ir.Length >= 218 && Math.Abs(left - 0.5) < 1e-6 && Math.Abs(right - 0.25) < 1e-6, "Impulse rate conversion altered DC gain or channels"); engine.LoadIr("");
        });
        Check("WAV export compensates DSP latency and retains last frame", () =>
        {
            string input = Path.Combine(folder, "delay-input.wav"), output = Path.Combine(folder, "delay-output.wav"); var data = new float[5367 * 2]; for (int i = 0; i < 5367; i++) { data[i * 2] = MathF.Sin(i * 0.03f) * 0.05f; data[i * 2 + 1] = MathF.Cos(i * 0.05f) * 0.02f; }
            WaveFile.Write(input, new WaveData(48000, data)); using var clean = new DspEngine(); var s = state.Clone(); s.Set("36868", true); ParameterMapping.Apply(clean, s); WaveFile.Process(input, output, clean.Process, (int)clean.LatencyFrames); var actual = WaveFile.Read(output).Samples;
            Require(actual.Length == data.Length && actual.Zip(data).Max(x => Math.Abs(x.First - x.Second)) < 1e-6, "Limiter delay compensation loses or changes samples");
        });
        Check("APO v2 local IPC publication", () => { using var bridge = new ApoBridge(testOnly: true); Require(bridge.Connected, bridge.Error); bridge.Publish(engine); bridge.PublishAssets(engine); Require(!bridge.ReadStatus().Configured, "Status fabricated without APO"); });
        var report = new { generatedUtc = DateTime.UtcNow, target = "Windows x64", nativeVersion = engine.Version, passed = failed == 0, tests = results };
        File.WriteAllText(Path.Combine(folder, "application-tests.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
