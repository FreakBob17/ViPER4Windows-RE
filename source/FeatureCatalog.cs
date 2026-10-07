using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ViperPc;

public sealed class ControlDef
{
    public string Kind { get; init; } = "";
    public string Key { get; init; } = "";
    public string KeyResource { get; init; } = "";
    public string Title { get; init; } = "";
    public JsonElement DefaultValue { get; init; }
    public int Min { get; init; }
    public int Max { get; init; } = 100;
    public string[] Entries { get; init; } = System.Array.Empty<string>();
    public string[] EntryValues { get; init; } = System.Array.Empty<string>();
    public string Dependency { get; init; } = "";
    public bool DisableDependentsState { get; init; }
}

public sealed class EffectDef
{
    public int Order { get; init; }
    public string Title { get; init; } = "";
    public string Key { get; init; } = "";
    public string KeyResource { get; init; } = "";
    public string Icon { get; init; } = "";
    public JsonElement DefaultValue { get; init; }
    public List<ControlDef> Controls { get; init; } = new();
}

/// <summary>Metadata recovered from the user's exact 0.6.2 APK, in its original order.</summary>
public sealed class FeatureCatalog
{
    public JsonDocument Root { get; }
    public List<EffectDef> Effects { get; } = new();
    public Dictionary<string, JsonElement> Arrays { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonElement> Defaults { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<double> EqualizerFrequencies { get; } = new[] { 31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public FeatureCatalog(string? path = null)
    {
        if (path != null) Root = JsonDocument.Parse(File.ReadAllText(path));
        else
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("features.json", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                Root = JsonDocument.Parse(stream);
            }
            else
            {
                var candidate = new[] { Path.Combine(AppContext.BaseDirectory, "features.json"), Path.Combine(AppContext.BaseDirectory, "source", "features.json") }.FirstOrDefault(File.Exists)
                    ?? throw new FileNotFoundException("Не найден каталог функций исходного APK: features.json.");
                Root = JsonDocument.Parse(File.ReadAllText(candidate));
            }
        }
        var root = Root.RootElement;
        foreach (var item in root.GetProperty("arrays").EnumerateObject()) Arrays[item.Name] = item.Value.Clone();
        foreach (var element in root.GetProperty("effect_groups").EnumerateArray())
        {
            var effect = new EffectDef
            {
                Order = Number(element, "order"), Key = Text(element, "key"), Title = Text(element, "title"),
                KeyResource = Text(element, "key_resource"), Icon = Text(element, "icon_resource").Replace("drawable/", ""),
                DefaultValue = Default(element), Controls = element.GetProperty("controls").EnumerateArray().Select(ParseControl).ToList()
            };
            Effects.Add(effect); Defaults[effect.Key] = effect.DefaultValue.Clone();
            foreach (var control in effect.Controls) Defaults[control.Key] = control.DefaultValue.Clone();
        }
    }

    public static FeatureCatalog Load(string? path = null) => new(path);
    public string[] Array(string name) => Arrays.TryGetValue(name, out var value) ? value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToArray() : System.Array.Empty<string>();
    public string String(string name) => Root.RootElement.GetProperty("strings").TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
    static int Number(JsonElement element, string name, int fallback = 0) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;
    static bool Boolean(JsonElement element, string name) => element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && value.GetString() == "true");
    static JsonElement Default(JsonElement element) => element.TryGetProperty("defaultValue", out var value) ? value.Clone() : JsonSerializer.SerializeToElement("");
    static string[] Strings(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToArray() : System.Array.Empty<string>();
    static ControlDef ParseControl(JsonElement element) => new()
    {
        Kind = Text(element, "kind"), Key = Text(element, "key"), KeyResource = Text(element, "key_resource"), Title = Text(element, "title"),
        DefaultValue = Default(element), Min = Number(element, "min"), Max = Number(element, "max", 100), Entries = Strings(element, "entries"),
        EntryValues = Strings(element, "entryValues"), Dependency = Text(element, "dependency"), DisableDependentsState = Boolean(element, "disableDependentsState")
    };
}
