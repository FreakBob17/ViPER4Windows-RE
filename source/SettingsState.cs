using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ViperPc;

public sealed class SettingsState
{
    public Dictionary<string, JsonElement> Values { get; set; } = new();
    public int Int(string key) => Values.TryGetValue(key, out var v) ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : int.TryParse(v.ToString(), out var n) ? n : 0 : 0;
    public bool Bool(string key) => Values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;
    public string Text(string key) => Values.TryGetValue(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
    public void Set(string key, object value) => Values[key] = JsonSerializer.SerializeToElement(value);
    public void Reset(FeatureCatalog catalog)
    {
        Values.Clear();
        foreach (var fx in catalog.Effects)
        {
            Values[fx.Key] = fx.DefaultValue.Clone();
            foreach (var p in fx.Controls) Values[p.Key] = p.DefaultValue.Clone();
        }
    }
    public SettingsState Clone() => new() { Values = Values.ToDictionary(x => x.Key, x => x.Value.Clone()) };
    public void Export(string path)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        var map = new XElement("map");
        foreach (var pair in Values)
        {
            var kind = pair.Value.ValueKind;
            if (kind is JsonValueKind.True or JsonValueKind.False)
                map.Add(new XElement("boolean", new XAttribute("name", pair.Key), new XAttribute("value", pair.Value.GetBoolean() ? "true" : "false")));
            else if (kind == JsonValueKind.Number)
                map.Add(new XElement("int", new XAttribute("name", pair.Key), new XAttribute("value", pair.Value.GetInt32())));
            else
            {
                string text = pair.Value.GetString() ?? "";
                if (pair.Key is "65547" or "65540;65541;65542") text = Path.GetFileName(text.Replace('/', Path.DirectorySeparatorChar));
                map.Add(new XElement("string", new XAttribute("name", pair.Key), text));
            }
        }
        new XDocument(new XDeclaration("1.0", "utf-8", "yes"), map).Save(path);
    }
    public void Import(string path, FeatureCatalog catalog)
    {
        Dictionary<string, JsonElement> incoming;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            incoming = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Пустой пресет.");
        else
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 2_000_000 });
            var xml = XDocument.Load(reader);
            if (xml.Root?.Name != "map") throw new InvalidDataException("Нужен XML-пресет ViPER4Android: корневой элемент <map>.");
            incoming = new();
            foreach (var e in xml.Root.Elements())
            {
                string? key = (string?)e.Attribute("name");
                if (key == null) continue;
                object value = e.Name.LocalName switch
                {
                    "boolean" => bool.Parse((string?)e.Attribute("value") ?? "false"),
                    "int" or "long" => int.Parse((string?)e.Attribute("value") ?? "0", CultureInfo.InvariantCulture),
                    "string" => e.Value,
                    _ => e.Value
                };
                incoming[key] = JsonSerializer.SerializeToElement(value);
            }
        }
        var next = new SettingsState(); next.Reset(catalog);
        foreach (var fx in catalog.Effects)
        {
            if (incoming.TryGetValue(fx.Key, out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False) next.Values[fx.Key] = enabled.Clone();
            foreach (var c in fx.Controls)
            {
                if (!incoming.TryGetValue(c.Key, out var v)) continue;
                if (c.Kind.Contains("SeekBar") && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) next.Set(c.Key, Math.Clamp(ParameterMapping.NormalizeImportedIndex(c.Key, n), c.Min, c.Max));
                else if (c.Kind.Contains("CheckBox") && v.ValueKind is JsonValueKind.True or JsonValueKind.False) next.Values[c.Key] = v.Clone();
                else if (v.ValueKind == JsonValueKind.String) next.Values[c.Key] = v.Clone();
            }
        }
        Values = next.Values;
    }
}

public sealed class AppPreferences
{
    public string Route { get; set; } = "Наушники";
    public bool LightTheme { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool AutoSwitchProfiles { get; set; } = true;
    public Dictionary<string, string> DeviceRoutes { get; set; } = new();
    public int InputDevice { get; set; } = -1;
    public int OutputDevice { get; set; } = -1;
    public string VirtualEndpointId { get; set; } = "";
    public string PhysicalOutputId { get; set; } = "";
    public bool StartSystemAudio { get; set; }
    public string DataDirectory { get; set; } = "";
    public Dictionary<string, SettingsState> Routes { get; set; } = new();
    public static string DataPath => Path.Combine(AppContext.BaseDirectory, "data");
    public static string FilePath => Path.Combine(DataPath, "settings.json");
    public static AppPreferences Load(FeatureCatalog catalog)
    {
        AppPreferences p;
        try { p = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch { p = new(); }
        foreach (var name in new[] { "Наушники", "Динамики", "Bluetooth" })
        {
            if (!p.Routes.TryGetValue(name, out var state)) { state = new(); state.Reset(catalog); p.Routes[name] = state; }
            foreach (var fx in catalog.Effects)
            {
                state.Values.TryAdd(fx.Key, fx.DefaultValue.Clone());
                foreach (var c in fx.Controls) state.Values.TryAdd(c.Key, c.DefaultValue.Clone());
            }
        }
        if (!p.Routes.ContainsKey(p.Route)) p.Route = "Наушники";
        return p;
    }
    public void Save()
    {
        Directory.CreateDirectory(DataPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
