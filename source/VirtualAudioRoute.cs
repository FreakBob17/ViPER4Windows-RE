using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ViperPc;

public sealed class VirtualAudioRoute
{
    readonly string folder;
    string RouteFile => Path.Combine(folder, "audio-route-backup.json");
    string NameFile => Path.Combine(folder, "virtual-device-backup.json");
    public bool Active => File.Exists(RouteFile);
    public VirtualAudioRoute(string folder) { this.folder = folder; }
    static void Save(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(path + ".tmp", path, true);
    }
    public void Prepare(string id)
    {
        var endpoint = WindowsAudio.Outputs().SingleOrDefault(x => x.Id == id);
        if (endpoint?.IsFxSound != true) throw new InvalidOperationException("Для устройства ViPER нужен уже установленный подписанный виртуальный драйвер FxSound.");
        if (System.Diagnostics.Process.GetProcessesByName("FxSound").Length > 0) throw new InvalidOperationException("Закройте FxSound, затем подготовьте устройство ViPER.");
        if (!File.Exists(NameFile)) Save(NameFile, new NameBackup(id, endpoint.Name));
        else if (JsonSerializer.Deserialize<NameBackup>(File.ReadAllText(NameFile))?.Endpoint != id) throw new InvalidOperationException("Сначала восстановите имя ранее выбранного виртуального устройства.");
        WindowsAudio.Rename(id, "ViPER");
        // Processing belongs to the app on this route. Keep a reversible backup before removing an old APO assignment from its input.
        if (DriverSetup.Inspect(new OutputEndpoint(id, endpoint.Name, false)).Registered)
            DriverSetup.Unregister(id, Path.Combine(folder, "driver-backups"));
    }
    public void Start(string id, string? fallback = null)
    {
        if (Active) Restore();
        var defaults = Enumerable.Range(0, 3).Select(WindowsAudio.DefaultId).ToArray();
        if (fallback != null)
        {
            if (!WindowsAudio.Outputs().Any(x => x.Id == fallback && !x.IsVirtual)) throw new InvalidOperationException("Конечный физический выход недоступен.");
            for (int i = 0; i < defaults.Length; i++) if (defaults[i] == id) defaults[i] = fallback;
        }
        if (defaults.All(x => x == id)) throw new InvalidOperationException("Сначала выберите физический выход Windows, чтобы его можно было восстановить при остановке ViPER.");
        Save(RouteFile, new RouteBackup(id, defaults, Guid.NewGuid().ToString("N")));
        try { for (int role = 0; role < 3; role++) WindowsAudio.SetDefault(id, role); }
        catch { Restore(); throw; }
    }
    public void Restore(string? expectedToken = null)
    {
        if (!Active) return;
        var backup = JsonSerializer.Deserialize<RouteBackup>(File.ReadAllText(RouteFile)) ?? throw new InvalidDataException("Не удалось прочитать исходный звуковой маршрут.");
        if (expectedToken != null && backup.Token != expectedToken) return;
        if (backup.Defaults.Length != 3) throw new InvalidDataException("Неверная резервная копия маршрута.");
        List<Exception> errors = new();
        for (int role = 0; role < 3; role++)
        {
            try { if (WindowsAudio.DefaultId(role) == backup.VirtualEndpoint && backup.Defaults[role] != backup.VirtualEndpoint) WindowsAudio.SetDefault(backup.Defaults[role], role); }
            catch (Exception e) { errors.Add(e); }
        }
        if (errors.Count > 0) throw new AggregateException("Не все исходные выходы доступны. Выберите выход Windows вручную; резервная копия сохранена.", errors);
        File.Delete(RouteFile);
    }
    public void Watch(string executable)
    {
        var backup = JsonSerializer.Deserialize<RouteBackup>(File.ReadAllText(RouteFile)) ?? throw new InvalidDataException("Нет резервной копии маршрута.");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
        foreach (string value in new[] { "--route-watch", Environment.ProcessId.ToString(), folder, backup.Token }) start.ArgumentList.Add(value);
        using var watcher = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить восстановление звука при сбое.");
    }
    public void RestoreName()
    {
        Restore();
        if (!File.Exists(NameFile)) return;
        var backup = JsonSerializer.Deserialize<NameBackup>(File.ReadAllText(NameFile)) ?? throw new InvalidDataException("Неверная копия имени устройства.");
        var endpoint = WindowsAudio.Outputs().Find(x => x.Id == backup.Endpoint);
        string suffix = endpoint == null ? "" : " (" + endpoint.Adapter + ")";
        string shortName = suffix.Length > 0 && backup.Name.EndsWith(suffix, StringComparison.Ordinal) ? backup.Name[..^suffix.Length] : backup.Name;
        WindowsAudio.Rename(backup.Endpoint, shortName); File.Delete(NameFile);
    }
    sealed record NameBackup(string Endpoint, string Name);
    sealed record RouteBackup(string VirtualEndpoint, string[] Defaults, string Token = "");
}
