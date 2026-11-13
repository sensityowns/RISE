using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace Rise.Services;

internal sealed record RegistrySetting(string SubKey, string Name, object Value, RegistryValueKind Kind);

internal sealed record TweakDefinition(
    string Id,
    string Name,
    string Category,
    string Description,
    string Benefit,
    string Tradeoff,
    bool RestartRequired,
    IReadOnlyList<RegistrySetting> Settings);

internal sealed class TweakService
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RISE");
    private readonly string _backupFile;
    private readonly string _logFile;
    private Dictionary<string, SavedRegistryValue> _backups;

    public TweakService()
    {
        Directory.CreateDirectory(_directory);
        _backupFile = Path.Combine(_directory, "registry-backups.json");
        _logFile = Path.Combine(_directory, "activity.log");
        _backups = LoadBackups();
    }

    public static IReadOnlyList<TweakDefinition> Definitions { get; } =
    [
        new("suggestions", "Reduce Windows suggestions", "Windows", "Turns off consumer suggestion content for the current Windows user.", "Fewer promotional suggestions in Windows surfaces.", "Windows may show less contextual setup guidance.", false, [Setting(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0, RegistryValueKind.DWord)]),
        new("background-apps", "Restrict background apps", "Windows", "Disables background app execution where the Windows setting is honored.", "May reduce background activity for supported Store apps.", "Background updates and notifications may be delayed.", false, [Setting(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, RegistryValueKind.DWord)]),
        new("game-dvr", "Disable background Game DVR capture", "Gaming", "Disables background game recording for the current user.", "Prevents background recording overhead when it is not wanted.", "Background clips and capture shortcuts will not work.", false, [Setting(@"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord)]),
        new("game-mode", "Enable Windows Game Mode", "Gaming", "Enables Windows Game Mode for the current user.", "Lets Windows apply its built-in gaming policy.", "Windows controls scheduling behavior while a game is detected.", false, [Setting(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord), Setting(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord)]),
        new("mouse-acceleration", "Turn off mouse acceleration", "Input", "Disables Enhance pointer precision for the current user.", "Provides a more consistent cursor response to the same physical mouse movement.", "Some users prefer pointer acceleration.", false, [Setting(@"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String), Setting(@"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String), Setting(@"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String)]),
        new("advertising-id", "Disable the advertising ID", "Privacy", "Disables the Windows advertising ID for the current user.", "Reduces use of the Windows advertising identifier in apps.", "Personalized app advertising may be reduced.", false, [Setting(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, RegistryValueKind.DWord)]),
        new("input-personalization", "Restrict input data collection", "Privacy", "Restricts optional typing and inking data collection for the current user.", "Reduces optional collection used for personalized input features.", "Personalized typing and inking suggestions may be unavailable.", false, [Setting(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, RegistryValueKind.DWord), Setting(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, RegistryValueKind.DWord)]),
        new("window-animation", "Reduce window animations", "Accessibility", "Turns off the legacy window minimize/maximize animation for the current user.", "Reduces one visual transition.", "Window transitions will be less animated.", false, [Setting(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String)]),
        new("search-highlights", "Turn off search highlights", "Windows", "Turns off search box highlight content for the current user.", "Reduces promoted and trending content in Windows Search.", "Search highlights and related content are hidden.", false, [Setting(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, RegistryValueKind.DWord)]),
        new("widgets", "Turn off taskbar widgets", "Windows", "Disables the Windows taskbar Widgets entry for the current user.", "Removes the Widgets taskbar entry and its feed access point.", "Widgets are not available from the taskbar.", false, [Setting(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0, RegistryValueKind.DWord)])
    ];

    public string GetStatus(TweakDefinition tweak)
    {
        var applied = tweak.Settings.All(setting => Equals(ReadCurrent(setting), setting.Value));
        return applied ? "Applied" : HasBackup(tweak) ? "Changed; original value saved" : "Not applied";
    }

    public bool HasBackup(TweakDefinition tweak) => tweak.Settings.Any(setting => _backups.ContainsKey(Key(setting)));

    public void Apply(TweakDefinition tweak)
    {
        foreach (var setting in tweak.Settings)
        {
            var key = Key(setting);
            if (!_backups.ContainsKey(key))
            {
                using var registry = Registry.CurrentUser.OpenSubKey(setting.SubKey, writable: false);
                var existing = registry?.GetValue(setting.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                _backups[key] = existing is null
                    ? new SavedRegistryValue(false, null, null)
                    : new SavedRegistryValue(true, existing.ToString(), (int)(registry!.GetValueKind(setting.Name)));
            }
        }
        SaveBackups();

        foreach (var setting in tweak.Settings)
        {
            using var registry = Registry.CurrentUser.CreateSubKey(setting.SubKey, writable: true);
            registry.SetValue(setting.Name, setting.Value, setting.Kind);
        }
        Log($"Applied tweak: {tweak.Name}");
    }

    public void Restore(TweakDefinition tweak)
    {
        foreach (var setting in tweak.Settings)
        {
            var key = Key(setting);
            if (!_backups.TryGetValue(key, out var original))
                throw new InvalidOperationException($"RISE has no saved original value for {setting.Name}; it was not changed.");

            using (var registry = Registry.CurrentUser.CreateSubKey(setting.SubKey, writable: true))
            {
                if (original.Exists && original.Kind is int kind && Enum.IsDefined(typeof(RegistryValueKind), kind))
                    registry.SetValue(setting.Name, ParseValue(original.Value, (RegistryValueKind)kind), (RegistryValueKind)kind);
                else
                    registry.DeleteValue(setting.Name, throwOnMissingValue: false);
            }
            _backups.Remove(key);
        }
        SaveBackups();
        Log($"Restored tweak: {tweak.Name}");
    }

    public IReadOnlyList<string> ReadLog()
    {
        try { return File.Exists(_logFile) ? File.ReadAllLines(_logFile).TakeLast(100).Reverse().ToArray() : []; }
        catch (IOException) { return ["Activity log is unavailable."]; }
    }

    private static RegistrySetting Setting(string path, string name, object value, RegistryValueKind kind) => new(path, name, value, kind);
    private static string Key(RegistrySetting setting) => $"{setting.SubKey}|{setting.Name}";

    private static object? ReadCurrent(RegistrySetting setting)
    {
        using var key = Registry.CurrentUser.OpenSubKey(setting.SubKey);
        return key?.GetValue(setting.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private void SaveBackups() => File.WriteAllText(_backupFile, JsonSerializer.Serialize(_backups, new JsonSerializerOptions { WriteIndented = true }));

    private Dictionary<string, SavedRegistryValue> LoadBackups()
    {
        try { return File.Exists(_backupFile) ? JsonSerializer.Deserialize<Dictionary<string, SavedRegistryValue>>(File.ReadAllText(_backupFile)) ?? [] : []; }
        catch (JsonException) { Log("Registry backup file could not be read; current system values were left unchanged."); return []; }
        catch (IOException) { return []; }
    }

    private void Log(string message) => File.AppendAllText(_logFile, $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");

    private static object ParseValue(string? value, RegistryValueKind kind) =>
        kind == RegistryValueKind.DWord && int.TryParse(value, out var number) ? number : value ?? string.Empty;

    private sealed record SavedRegistryValue(bool Exists, string? Value, int? Kind);
}
