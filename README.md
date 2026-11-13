# RISE

RISE is a Windows 10/11 desktop utility built with .NET 8 and WPF. It provides live system information, a reversible per-user Windows settings library, game discovery and launching, modest process-priority profiles, and Windows-native network and maintenance tools.

## Build and run

Install the .NET 8 SDK on Windows, then run:

```powershell
dotnet build RISE.csproj -c Release
dotnet run --project RISE.csproj
```

To create a self-contained x64 Windows build:

```powershell
dotnet publish RISE.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Implemented

- Dark RISE-branded WPF interface with launch and page transitions.
- Live Windows version/build, CPU, GPU name when exposed by the display driver, memory, system-drive space, architecture, battery status, active power scheme, and network availability.
- Ten documented, reversible per-user registry tweaks. Original values are saved before changes; individual and bulk restore are available. No Windows services or security protections are disabled.
- Detection of 24 requested game profiles in common installation folders, with manual executable selection and launch. Steam library folders are read from the local Steam configuration.
- Light, Balanced, and Extreme launch profiles request Normal, AboveNormal, and High process priority respectively. No real-time priority, process termination, background-app cleanup, or power-plan change is part of a boost.
- A live view of the launched process's CPU time and working set. System-wide CPU use is shown separately.
- Existing Windows power-plan activation with an explicit confirmation and a session-only restore option.
- Active adapter/IP/DNS display, ICMP connectivity/loss/round-trip measurements to `1.1.1.1`, and a Windows DNS-cache flush command.
- Registered desktop-app inventory, Windows Settings links for app removal and startup management, conservative cleanup of old top-level files in the current user's temp folder, and a text system report.
- Persistent accent, launch-at-sign-in, animation, boost-profile, and manually located game settings.
- Official RISE website and Discord links. No passwords or fabricated Pro entitlements are stored.

## Current limitations

This is an initial working build, not the full product described in the original specification. Games are launched from their detected or manually selected executable; some titles may require their official launcher or anti-cheat flow and are not guaranteed to start directly. The process opened by a launcher may not be the game itself. Automatic game updates, GPU/FPS/frame-time/game-ping telemetry, DNS benchmarking or provider changes, per-game settings, custom RISE power plans, AppX removal, service/process managers, restore-point creation, backups of arbitrary settings, tray support, licensing, and developer diagnostics are not implemented. Windows exposes no generally reliable game-independent FPS or GPU-reading API, so those values are not estimated or fabricated. RISE does not promise performance gains.

User settings and registry backups are stored under `%LOCALAPPDATA%\RISE`. Review every change before applying it and use the restore controls to return saved settings.
