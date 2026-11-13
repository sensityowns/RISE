using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace Rise.Services;

internal sealed record SystemSnapshot(
    string Windows,
    string Build,
    string Cpu,
    string Gpu,
    string Architecture,
    string FormFactor,
    string Battery,
    string PowerPlan,
    string Activation,
    double RamUsedPercent,
    string RamUsed,
    string RamTotal,
    string DiskFree,
    string DiskTotal,
    double CpuPercent,
    string Network,
    string Uptime);

internal static class SystemSnapshotService
{
    private static ulong _previousIdle;
    private static ulong _previousKernel;
    private static ulong _previousUser;
    private static bool _hasCpuSample;

    public static SystemSnapshot Read()
    {
        using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = version?.GetValue("ProductName")?.ToString() ?? "Windows";
        var displayVersion = version?.GetValue("DisplayVersion")?.ToString();
        var build = version?.GetValue("CurrentBuildNumber")?.ToString() ?? "Unknown";
        var windows = string.IsNullOrWhiteSpace(displayVersion) ? product : $"{product} {displayVersion}";
        using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var cpu = Normalize(cpuKey?.GetValue("ProcessorNameString")?.ToString()) ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown processor";
        var gpu = ReadGpu();

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var memoryAvailable = GlobalMemoryStatusEx(ref memory);
        var total = memoryAvailable ? memory.TotalPhysical : 0;
        var available = memoryAvailable ? memory.AvailablePhysical : 0;
        var used = total > available ? total - available : 0;
        var usedPercent = total > 0 ? Math.Round(100d * used / total) : 0;

        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var diskAvailable = GetDiskFreeSpaceEx(root, out var free, out var capacity, out _);
        var hasPowerStatus = GetSystemPowerStatus(out var power);
        var powerPlan = ReadPowerPlan();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var connection = NetworkInterface.GetIsNetworkAvailable() ? "Connected" : "No active connection";
        var formFactor = !hasPowerStatus ? "Unavailable"
            : power.BatteryFlag == 128 ? "Desktop / no battery"
            : power.BatteryFlag == 255 ? "Unknown"
            : "Portable device";

        return new SystemSnapshot(
            windows,
            build,
            cpu,
            gpu,
            Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
            formFactor,
            !hasPowerStatus ? "Unavailable" : power.BatteryFlag == 128 ? "No battery detected" : power.BatteryFlag == 255 ? "Battery status unavailable" : $"{power.BatteryLifePercent}% remaining",
            powerPlan,
            "Not checked (requires elevated Windows validation)",
            usedPercent,
            FormatBytes(used),
            FormatBytes(total),
            diskAvailable ? FormatBytes(free) : "Unavailable",
            diskAvailable ? FormatBytes(capacity) : "Unavailable",
            ReadCpuPercent(),
            connection,
            $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m");
    }

    private static double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var idleNow = ToUInt64(idle);
        var kernelNow = ToUInt64(kernel);
        var userNow = ToUInt64(user);
        if (!_hasCpuSample)
        {
            _previousIdle = idleNow;
            _previousKernel = kernelNow;
            _previousUser = userNow;
            _hasCpuSample = true;
            return 0;
        }

        var idleDelta = idleNow - _previousIdle;
        var totalDelta = (kernelNow - _previousKernel) + (userNow - _previousUser);
        _previousIdle = idleNow;
        _previousKernel = kernelNow;
        _previousUser = userNow;
        return totalDelta == 0 ? 0 : Math.Round(100d * (totalDelta - idleDelta) / totalDelta);
    }

    private static string ReadGpu()
    {
        try
        {
            using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (video is null) return "Not reported";
            foreach (var name in video.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var adapter = video.OpenSubKey(name);
                var description = adapter?.GetValue("DriverDesc")?.ToString();
                if (!string.IsNullOrWhiteSpace(description) && !description.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                    return description;
            }
        }
        catch (UnauthorizedAccessException) { }
        return "Not reported by the display driver";
    }

    private static string ReadPowerPlan()
    {
        try
        {
            var start = new ProcessStartInfo("powercfg.exe", "/getactivescheme")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process is null) return "Unavailable";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0 ? output[(output.LastIndexOf(':') + 1)..].Trim() : "Unavailable";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return "Unavailable";
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string root, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
