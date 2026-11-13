using System.Text.Json;
using System.IO;

namespace Rise.Services;

internal sealed class AppSettings
{
    public string Accent { get; set; } = "#FFFF762E";
    public string BoostProfile { get; set; } = "Balanced";
    public Dictionary<string, string> GamePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool LaunchAtStartup { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
}

internal sealed class SettingsService
{
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RISE",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_file)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
