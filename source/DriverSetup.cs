using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ViperPc;

public sealed record DriverDiagnosis
{
    public string Summary { get; init; } = "";
    public bool Installed { get; init; }
    public bool Registered { get; init; }
    public bool EffectsDisabled { get; init; }
    public bool IsAdmin { get; init; }
    public string? EndpointId { get; init; }
    public string EndpointName { get; init; } = "";
    public string DriverPath { get; init; } = "";
    public string? Problem { get; init; }
    public bool HasForeignModeEffect { get; init; }
    public bool ProtectedAudioDisabled { get; init; }
}

/// <summary>Endpoint-scoped setup. Writes only an explicitly selected render endpoint's FxProperties.</summary>
public static class DriverSetup
{
    const string ViperClsid = "{B5A2C3D4-E6F7-4A8B-9C0D-1E2F3A4B5C6D}";
    const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    const int MaximumBackupBytes = 20 * 1024 * 1024;
    const string ModeEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6";
    const string CompositeMode = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},14";
    const string StreamEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
    const string CompositeStream = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";
    const string ProcessingModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},6";
    const string DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    const string DisableEffects = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    const uint ReadControl = 0x20000, Wow64 = 0x100, QueryValue = 1, SetValue = 2;
    static readonly string[] ChangedSlots = { ModeEffect, CompositeMode, StreamEffect, CompositeStream, ProcessingModes };
    static readonly IntPtr LocalMachine = new(unchecked((int)0x80000002));
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DriverDiagnosis Inspect(OutputEndpoint? endpoint)
    {
        bool admin = IsAdministrator(), installed = false, registered = false, disabled = false, foreign = false, protectedDisabled = false;
        string path = "", name = endpoint?.Name ?? "Выход Windows не найден";
        string? id = null, problem = null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var com = machine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + ViperClsid + @"\InprocServer32");
            path = Environment.ExpandEnvironmentVariables(com?.GetValue("") as string ?? "");
            installed = path.Length != 0 && File.Exists(path);
            using var audio = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio");
            protectedDisabled = Convert.ToInt32(audio?.GetValue("DisableProtectedAudioDG", 0) ?? 0) == 1;
            if (endpoint != null)
            {
                id = NormalizeEndpointId(endpoint.Id);
                using var key = machine.OpenSubKey(RenderRoot + "\\" + id);
                if (key == null) problem = "Выбранный выход больше не существует. Обновите список устройств.";
                else
                {
                    using var fx = key.OpenSubKey("FxProperties");
                    registered = new[] { ModeEffect, CompositeMode, StreamEffect, CompositeStream }.Any(slot => ContainsClsid(fx?.GetValue(slot), ViperClsid));
                    foreign = new[] { ModeEffect, CompositeMode }.Any(slot => HasForeignClsid(fx?.GetValue(slot)));
                    disabled = Convert.ToInt32(fx?.GetValue(DisableEffects, 0) ?? 0) != 0;
                }
            }
            if (!installed) problem = "Драйвер ViPER4Windows не установлен или его DLL отсутствует.";
            else if (id == null) problem = "Выход Windows не найден.";
            else if (disabled) problem = "На этом выходе отключены звуковые улучшения Windows. APO не сможет обрабатывать звук.";
            else if (!registered) problem = foreign ? "На этом выходе уже установлен другой эффект MFX. Автоматическая замена запрещена, чтобы сохранить его работу." : "Драйвер установлен, но к выбранному выходу Windows ещё не подключён.";
            else if (!protectedDisabled) problem = "Защищённый аудиопроцесс Windows может блокировать неподписанный APO. Проверьте установку драйвера.";
        }
        catch (Exception e) { problem = "Не удалось прочитать состояние драйвера: " + e.Message; }
        var lines = new List<string> { "Выход: " + name, "Драйвер: " + (installed ? "установлен" : "не установлен"), "Регистрация выхода: " + (registered ? "есть" : "отсутствует"), "Звуковые улучшения: " + (disabled ? "отключены" : "разрешены"), "Права администратора: " + (admin ? "есть" : "нет") };
        if (path.Length != 0) lines.Add("DLL: " + path);
        if (problem != null) lines.Add(problem);
        return new DriverDiagnosis { Summary = string.Join("\n", lines), Installed = installed, Registered = registered, EffectsDisabled = disabled, IsAdmin = admin, EndpointId = id, EndpointName = name, DriverPath = path, Problem = problem, HasForeignModeEffect = foreign, ProtectedAudioDisabled = protectedDisabled };
    }

    public static string Register(string endpointId, string backupDir)
        => ChangeRegistration(endpointId, backupDir, false);
    public static string Unregister(string endpointId, string backupDir)
        => ChangeRegistration(endpointId, backupDir, true);
    static string ChangeRegistration(string endpointId, string backupDir, bool remove)
    {
        RequireAdministrator();
        string id = NormalizeEndpointId(endpointId), path = RenderRoot + "\\" + id + "\\FxProperties";
        ValidateEndpointExists(id);
        var diagnosis = Inspect(new OutputEndpoint(id, id, false));
        if (!remove && !diagnosis.Installed) throw new InvalidOperationException(diagnosis.Problem);
        if (!remove && diagnosis.EffectsDisabled) throw new InvalidOperationException(diagnosis.Problem);
        using var backupPrivilege = new Privilege("SeBackupPrivilege");
        using var restorePrivilege = new Privilege("SeRestorePrivilege");
        using var securityPrivilege = new Privilege("SeSecurityPrivilege");
        var snapshot = Capture(id, path);
        foreach (string slot in new[] { ModeEffect, CompositeMode })
        {
            var value = snapshot.Values.FirstOrDefault(x => x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
            if (value != null && (value.Type is not 1 and not 7 || !remove && HasForeignClsid(Decode(value)))) throw new InvalidOperationException("Выход уже содержит другой MFX или неизвестный формат его настройки. Автоматическая замена запрещена.");
        }
        var modes = snapshot.Values.FirstOrDefault(x => x.Name.Equals(ProcessingModes, StringComparison.OrdinalIgnoreCase));
        if (modes != null && modes.Type is not 1 and not 7) throw new InvalidDataException("Список режимов MFX имеет неизвестный формат. Настройки не изменены.");
        ValidateBackup(snapshot);
        byte[] serializedBackup = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (serializedBackup.Length > MaximumBackupBytes) throw new InvalidDataException("Резервная копия слишком велика. Настройки не изменены.");
        Directory.CreateDirectory(backupDir);
        string backupFile = Path.Combine(Path.GetFullPath(backupDir), "endpoint-" + id.Trim('{', '}') + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
        using (var stream = new FileStream(backupFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(serializedBackup); stream.Flush(true);
        }
        IntPtr key = IntPtr.Zero; bool writeOpened = false;
        try
        {
            key = OpenWritable(path);
            writeOpened = true;
            // One MFX placement. Remove only this same APO from other placements;
            // foreign stream/endpoint effects and all global audio settings stay intact.
            RemoveViperFromSlot(key, snapshot, StreamEffect);
            RemoveViperFromSlot(key, snapshot, CompositeStream);
            RemoveViperFromSlot(key, snapshot, CompositeMode);
            if (remove) RemoveViperFromSlot(key, snapshot, ModeEffect);
            else
            {
            SetString(key, ModeEffect, ViperClsid);
            var modeList = AsStrings(modes == null ? null : Decode(modes)).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (!modeList.Contains(DefaultMode, StringComparer.OrdinalIgnoreCase)) modeList.Add(DefaultMode);
            SetMultiString(key, ProcessingModes, modeList);
            }
            VerifySecurityUnchanged(key, snapshot);
        }
        catch (Exception error)
        {
            if (key != IntPtr.Zero) { RegCloseKey(key); key = IntPtr.Zero; }
            try { if (writeOpened) RestoreCore(snapshot); }
            catch (Exception rollback) { throw new InvalidOperationException("Подключение не завершено, откат требует внимания. Резервная копия: " + backupFile + "\n" + error.Message + "\nОткат: " + rollback.Message, error); }
            throw new InvalidOperationException("Подключение не завершено. Исходные настройки восстановлены. Резервная копия: " + backupFile + "\n" + error.Message, error);
        }
        finally { if (key != IntPtr.Zero) RegCloseKey(key); }
        return (remove ? "Назначение ViPER APO на выбранном выходе снято." : "Выход подключён к одному MFX ViPER4Windows.") + " Изменения вступят в силу после обновления аудиопотока или перезапуска аудио.\nРезервная копия: " + backupFile;
    }

    public static string Restore(string backupFile)
    {
        RequireAdministrator();
        var info = new FileInfo(backupFile);
        if (!info.Exists || info.Length > MaximumBackupBytes) throw new InvalidDataException("Некорректная резервная копия.");
        var snapshot = JsonSerializer.Deserialize<EndpointBackup>(File.ReadAllText(backupFile)) ?? throw new InvalidDataException("Резервная копия пуста.");
        ValidateBackup(snapshot);
        string id = NormalizeEndpointId(snapshot.EndpointId);
        ValidateEndpointExists(id);
        using var backupPrivilege = new Privilege("SeBackupPrivilege");
        using var restorePrivilege = new Privilege("SeRestorePrivilege");
        using var securityPrivilege = new Privilege("SeSecurityPrivilege");
        RestoreCore(snapshot);
        return "Пять настроек подключения выбранного выхода восстановлены. Остальные значения, владелец и права доступа сохранены.\nРезервная копия: " + Path.GetFullPath(backupFile);
    }

    static void RestoreCore(EndpointBackup snapshot)
    {
        ValidateBackup(snapshot);
        IntPtr key = IntPtr.Zero;
        try
        {
            key = OpenWritable(snapshot.RegistryPath);
            foreach (string slot in ChangedSlots)
            {
                var value = snapshot.Values.FirstOrDefault(x => x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
                if (value == null)
                {
                    int rc = RegDeleteValueW(key, slot);
                    if (rc != 2) Check(rc, "Удаление добавленного значения " + slot);
                }
                else
                {
                    byte[] bytes = Convert.FromBase64String(value.Data);
                    Check(RegSetValueExW(key, value.Name, 0, value.Type, bytes, (uint)bytes.Length), "Восстановление значения " + value.Name);
                }
            }
            // Registration never changes security. Do not overwrite a later ACL or
            // owner change during a value restore; the complete original is retained
            // in the backup for inspection and recovery by the owner.
        }
        finally { if (key != IntPtr.Zero) RegCloseKey(key); }
        // Keep an empty, newly-created FxProperties key. Deleting it could remove
        // subkeys or unrelated settings subsequently added by Windows or a plugin.
    }

    static EndpointBackup Capture(string id, string path)
    {
        var snapshot = new EndpointBackup { EndpointId = id, RegistryPath = path, CreatedUtc = DateTime.UtcNow };
        int rc = RegOpenKeyExW(LocalMachine, path, 0, QueryValue | ReadControl | Wow64, out var key);
        if (rc == 2) return snapshot;
        Check(rc, "Чтение исходных настроек до подключения");
        try
        {
            snapshot.Existed = true; snapshot.Values = ReadValues(key);
            snapshot.SecurityInformation = 7; snapshot.SecurityDescriptor = Convert.ToBase64String(ReadSecurity(key, 7));
            snapshot.BaseSecurityDescriptor = snapshot.SecurityDescriptor;
            if (RegOpenKeyExW(LocalMachine, path, 0, QueryValue | ReadControl | Wow64 | 0x1000000, out var allSecurity) == 0)
            {
                try { snapshot.SecurityDescriptor = Convert.ToBase64String(ReadSecurity(allSecurity, 15)); snapshot.SecurityInformation = 15; }
                finally { RegCloseKey(allSecurity); }
            }
            return snapshot;
        }
        finally { RegCloseKey(key); }
    }

    static void VerifySecurityUnchanged(IntPtr key, EndpointBackup snapshot)
    {
        if (snapshot.Existed && !ReadSecurity(key, 7).SequenceEqual(Convert.FromBase64String(snapshot.BaseSecurityDescriptor)))
            throw new InvalidOperationException("Права доступа к выходу изменились во время подключения. Выполняется откат.");
    }
    static void ValidateBackup(EndpointBackup snapshot)
    {
        string id = NormalizeEndpointId(snapshot.EndpointId ?? "");
        if (snapshot.Schema != 1 || snapshot.RegistryPath != RenderRoot + "\\" + id + "\\FxProperties" || snapshot.Values == null || snapshot.Values.Count > 4096 || snapshot.SecurityInformation is not 7 and not 15)
            throw new InvalidDataException("Резервная копия не относится к поддерживаемому выходу Windows.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var value in snapshot.Values)
        {
            if (value == null || value.Name == null || value.Name.Length > 16383 || value.Name.Contains('\0') || !names.Add(value.Name) || value.Data == null || value.Type > 11)
                throw new InvalidDataException("Некорректное имя, тип или повтор значения резервной копии.");
            byte[] data;
            try { data = Convert.FromBase64String(value.Data); }
            catch (FormatException e) { throw new InvalidDataException("Некорректные данные резервной копии.", e); }
            total += data.Length;
            if (data.Length > 8 * 1024 * 1024 || total > 12 * 1024 * 1024) throw new InvalidDataException("Данные резервной копии слишком велики.");
        }
        if (snapshot.Existed)
        {
            ValidateDescriptor(snapshot.SecurityDescriptor);
            ValidateDescriptor(snapshot.BaseSecurityDescriptor);
        }
        else if (snapshot.Values.Count != 0 || !string.IsNullOrEmpty(snapshot.SecurityDescriptor) || !string.IsNullOrEmpty(snapshot.BaseSecurityDescriptor))
            throw new InvalidDataException("Резервная копия отсутствовавшего ключа содержит неожиданные данные.");
    }
    static void ValidateDescriptor(string encoded)
    {
        byte[] data;
        try { data = Convert.FromBase64String(encoded ?? ""); }
        catch (FormatException e) { throw new InvalidDataException("Некорректный дескриптор безопасности резервной копии.", e); }
        if (data.Length < 20 || data.Length > 1024 * 1024 || (BitConverter.ToUInt16(data, 2) & 0x8000) == 0 || !IsValidSecurityDescriptor(data))
            throw new InvalidDataException("Некорректный дескриптор безопасности резервной копии.");
    }

    static List<ValueBackup> ReadValues(IntPtr key)
    {
        var result = new List<ValueBackup>();
        for (uint index = 0; index < 4096; index++)
        {
            var name = new StringBuilder(16384); uint nameLength = 16383, size = 0, kind;
            int rc = RegEnumValueW(key, index, name, ref nameLength, IntPtr.Zero, out kind, null, ref size);
            if (rc == 259) return result;
            if (rc != 0 && rc != 234) Check(rc, "Перечисление настроек");
            if (size > 8 * 1024 * 1024) throw new InvalidDataException("Значение FxProperties слишком велико для резервной копии.");
            byte[] data = new byte[size]; nameLength = 16383;
            Check(RegEnumValueW(key, index, name, ref nameLength, IntPtr.Zero, out kind, data, ref size), "Чтение настройки");
            if (size != data.Length) Array.Resize(ref data, checked((int)size));
            result.Add(new ValueBackup { Name = name.ToString(), Type = kind, Data = Convert.ToBase64String(data) });
        }
        throw new InvalidDataException("Слишком много значений FxProperties.");
    }
    static byte[] ReadSecurity(IntPtr key, uint information)
    {
        uint size = 0; int rc = RegGetKeySecurity(key, information, null, ref size);
        if (rc != 0 && rc != 122) Check(rc, "Чтение владельца и ACL");
        if (size == 0 || size > 1024 * 1024) throw new InvalidDataException("Некорректный дескриптор безопасности выхода.");
        byte[] data = new byte[size]; Check(RegGetKeySecurity(key, information, data, ref size), "Чтение владельца и ACL"); return data;
    }
    static IntPtr OpenWritable(string path)
    {
        int rc = RegOpenKeyExW(LocalMachine, path, 0, QueryValue | SetValue | ReadControl | Wow64, out var key);
        if (rc == 0) return key;
        if (rc is 5 or 2) return OpenBackupRestore(path);
        Check(rc, "Открытие выбранного выхода для записи"); return IntPtr.Zero;
    }
    static IntPtr OpenBackupRestore(string path)
    {
        Check(RegCreateKeyExW(LocalMachine, path, 0, null, 4, QueryValue | SetValue | ReadControl | Wow64, IntPtr.Zero, out var key, out var disposition), "Доступ к выбранному выходу с привилегиями резервного восстановления");
        return key;
    }
    static void RemoveViperFromSlot(IntPtr key, EndpointBackup snapshot, string slot)
    {
        var value = snapshot.Values.FirstOrDefault(x => x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (value == null || !ContainsClsid(Decode(value), ViperClsid)) return;
        var remaining = AsStrings(Decode(value)).Where(x => !x.Equals(ViperClsid, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remaining.Length == 0) Check(RegDeleteValueW(key, slot), "Удаление повторного размещения ViPER");
        else if (value.Type == 7) SetMultiString(key, slot, remaining);
    }
    static object? Decode(ValueBackup value) => value.Type switch { 1 or 2 => Encoding.Unicode.GetString(Convert.FromBase64String(value.Data)).TrimEnd('\0'), 7 => Encoding.Unicode.GetString(Convert.FromBase64String(value.Data)).Split('\0', StringSplitOptions.RemoveEmptyEntries), _ => null };
    static IEnumerable<string> AsStrings(object? value) => value is string s ? new[] { s } : value as string[] ?? Array.Empty<string>();
    static bool ContainsClsid(object? value, string clsid) => AsStrings(value).Any(x => x.Equals(clsid, StringComparison.OrdinalIgnoreCase));
    static bool HasForeignClsid(object? value) => AsStrings(value).Any(x => !string.IsNullOrWhiteSpace(x) && !x.Equals(ViperClsid, StringComparison.OrdinalIgnoreCase) && x != "{00000000-0000-0000-0000-000000000000}");
    static void SetString(IntPtr key, string name, string value) { byte[] data = Encoding.Unicode.GetBytes(value + "\0"); Check(RegSetValueExW(key, name, 0, 1, data, (uint)data.Length), "Запись " + name); }
    static void SetMultiString(IntPtr key, string name, IEnumerable<string> values) { byte[] data = Encoding.Unicode.GetBytes(string.Join("\0", values) + "\0\0"); Check(RegSetValueExW(key, name, 0, 7, data, (uint)data.Length), "Запись " + name); }
    static string NormalizeEndpointId(string value)
    {
        const string prefix = "{0.0.0.00000000}.";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..];
        if (!Guid.TryParseExact(value, "B", out var guid)) throw new ArgumentException("Нужен точный GUID выхода Windows.", nameof(value));
        return guid.ToString("B");
    }
    static void ValidateEndpointExists(string id) { using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); using var endpoint = machine.OpenSubKey(RenderRoot + "\\" + id); if (endpoint == null) throw new InvalidOperationException("Указанный выход Windows отсутствует."); }
    static bool IsAdministrator() { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    static void RequireAdministrator() { if (!Environment.Is64BitProcess) throw new InvalidOperationException("Подключение драйвера требует 64-разрядного приложения."); if (!IsAdministrator()) throw new InvalidOperationException("Подключение и восстановление выхода выполняются от администратора."); }
    static void Check(int code, string action) { if (code != 0) throw new Win32Exception(code, action + ": " + new Win32Exception(code).Message); }

    public sealed class EndpointBackup
    {
        public int Schema { get; set; } = 1;
        public string EndpointId { get; set; } = "";
        public string RegistryPath { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public bool Existed { get; set; }
        public uint SecurityInformation { get; set; } = 7;
        public string SecurityDescriptor { get; set; } = "";
        public string BaseSecurityDescriptor { get; set; } = "";
        public List<ValueBackup> Values { get; set; } = new();
    }
    public sealed class ValueBackup { public string Name { get; set; } = ""; public uint Type { get; set; } public string Data { get; set; } = ""; }

    sealed class Privilege : IDisposable
    {
        IntPtr token; TokenPrivilege previous; bool changed;
        public Privilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) return;
            if (!LookupPrivilegeValueW(null, name, out var luid)) return;
            var requested = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 };
            changed = AdjustTokenPrivileges(token, false, ref requested, (uint)Marshal.SizeOf<TokenPrivilege>(), out previous, out _) && Marshal.GetLastWin32Error() == 0 && previous.Count != 0;
        }
        public void Dispose() { if (token == IntPtr.Zero) return; if (changed) AdjustTokenPrivileges(token, false, ref previous, 0, out _, out _); CloseHandle(token); token = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegOpenKeyExW(IntPtr root, string path, uint options, uint access, out IntPtr key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegCreateKeyExW(IntPtr root, string path, uint reserved, string? cls, uint options, uint access, IntPtr attributes, out IntPtr key, out uint disposition);
    [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegEnumValueW(IntPtr key, uint index, StringBuilder name, ref uint nameLength, IntPtr reserved, out uint type, byte[]? data, ref uint dataLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegSetValueExW(IntPtr key, string name, uint reserved, uint type, byte[] data, uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegDeleteValueW(IntPtr key, string name);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegDeleteKeyExW(IntPtr root, string path, uint access, uint reserved);
    [DllImport("advapi32.dll")] static extern int RegGetKeySecurity(IntPtr key, uint information, byte[]? descriptor, ref uint size);
    [DllImport("advapi32.dll")] static extern int RegSetKeySecurity(IntPtr key, uint information, byte[] descriptor);
    [DllImport("advapi32.dll")] static extern bool IsValidSecurityDescriptor(byte[] descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege next, uint bufferLength, out TokenPrivilege previous, out uint returnLength);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
