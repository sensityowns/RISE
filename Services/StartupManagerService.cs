using Microsoft.Win32;
using System.IO;

namespace Rise.Services;

internal sealed record StartupEntry(string Name, string Command, bool Enabled);

internal sealed class StartupManagerService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DisabledKey = @"Software\RISE\DisabledStartupApps";

    public IReadOnlyList<StartupEntry> GetEntries()
    {
        using var enabled = Registry.CurrentUser.OpenSubKey(RunKey);
        using var disabled = Registry.CurrentUser.OpenSubKey(DisabledKey);
        var enabledNames = enabled?.GetValueNames().Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var disabledNames = disabled?.GetValueNames().Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return enabledNames.Concat(disabledNames).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Select(name =>
            {
                var isEnabled = enabledNames.Contains(name);
                var source = isEnabled ? enabled : disabled;
                var value = source?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return new StartupEntry(name, FormatCommand(value), isEnabled);
            })
            .ToArray();
    }

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.Name.Equals("RISE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Manage RISE startup using the Launch at sign-in setting instead.");

        MoveEntry(entry.Name, enabled ? DisabledKey : RunKey, enabled ? RunKey : DisabledKey);
    }

    private static void MoveEntry(string name, string sourcePath, string destinationPath)
    {
        using var source = Registry.CurrentUser.OpenSubKey(sourcePath, writable: true);
        var value = source?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (source is null || value is null)
            throw new InvalidOperationException($"The startup entry “{name}” no longer exists in its expected location.");

        using var destination = Registry.CurrentUser.CreateSubKey(destinationPath, writable: true);
        if (destination.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
            throw new InvalidOperationException($"A startup entry named “{name}” already exists in the destination.");

        var kind = source.GetValueKind(name);
        destination.SetValue(name, value, kind);
        source.DeleteValue(name);
    }

    private static string FormatCommand(object? value) => value switch
    {
        string text => text,
        string[] lines => string.Join("; ", lines),
        byte[] => "(binary startup value)",
        null => "(empty startup value)",
        _ => value.ToString() ?? "(unavailable)"
    };
}
