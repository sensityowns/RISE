using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Rise.Services;

internal sealed record RegistrySetting(string SubKey, string Name, object Value, RegistryValueKind Kind);

internal sealed record TweakDefinition(
    string Id,
    string Name,
    string Category,
    string Description,
    string Risk,
    bool RestartRequired,
    string Requires,
    string Kind,
    string Tier,
    IReadOnlyList<RegistrySetting> Settings,
    string? UnsupportedReason);

internal sealed class TweakService
{
    private const int MaximumCatalogBytes = 1_000_000;
    private static readonly string[] AllowedRegistryRoots =
    [
        @"Software\Microsoft\Windows\CurrentVersion",
        @"Software\Microsoft\GameBar",
        @"Software\Microsoft\InputPersonalization",
        @"Software\Microsoft\DirectX\UserGpuPreferences",
        @"Software\Microsoft\Siuf\Rules",
        @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy",
        @"Software\Microsoft\Windows\DWM",
        @"System\GameConfigStore",
        @"Control Panel\Desktop",
        @"Control Panel\Mouse",
        @"Control Panel\Keyboard",
        @"Control Panel\Accessibility",
        @"Control Panel\Cursors",
        @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"
    ];

    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RISE");
    private readonly string _backupFile;
    private readonly string _logFile;
    private Dictionary<string, SavedRegistryValue> _backups;

    public TweakService()
    {
        Directory.CreateDirectory(_directory);
        _backupFile = Path.Combine(_directory, "registry-backups.json");
        _logFile = Path.Combine(_directory, "activity.log");
        CatalogPath = Path.Combine(_directory, "Tweaks.json");
        EnsureCatalogExists(CatalogPath);
        Definitions = LoadUserCatalog();
        _backups = LoadBackups();
    }

    public string CatalogPath { get; }
    public IReadOnlyList<TweakDefinition> Definitions { get; }
    public int FreeCatalogCount => Definitions.Count(t => t.Tier == "Free");
    public int PremiumCatalogCount => Definitions.Count(t => t.Tier == "Premium");
    public int SupportedCount => Definitions.Count(CanApply);
    public int SupportedFreeCount => Definitions.Count(t => t.Tier == "Free" && IsSupported(t));

    public static bool IsDeveloperBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public bool CanApply(TweakDefinition tweak) =>
        IsSupported(tweak) && (tweak.Tier == "Free" || IsDeveloperBuild && tweak.Tier == "Premium");

    public bool IsApplied(TweakDefinition tweak) =>
        CanApply(tweak) && tweak.Settings.All(setting => Equals(ReadCurrent(setting), setting.Value));

    public string GetStatus(TweakDefinition tweak)
    {
        if (!CanApply(tweak))
            return tweak.UnsupportedReason ?? (tweak.Tier == "Premium"
                ? "Premium entries are not enabled in the public build."
                : "This action is not supported in this build.");

        if (IsApplied(tweak))
            return HasBackup(tweak) ? "Applied · original values backed up" : "Current values already match";
        return HasBackup(tweak) ? "Changed · original values backed up" : "Not applied";
    }

    public bool HasBackup(TweakDefinition tweak) => tweak.Settings.Any(setting => _backups.ContainsKey(Key(setting)));

    public void Apply(TweakDefinition tweak)
    {
        if (!CanApply(tweak))
            throw new InvalidOperationException(GetStatus(tweak));

        foreach (var setting in tweak.Settings)
        {
            var key = Key(setting);
            if (_backups.ContainsKey(key)) continue;

            using var registry = Registry.CurrentUser.OpenSubKey(setting.SubKey, writable: false);
            var existing = registry?.GetValue(setting.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (existing is null)
            {
                _backups[key] = new SavedRegistryValue(false, null, null);
            }
            else
            {
                var kind = registry!.GetValueKind(setting.Name);
                _backups[key] = new SavedRegistryValue(true, EncodeValue(existing, kind), (int)kind);
            }
        }
        SaveBackups();

        foreach (var setting in tweak.Settings)
        {
            using var registry = Registry.CurrentUser.CreateSubKey(setting.SubKey, writable: true);
            registry.SetValue(setting.Name, setting.Value, setting.Kind);
        }
        Log($"Applied tweak: {tweak.Id}");
    }

    public void Restore(TweakDefinition tweak)
    {
        var restored = 0;
        foreach (var setting in tweak.Settings)
        {
            var key = Key(setting);
            if (!_backups.TryGetValue(key, out var original)) continue;

            using (var registry = Registry.CurrentUser.CreateSubKey(setting.SubKey, writable: true))
            {
                if (original.Exists && original.Kind is int kind && Enum.IsDefined(typeof(RegistryValueKind), kind))
                    registry.SetValue(setting.Name, ParseValue(original.Value, (RegistryValueKind)kind), (RegistryValueKind)kind);
                else
                    registry.DeleteValue(setting.Name, throwOnMissingValue: false);
            }
            _backups.Remove(key);
            restored++;
        }

        if (restored == 0) return;

        SaveBackups();
        Log($"Restored tweak: {tweak.Id}");
    }

    public IReadOnlyList<string> ReadLog()
    {
        try { return File.Exists(_logFile) ? File.ReadAllLines(_logFile).TakeLast(100).Reverse().ToArray() : []; }
        catch (IOException) { return ["Activity log is unavailable."]; }
    }

    private static bool IsSupported(TweakDefinition tweak) =>
        tweak.UnsupportedReason is null && tweak.Settings.Count > 0;

    private static IReadOnlyList<TweakDefinition> LoadDefinitions(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumCatalogBytes)
            throw new InvalidDataException("The RISE tweak catalog is too large to load.");

        CatalogDocument catalog;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            catalog = JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllText(path), options)
                ?? throw new InvalidDataException("The RISE tweak catalog is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The RISE tweak catalog contains invalid JSON.", ex);
        }

        var entries = catalog.Tweaks;
        if (catalog.Version != 1 || entries is null || entries.Count > 500)
            throw new InvalidDataException("The RISE tweak catalog version or entry count is not supported.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<TweakDefinition>(entries.Count);
        foreach (var entry in entries)
        {
            if (!IsValidEntry(entry) || !ids.Add(entry.Id))
                throw new InvalidDataException("The RISE tweak catalog contains an invalid entry or duplicate ID.");

            var settings = new List<RegistrySetting>();
            string? unsupportedReason = ValidateForExecution(entry, settings);
            definitions.Add(new TweakDefinition(
                entry.Id,
                entry.Name,
                entry.Category,
                entry.Description,
                entry.Risk,
                entry.RequiresReboot,
                entry.Requires,
                entry.Kind,
                entry.Tier,
                settings,
                unsupportedReason));
        }

        return definitions.AsReadOnly();
    }

    private static bool IsValidEntry(CatalogEntry? entry) =>
        entry is not null
        && IsCatalogText(entry.Id, 1, 100)
        && entry.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && IsCatalogText(entry.Name, 1, 160)
        && IsCatalogText(entry.Category, 1, 80)
        && IsCatalogText(entry.Description, 1, 1000)
        && IsCatalogText(entry.Risk, 1, 20)
        && IsCatalogText(entry.Requires, 1, 100)
        && IsCatalogText(entry.Kind, 1, 30)
        && entry.Tier is "Free" or "Premium"
        && entry.Actions is { Count: <= 20 } actions
        && actions.All(action => action is not null);

    private static bool IsCatalogText(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum;

    private static string? ValidateForExecution(CatalogEntry entry, List<RegistrySetting> settings)
    {
        var actions = entry.Actions;
        if (actions is null)
            return "This catalog entry has an invalid action list.";
        if (entry.Risk != "Low")
            return $"Catalog risk is {entry.Risk}; only Low-risk actions are enabled.";
        if (entry.Requires != "None")
            return $"Requires {entry.Requires}; hardware-specific actions are not enabled.";
        if (entry.Kind != "Toggle")
            return $"{entry.Kind} actions are not enabled.";
        if (actions.Count == 0)
            return "This catalog entry has no executable action.";

        foreach (var action in actions)
        {
            if (action is null)
                return "This catalog entry contains an invalid action.";
            if (!string.Equals(action.Type, "registry", StringComparison.OrdinalIgnoreCase))
                return $"Action type '{action.Type}' is not enabled. Shell commands, services, network changes, and powercfg actions are never run from the catalog.";
            if (!string.Equals(action.Hive, "HKCU", StringComparison.OrdinalIgnoreCase))
                return "Only current-user registry values are supported; administrator-level writes are not enabled.";
            if (string.IsNullOrWhiteSpace(action.Path) || !IsAllowedRegistryPath(action.Path))
                return "The registry path is outside the supported current-user settings.";
            if (string.IsNullOrWhiteSpace(action.Name) || action.Name.Length > 163)
                return "The registry value name is invalid.";

            var setting = ParseRegistrySetting(action);
            if (setting is null)
                return "Only validated DWORD and String values are supported.";
            settings.Add(setting);
        }

        return entry.Tier == "Premium" && !IsDeveloperBuild
            ? "Premium entries are visible in the catalog but are not enabled in the public build."
            : null;
    }

    private static bool IsAllowedRegistryPath(string path)
    {
        if (path.Length > 512 || path.Contains('/') || path.Contains(':')
            || path.Split('\\').Any(segment => segment is "" or "." or ".."))
            return false;
        if (path.Split('\\').Any(segment => segment.Equals("Run", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("RunOnce", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Services", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Tasks", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Policies", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("CapabilityAccessManager", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Winlogon", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("AppInit_DLLs", StringComparison.OrdinalIgnoreCase)))
            return false;

        return AllowedRegistryRoots.Any(root =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase));
    }

    private static RegistrySetting? ParseRegistrySetting(CatalogAction action)
    {
        if (string.Equals(action.Kind, "DWord", StringComparison.OrdinalIgnoreCase)
            && action.Value.ValueKind == JsonValueKind.Number
            && action.Value.TryGetInt32(out var dword))
            return new RegistrySetting(action.Path!, action.Name!, dword, RegistryValueKind.DWord);

        if (string.Equals(action.Kind, "String", StringComparison.OrdinalIgnoreCase)
            && action.Value.ValueKind == JsonValueKind.String
            && action.Value.GetString() is { } text
            && text.Length <= 4096)
            return new RegistrySetting(action.Path!, action.Name!, text, RegistryValueKind.String);

        return null;
    }

    private static void EnsureCatalogExists(string catalogPath)
    {
        if (File.Exists(catalogPath)) return;
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Tweaks.json");
        if (!File.Exists(bundledPath))
            throw new FileNotFoundException("The bundled RISE tweak catalog is missing.", bundledPath);
        File.Copy(bundledPath, catalogPath);
    }

    private IReadOnlyList<TweakDefinition> LoadUserCatalog()
    {
        try
        {
            return LoadDefinitions(CatalogPath);
        }
        catch (InvalidDataException ex)
        {
            var recoveryPath = Path.Combine(_directory, $"Tweaks.invalid-{Guid.NewGuid():N}.json");
            File.Move(CatalogPath, recoveryPath);
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Tweaks.json");
            File.Copy(bundledPath, CatalogPath, overwrite: true);
            Log($"Invalid catalog preserved at {Path.GetFileName(recoveryPath)}; restored the bundled catalog. {ex.Message}");
            return LoadDefinitions(CatalogPath);
        }
    }

    private void SaveBackups() =>
        File.WriteAllText(_backupFile, JsonSerializer.Serialize(_backups, new JsonSerializerOptions { WriteIndented = true }));

    private Dictionary<string, SavedRegistryValue> LoadBackups()
    {
        try { return File.Exists(_backupFile) ? JsonSerializer.Deserialize<Dictionary<string, SavedRegistryValue>>(File.ReadAllText(_backupFile)) ?? [] : []; }
        catch (JsonException) { Log("Registry backup file could not be read; current system values were left unchanged."); return []; }
        catch (IOException) { return []; }
    }

    private void Log(string message) => File.AppendAllText(_logFile, $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");

    private static string Key(RegistrySetting setting) => $"{setting.SubKey}|{setting.Name}";

    private static object? ReadCurrent(RegistrySetting setting)
    {
        using var key = Registry.CurrentUser.OpenSubKey(setting.SubKey);
        return key?.GetValue(setting.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static string EncodeValue(object value, RegistryValueKind kind) =>
        kind switch
        {
            RegistryValueKind.DWord or RegistryValueKind.QWord => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            RegistryValueKind.String or RegistryValueKind.ExpandString => (string)value,
            RegistryValueKind.Binary or RegistryValueKind.None => Convert.ToBase64String((byte[])value),
            RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])value),
            _ => throw new InvalidOperationException($"Registry value type '{kind}' cannot be backed up safely.")
        };

    private static object ParseValue(string? value, RegistryValueKind kind) =>
        kind switch
        {
            RegistryValueKind.DWord when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            RegistryValueKind.QWord when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            RegistryValueKind.String or RegistryValueKind.ExpandString => value ?? string.Empty,
            RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(value ?? string.Empty),
            RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value ?? "[]") ?? [],
            _ => throw new InvalidOperationException($"The saved registry value type '{kind}' is invalid.")
        };

    private sealed class CatalogDocument
    {
        public int Version { get; init; }
        public List<CatalogEntry>? Tweaks { get; init; } = [];
    }

    private sealed class CatalogEntry
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Risk { get; init; } = string.Empty;
        public bool RequiresReboot { get; init; }
        public string Requires { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Tier { get; init; } = string.Empty;
        public List<CatalogAction>? Actions { get; init; } = [];
    }

    private sealed class CatalogAction
    {
        public string Type { get; init; } = string.Empty;
        public string? Hive { get; init; }
        public string? Path { get; init; }
        public string? Name { get; init; }
        public string? Kind { get; init; }
        public JsonElement Value { get; init; }
    }

    private sealed record SavedRegistryValue(bool Exists, string? Value, int? Kind);
}
