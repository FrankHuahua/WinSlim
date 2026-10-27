using System.Security.Principal;
using WinSlim.Core;

namespace WinSlim;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--selftest")
            return SelfTest.Run(args[1]).GetAwaiter().GetResult();

        using var single = new Mutex(true, @"Global\WinSlim.SingleInstance", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();

        var os = SystemInfo.ReadOs();
        if (os.Build < 10240)
        {
            MessageBox.Show("WinSlim 只支持 Windows 10 和 Windows 11。", "不支持的系统", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("请右键选择“以管理员身份运行”。修改系统设置需要管理员权限。", "需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        Log.Write($"启动 {os.Label} build {os.Build}.{os.Ubr}");
        Application.Run(new MainForm());
        return 0;
    }
}

/// <summary>
/// 只读自检：加载全部目录，对每个优化项做一次检测，列出预装应用并扫描可清理空间，结果写成 JSON。
/// 不修改任何系统设置，不需要管理员权限，用于在开发机和 CI 上验证目录与检测逻辑。
/// </summary>
static class SelfTest
{
    public static async Task<int> Run(string outFile)
    {
        var report = new Dictionary<string, object?>();
        var errors = new List<string>();
        try
        {
            var bridge = new Bridge(_ => { });
            var os = SystemInfo.ReadOs();
            report["os"] = os;
            using var s = Resources.Open("catalog/tweaks.json")!;
            var catalog = System.Text.Json.JsonSerializer.Deserialize<TweakCatalog>(s, Json.Options)!;
            using var ps = Resources.Open("catalog/protection.json")!;
            var protection = System.Text.Json.JsonSerializer.Deserialize<TweakCatalog>(ps, Json.Options)!;
            catalog.Categories.AddRange(protection.Categories);
            catalog.Tweaks.AddRange(protection.Tweaks);
            var engine = new TweakEngine(catalog);
            var ids = new HashSet<string>();
            foreach (var t in catalog.Tweaks)
            {
                if (!ids.Add(t.Id)) errors.Add("重复的 id：" + t.Id);
                if (catalog.Categories.All(c => c.Id != t.Category)) errors.Add($"{t.Id} 的分类不存在：{t.Category}");
                if (t.Actions.Count == 0) errors.Add(t.Id + " 没有任何改动");
                if (t.Actions.Any(a => a.Type == "cmd") && t.Detect == null) errors.Add(t.Id + " 是命令类，但没有 detect 规则");
            }
            var statuses = new List<object>();
            foreach (var t in catalog.Tweaks.Where(t => t.AppliesTo(os)))
            {
                try { statuses.Add(await engine.Detect(t)); }
                catch (Exception ex) { errors.Add($"检测 {t.Id} 出错：{ex.Message}"); }
            }
            report["tweaks"] = statuses;
            using var a = Resources.Open("catalog/apps.json")!;
            var apps = System.Text.Json.JsonSerializer.Deserialize<AppCatalog>(a, Json.Options)!;
            report["apps"] = await AppxService.List(apps.Apps.Select(x => x.Package));
            using var m = Resources.Open("catalog/maintenance.json")!;
            var cleaners = System.Text.Json.JsonSerializer.Deserialize<MaintenanceCatalog>(m, Json.Options)!;
            report["cleanup"] = await CleanupService.Scan(cleaners.Cleaners.Select(c => c.Id));
            report["system"] = SystemInfo.Read();
        }
        catch (Exception ex)
        {
            errors.Add(ex.ToString());
        }
        report["errors"] = errors;
        File.WriteAllText(outFile, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(Json.Options) { WriteIndented = true }));
        return errors.Count == 0 ? 0 : 1;
    }
}
