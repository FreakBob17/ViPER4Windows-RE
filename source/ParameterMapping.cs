using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ViperPc;

/// <summary>Exact APK preference-to-driver conversions, verified in static Dalvik bytecode.</summary>
public static class ParameterMapping
{
    static readonly int[] OutputVolumes = { 1, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200 };
    static readonly string[] OutputDb = { "-40.0", "-26.0", "-20.0", "-14.0", "-10.5", "-8.0", "-6.0", "-4.4", "-3.0", "-1.9", "-1.0", "0.0", "0.8", "1.6", "2.3", "2.9", "3.5", "4.1", "4.6", "5.1", "5.6", "6.0" };
    static readonly int[] Thresholds = { 30, 50, 70, 80, 90, 100 };
    static readonly string[] ThresholdDb = { "-10.5", "-6.0", "-3.0", "-1.9", "-1.0", "0.0" };
    static readonly int[] PlaybackRatios = { 50, 100, 300 };
    static readonly int[] PlaybackMaximums = { 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 3000 };
    static readonly string[] BassDb = { "3.5", "6.0", "8.0", "10.0", "11.0", "12.0", "13.0", "14.0", "14.8", "15.6", "16.3", "17.0" };
    static readonly string[] ClarityDb = { "0.0", "3.5", "6.0", "8.0", "10.0", "11.0", "12.0", "13.0", "14.0", "14.8" };
    static readonly string[] RoomAreas = { "25", "36", "49", "64", "81", "100", "121", "203", "347", "652", "1200" };
    static readonly string[] RoomWidths = { "5", "6", "7", "8", "9", "10", "11", "14", "19", "26", "36" };
    static readonly string[] EffectSwitches = { "65565", "65610", "65546", "65548", "65551", "65538", "65553", "65557", "65544", "65559", "65569", "65583", "65574", "65578", "65581", "65584", "65603" };
    static readonly string[] FetBooleans = { "65614", "65616", "65618", "65620", "65626" };
    static readonly string[] FetIntegers = { "65611", "65612", "65613", "65621", "65615", "65617", "65622", "65619", "65623", "65624", "65625" };
    static int Index(int value, int length) => Math.Clamp(value, 0, length - 1);
    static int IndexedValue(int[] values, int value) => value >= values.Length && System.Array.IndexOf(values, value) >= 0 ? value : values[Index(value, values.Length)];
    // Earlier Android presets sometimes store native values instead of slider indices.
    public static int NormalizeImportedIndex(string key, int value)
    {
        int[]? array = key switch
        {
            "65586" => OutputVolumes, "65588" or "65567" => Thresholds,
            "65566" => PlaybackRatios, "65568" => PlaybackMaximums, _ => null
        };
        if (array == null || value < array.Length) return value;
        int index = System.Array.IndexOf(array, value);
        return index >= 0 ? index : value;
    }
    static int Int(SettingsState state, string key)
    {
        if (!state.Values.TryGetValue(key, out var value)) return 0;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public static void Apply(DspEngine engine, SettingsState state)
    {
        engine.MasterEnabled = state.Bool("36868");
        foreach (var key in EffectSwitches) engine.Set(int.Parse(key, CultureInfo.InvariantCulture), state.Bool(key) ? 1 : 0);
        engine.Set(65586, IndexedValue(OutputVolumes, Int(state, "65586")));
        engine.Set(65587, Math.Clamp(Int(state, "65587"), 0, 100) * 2 - 100);
        engine.Set(65588, IndexedValue(Thresholds, Int(state, "65588")));
        engine.Set(65566, IndexedValue(PlaybackRatios, Int(state, "65566")));
        engine.Set(65568, IndexedValue(PlaybackMaximums, Int(state, "65568")));
        engine.Set(65567, IndexedValue(Thresholds, Int(state, "65567")));
        foreach (var key in FetBooleans) engine.Set(int.Parse(key, CultureInfo.InvariantCulture), state.Bool(key) ? 1 : 0);
        foreach (var key in FetIntegers) engine.Set(int.Parse(key, CultureInfo.InvariantCulture), Math.Clamp(Int(state, key), 0, key == "65624" ? 300 : 100));
        engine.LoadDdc(ResolveAsset(state.Text("65547"), "ddc"));
        engine.Set(65549, 7600);
        engine.Set(65550, (int)(Math.Clamp(Int(state, "65549;65550"), 0, 100) * 5.6));
        var bands = EqualizerBands(state);
        for (int band = 0; band < 10; band++) engine.Set(65552, band, (int)Math.Floor(bands[band] * 100f + 0.5f));
        engine.LoadIr(ResolveAsset(state.Text("65540;65541;65542"), "kernels"));
        engine.Set(65543, Math.Clamp(Int(state, "65543"), 0, 100));
        // The 0.6.2 APK only writes the depth command for this combined key.
        engine.Set(65556, Math.Clamp(Int(state, "65554;65556"), 0, 8) * 75 + 200);
        engine.Set(65555, Math.Clamp(Int(state, "65555"), 0, 10) * 10 + 100);
        engine.Set(65558, (Math.Clamp(Int(state, "65558"), 0, 19) + 1) * 100);
        engine.Set(65545, Math.Clamp(Int(state, "65545"), 0, 4));
        engine.Set(65560, Math.Clamp(Int(state, "65560"), 0, 10) * 10);
        engine.Set(65561, Math.Clamp(Int(state, "65561"), 0, 10) * 10);
        foreach (var key in new[] { "65562", "65563", "65564" }) engine.Set(int.Parse(key, CultureInfo.InvariantCulture), Math.Clamp(Int(state, key), 0, 100));
        var dynamic = ParseIntegers(state.Text("65570;65571;65572"), new[] { 100, 5600, 40, 80, 50, 50 });
        engine.Set(65570, dynamic[0], dynamic[1]);
        engine.Set(65571, dynamic[2], dynamic[3]);
        engine.Set(65572, dynamic[4], dynamic[5]);
        engine.Set(65573, Math.Clamp(Int(state, "65573"), 0, 100) * 20 + 100);
        engine.Set(65575, Math.Clamp(Int(state, "65575"), 0, 2));
        engine.Set(65576, Math.Clamp(Int(state, "65576"), 0, 135) + 15);
        engine.Set(65577, Math.Clamp(Int(state, "65577"), 0, 11) * 50 + 50);
        engine.Set(65579, Math.Clamp(Int(state, "65579"), 0, 2));
        engine.Set(65580, Math.Clamp(Int(state, "65580"), 0, 9) * 50);
        engine.Set(65582, Math.Clamp(Int(state, "65582"), 0, 2));
        engine.Set(65585, Math.Clamp(Int(state, "65585"), 0, 2));
    }

    public static float[] EqualizerBands(SettingsState state)
    {
        var parts = state.Text("65552").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var result = new float[10];
        for (int i = 0; i < result.Length && i < parts.Length; i++)
            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)) result[i] = Math.Clamp(value, -12, 12);
        return result;
    }

    static int[] ParseIntegers(string text, int[] fallback)
    {
        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != fallback.Length) return fallback;
        var result = new int[parts.Length];
        for (int i = 0; i < result.Length; i++) if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]) || result[i] < 0 || result[i] > 24000) return fallback;
        return result;
    }

    public static string ResolveAsset(string value, string folder)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (File.Exists(value)) return Path.GetFullPath(value);
        string name = Path.GetFileName(value.Replace('/', Path.DirectorySeparatorChar));
        foreach (var root in new[] { folder, Path.Combine("data", folder), Path.Combine("assets", folder), folder == "ddc" ? Path.Combine("data", "vdc") : Path.Combine("data", "irs") })
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, root, name);
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }

    static string Format(double value, string spec) => value.ToString(spec, CultureInfo.InvariantCulture);
    static double Geometric(int value, double minimum, double maximum) => Math.Exp(Math.Log(minimum) + Math.Clamp(value, 0, 100) / 100.0 * (Math.Log(maximum) - Math.Log(minimum))) * 1000;
    public static string Display(ControlDef control, SettingsState state)
    {
        if (control.Kind.Contains("ListPreference"))
        {
            string text = state.Text(control.Key);
            int index = System.Array.IndexOf(control.EntryValues, text);
            return index >= 0 && index < control.Entries.Length ? control.Entries[index] : string.IsNullOrEmpty(text) ? "Не выбран" : Path.GetFileName(text);
        }
        if (control.Kind.Contains("CheckBox")) return state.Bool(control.Key) ? "Включено" : "Выключено";
        int value = Int(state, control.Key);
        return control.Key switch
        {
            "65586" => OutputDb[Index(value, OutputDb.Length)] + " dB",
            "65587" => $"{100 - value}:{value}",
            "65588" or "65567" => ThresholdDb[Index(value, ThresholdDb.Length)] + " dB",
            "65566" => (value + 1).ToString(CultureInfo.InvariantCulture),
            "65568" => (value >= 10 ? "∞" : (value + 1).ToString(CultureInfo.InvariantCulture)) + "x",
            "65611" => Format(value < 1 ? 0 : value * -0.6, "F1") + " dB",
            "65612" => (value >= 100 ? "∞" : Format(100.0 / (100 - value), "F2")) + ":1",
            "65613" or "65615" => Format(value * 0.6, "F1") + " dB",
            "65621" => Format(value * 0.04, "F2") + "x",
            "65617" or "65622" => Format(Geometric(value, 0.0001, 0.2), "F2") + " ms",
            "65619" or "65623" => ((int)Geometric(value, 0.005, 2)).ToString(CultureInfo.InvariantCulture) + " ms",
            "65624" => Format(value * 0.2, "F2") + " dB",
            "65625" => ((int)Geometric(value, 1, 4)).ToString(CultureInfo.InvariantCulture),
            "65549;65550" or "65543" or "65562" or "65563" or "65564" or "65573" => value + "%",
            "65554;65556" or "65555" or "65545" or "65582" or "65585" => (value + 1).ToString(CultureInfo.InvariantCulture),
            "65558" => (value + 1) + " ms",
            "65560" => RoomAreas[Index(value, RoomAreas.Length)] + " m²",
            "65561" => RoomWidths[Index(value, RoomWidths.Length)] + " m",
            "65576" => (value + 15) + " Hz",
            "65577" => BassDb[Index(value, BassDb.Length)] + " dB",
            "65580" => ClarityDb[Index(value, ClarityDb.Length)] + " dB",
            _ => value.ToString(CultureInfo.InvariantCulture)
        };
    }
}
