using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinSlim.Core;

public sealed record SystemSnapshot(
    string Os,
    bool IsWindows11,
    int Build,
    string Cpu,
    long MemoryTotal,
    long MemoryUsed,
    string SystemDrive,
    long DiskTotal,
    long DiskFree,
    int ProcessCount,
    long UptimeSeconds,
    string Computer);

public static class SystemInfo
{
    const string CurrentVersion = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static OsInfo ReadOs()
    {
        string S(string name) => Reg.Read(CurrentVersion, name).Value?.ToString() ?? "";
        int.TryParse(S("CurrentBuildNumber"), out var build);
        int.TryParse(S("UBR"), out var ubr);
        var edition = S("EditionID") switch
        {
            "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" => "家庭版",
            "Professional" => "专业版",
            "ProfessionalWorkstation" => "专业工作站版",
            "Enterprise" => "企业版",
            "Education" => "教育版",
            "IoTEnterprise" or "IoTEnterpriseS" => "IoT 企业版",
            "EnterpriseS" => "企业版 LTSC",
            var e => e,
        };
        // 注册表里的 ProductName 在 Windows 11 上仍然写着 "Windows 10"，必须看内部版本号。
        return new OsInfo(S("ProductName"), edition, S("DisplayVersion"), build, ubr, build >= 22000);
    }

    public static SystemSnapshot Read()
    {
        var os = ReadOs();
        var cpu = Reg.Read(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Value?.ToString()?.Trim() ?? "";
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        return new SystemSnapshot(
            os.Label,
            os.IsWindows11,
            os.Build,
            cpu,
            (long)mem.ullTotalPhys,
            (long)(mem.ullTotalPhys - mem.ullAvailPhys),
            drive.Name.TrimEnd('\\'),
            drive.TotalSize,
            drive.AvailableFreeSpace,
            Process.GetProcesses().Length,
            Environment.TickCount64 / 1000,
            Environment.MachineName);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>创建系统还原点。系统保护未开启时直接失败，不会替用户开启。</summary>
    public static async Task<(bool Ok, string Message)> CreateRestorePoint(string description)
    {
        var script = """
            $k='HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $old=(Get-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
            try { Checkpoint-Computer -Description '__DESC__' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; 'OK' }
            catch { 'ERR:' + $_.Exception.Message }
            finally {
              if ($null -eq $old) { Remove-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
              else { Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value $old }
            }
            """.Replace("__DESC__", description.Replace("'", "''"));
        var r = await Shell.PowerShell(script, TimeSpan.FromMinutes(5));
        if (r.Output.Contains("OK")) return (true, "已创建还原点");
        return (false, "未能创建还原点。系统保护可能没有开启：可以在“创建还原点”设置页里为系统盘开启后再试。");
    }

    public static async Task RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
            try { p.Kill(); } catch { }
        await Task.Delay(1500);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }
}
