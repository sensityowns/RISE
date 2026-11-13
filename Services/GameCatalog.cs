using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace Rise.Services;

internal sealed class GameEntry(string name, string? steamId, params string[] executables)
{
    public string Name { get; } = name;
    public string? SteamId { get; } = steamId;
    public IReadOnlyList<string> Executables { get; } = executables;
    public string? InstallPath { get; set; }
    public string? ExecutablePath { get; set; }
    public string Status => ExecutablePath is null ? "Not detected" : "Installed";
}

internal sealed class GameCatalog
{
    public static IReadOnlyList<GameEntry> Games { get; } =
    [
        new("Fortnite", null, @"Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"),
        new("Valorant", null, @"Riot Games\VALORANT\live\VALORANT.exe", @"VALORANT\live\VALORANT.exe"),
        new("Counter-Strike 2", "730", @"Counter-Strike Global Offensive\game\bin\win64\cs2.exe"),
        new("Counter-Strike: Global Offensive (Legacy)", "730", @"Counter-Strike Global Offensive\csgo.exe"),
        new("Roblox", null, @"Roblox\Versions\RobloxPlayerBeta.exe"),
        new("Minecraft Java Edition", null, @"Minecraft Launcher\MinecraftLauncher.exe", @"PrismLauncher\prismlauncher.exe"),
        new("Minecraft Bedrock Edition", null, @"Minecraft Bedrock\Minecraft.Windows.exe", @"WindowsApps\Microsoft.MinecraftUWP_8wekyb3d8bbwe\Minecraft.Windows.exe"),
        new("Cyberpunk 2077", "1091500", @"Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"),
        new("Grand Theft Auto V", "271590", @"Grand Theft Auto V\GTA5.exe"),
        new("Grand Theft Auto V Enhanced", "3240220", @"Grand Theft Auto V Enhanced\GTA5_Enhanced.exe"),
        new("Red Dead Redemption 2", "1174180", @"Red Dead Redemption 2\RDR2.exe"),
        new("Apex Legends", "1172470", @"Apex Legends\r5apex.exe"),
        new("Call of Duty: Warzone", null, @"Call of Duty\_retail_\cod.exe"),
        new("Call of Duty: Black Ops 6", null, @"Call of Duty\_retail_\cod.exe"),
        new("Overwatch 2", null, @"Overwatch\_retail_\Overwatch.exe"),
        new("Rocket League", "252950", @"rocketleague\Binaries\Win64\RocketLeague.exe"),
        new("League of Legends", null, @"Riot Games\League of Legends\LeagueClient.exe"),
        new("Dota 2", "570", @"dota 2 beta\game\bin\win64\dota2.exe"),
        new("Rainbow Six Siege", "359550", @"Tom Clancy's Rainbow Six Siege\RainbowSix.exe", @"Tom Clancy's Rainbow Six Siege\RainbowSix_Vulkan.exe"),
        new("PUBG: Battlegrounds", "578080", @"PUBG\TslGame\Binaries\Win64\TslGame.exe"),
        new("Forza Horizon 5", "1551360", @"ForzaHorizon5\ForzaHorizon5.exe"),
        new("Elden Ring", "1245620", @"ELDEN RING\Game\eldenring.exe"),
        new("Marvel Rivals", "2767030", @"MarvelGame\Marvel.exe"),
        new("The Finals", "2073850", @"Discovery\Binaries\Win64\Discovery.exe")
    ];

    public void Detect()
    {
        var roots = GetSearchRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var game in Games)
        {
            game.ExecutablePath = null;
            game.InstallPath = null;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var relativePath in game.Executables)
                {
                    var candidate = Path.Combine(root, relativePath);
                    if (!File.Exists(candidate)) continue;
                    game.ExecutablePath = candidate;
                    game.InstallPath = Path.GetDirectoryName(candidate);
                    break;
                }
                if (game.ExecutablePath is not null) break;

                if (game.Name == "Roblox")
                {
                    var versionsPath = Path.Combine(root, "Roblox", "Versions");
                    try
                    {
                        foreach (var versionPath in Directory.EnumerateDirectories(versionsPath))
                        {
                            var player = Path.Combine(versionPath, "RobloxPlayerBeta.exe");
                            if (!File.Exists(player)) continue;
                            game.ExecutablePath = player;
                            game.InstallPath = versionPath;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    if (game.ExecutablePath is not null) break;
                }
            }
        }
    }

    public static void SetExecutable(GameEntry game, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Choose an existing game executable.", path);
        game.ExecutablePath = path;
        game.InstallPath = Path.GetDirectoryName(path);
    }

    public static Process Launch(GameEntry game)
    {
        if (game.ExecutablePath is null || !File.Exists(game.ExecutablePath))
            throw new InvalidOperationException($"Locate the {game.Name} executable before launching it.");

        var existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.ExecutablePath)).FirstOrDefault();
        if (existing is not null)
        {
            existing.Dispose();
            throw new InvalidOperationException($"{game.Name} is already running.");
        }

        var process = Process.Start(new ProcessStartInfo(game.ExecutablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath)!,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException($"Windows could not start {game.Name}.");
        return process;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rockstar Games");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Rockstar Games");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Xbox Games");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Overwatch");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return @"C:\Riot Games";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Call of Duty");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Call of Duty");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Overwatch");
        foreach (var steamRoot in GetSteamRoots())
            yield return Path.Combine(steamRoot, "steamapps", "common");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            yield return Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common");
            yield return Path.Combine(drive.RootDirectory.FullName, "Games");
            yield return Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam", "steamapps", "common");
            yield return Path.Combine(drive.RootDirectory.FullName, "Program Files", "Steam", "steamapps", "common");
            yield return Path.Combine(drive.RootDirectory.FullName, "XboxGames");
        }
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        var installPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
            ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrWhiteSpace(installPath)) yield break;

        yield return installPath.Replace('/', Path.DirectorySeparatorChar);
        var librariesFile = Path.Combine(installPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(librariesFile)) yield break;
        var contents = ReadLibraryFile(librariesFile);
        if (contents is null) yield break;

        foreach (Match match in Regex.Matches(contents, "\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var library = match.Groups["path"].Value.Replace(@"\\", @"\").Replace('/', Path.DirectorySeparatorChar);
            if (Directory.Exists(library)) yield return library;
        }
    }

    private static string? ReadLibraryFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
    }
}
