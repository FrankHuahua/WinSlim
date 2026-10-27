using System.Runtime.InteropServices;

namespace WinSlim.Core;

public sealed record CleanupScan(string Id, long Bytes, bool Measurable);

public sealed record CleanupOutcome(string Id, bool Ok, long Freed, string? Error);

/// <summary>扫描并清理可以安全删除的缓存和临时文件。</summary>
public static class CleanupService
{
    static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    static string[] Folders(string id) => id switch
    {
        "user-temp" => new[] { Path.GetTempPath() },
        "windows-temp" => new[] { Path.Combine(Win, "Temp") },
        "update-cache" => new[] { Path.Combine(Win, @"SoftwareDistribution\Download") },
        "delivery-cache" => new[] { Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
        "error-reports" => new[]
        {
            Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
            Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
            Path.Combine(LocalAppData, @"CrashDumps"),
        },
        _ => Array.Empty<string>(),
    };

    public static Task<List<CleanupScan>> Scan(IEnumerable<string> ids) => Task.Run(() =>
        ids.Select(id => id switch
        {
            "thumbnails" => new CleanupScan(id, ThumbnailFiles().Sum(SizeOf), true),
            "recycle-bin" => new CleanupScan(id, RecycleBinSize(), true),
            "component-store" => new CleanupScan(id, 0, false),
            _ => new CleanupScan(id, Folders(id).Sum(DirSize), true),
        }).ToList());

    public static async Task<CleanupOutcome> Run(string id)
    {
        try
        {
            switch (id)
            {
                case "update-cache":
                {
                    var before = Folders(id).Sum(DirSize);
                    await Shell.Cmd("net stop wuauserv /y & net stop bits /y");
                    try { Folders(id).ToList().ForEach(EmptyDir); }
                    finally { await Shell.Cmd("net start bits & net start wuauserv"); }
                    return Done(id, before, Folders(id).Sum(DirSize));
                }
                case "delivery-cache":
                {
                    var before = Folders(id).Sum(DirSize);
                    await Shell.PowerShell("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue");
                    return Done(id, before, Folders(id).Sum(DirSize));
                }
                case "thumbnails":
                {
                    var files = ThumbnailFiles().ToList();
                    var before = files.Sum(SizeOf);
                    foreach (var f in files) TryDelete(f);
                    return Done(id, before, ThumbnailFiles().Sum(SizeOf));
                }
                case "recycle-bin":
                {
                    var before = RecycleBinSize();
                    // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
                    SHEmptyRecycleBin(IntPtr.Zero, null, 0x7);
                    return Done(id, before, RecycleBinSize());
                }
                case "component-store":
                {
                    var r = await Shell.Cmd("DISM.exe /Online /Cleanup-Image /StartComponentCleanup", TimeSpan.FromMinutes(60));
                    return new CleanupOutcome(id, r.Ok, 0, r.Ok ? null : "DISM 清理失败，可以稍后重试。");
                }
                default:
                {
                    var before = Folders(id).Sum(DirSize);
                    Folders(id).ToList().ForEach(EmptyDir);
                    return Done(id, before, Folders(id).Sum(DirSize));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"清理 {id} 失败：{ex.Message}");
            return new CleanupOutcome(id, false, 0, ex.Message);
        }
    }

    static CleanupOutcome Done(string id, long before, long after)
    {
        var freed = Math.Max(0, before - after);
        Log.Write($"清理 {id} 释放 {freed} 字节");
        return new CleanupOutcome(id, true, freed, null);
    }

    static IEnumerable<string> ThumbnailFiles()
    {
        var dir = Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        try { return Directory.EnumerateFiles(dir, "thumbcache_*.db").Concat(Directory.EnumerateFiles(dir, "iconcache_*.db")); }
        catch { return Array.Empty<string>(); }
    }

    static long SizeOf(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options))
                try { total += f.Length; } catch { }
        }
        catch { }
        return total;
    }

    /// <summary>删除目录里的内容，保留目录本身；被占用的文件跳过。</summary>
    static void EmptyDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var info = new DirectoryInfo(dir);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var f in info.EnumerateFiles("*", options)) TryDelete(f.FullName);
        foreach (var d in info.EnumerateDirectories("*", options))
        {
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) { try { d.Delete(); } catch { } continue; }
            EmptyDir(d.FullName);
            try { d.Delete(false); } catch { }
        }
    }

    static void TryDelete(string file)
    {
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }
}
