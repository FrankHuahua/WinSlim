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
        var dataDir = Path.Combine(Path.GetTempPath(), "WinSlimSelfTest-" + Guid.NewGuid().ToString("N")[..8]);
        Log.UseDirectory(dataDir);
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
            report["roundtrip"] = await Roundtrip(engine, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.ToString());
        }
        finally
        {
            try { Reg.DeleteKey(TestKey); } catch { }
            try { Directory.Delete(dataDir, true); } catch { }
        }
        report["errors"] = errors;
        File.WriteAllText(outFile, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(Json.Options) { WriteIndented = true }));
        return errors.Count == 0 ? 0 : 1;
    }

    /// <summary>只在这个专用测试键下读写，不触碰任何真实设置。</summary>
    const string TestKey = @"HKCU\Software\WinSlimSelfTest";

    static System.Text.Json.JsonElement J(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>应用 → 检测 → 还原 → 核对原值的完整往返，外加失败路径和“执行了但没生效”路径。</summary>
    static async Task<List<string>> Roundtrip(TweakEngine engine, List<string> errors)
    {
        var steps = new List<string>();
        void Expect(bool ok, string what) { steps.Add((ok ? "通过 " : "失败 ") + what); if (!ok) errors.Add("往返测试：" + what); }

        Reg.DeleteKey(TestKey);
        Reg.Write(TestKey, "Existing", 5, Microsoft.Win32.RegistryValueKind.DWord);

        var tweak = new Tweak
        {
            Id = "selftest-roundtrip",
            Actions =
            {
                new TweakAction { Type = "reg", Path = TestKey, Name = "Existing", Kind = "dword", Value = J("0") },
                new TweakAction { Type = "reg", Path = TestKey, Name = "Created", Kind = "string", Value = J("\"hello\"") },
                new TweakAction { Type = "reg", Path = TestKey + @"\Sub", Name = "", Kind = "string", Value = J("\"\""), RevertDeleteKey = TestKey + @"\Sub" },
                new TweakAction { Type = "cmd", Shell = "powershell",
                    Apply = @"New-Item -Path 'HKCU:\Software\WinSlimSelfTest\Ps' -Force | Out-Null",
                    Revert = @"Remove-Item -Path 'HKCU:\Software\WinSlimSelfTest\Ps' -Force" },
            },
        };

        var applied = await engine.Apply(tweak);
        Expect(applied.Ok, $"应用成功（{applied.Error}）");
        Expect((await engine.Detect(tweak)).State == "applied", "应用后检测为“已优化”");
        Expect(Reg.Read(TestKey, "Existing").Value is int e0 && e0 == 0, "已有值被改成 0");
        Expect(Reg.Read(TestKey, "Created").Value as string == "hello", "新值已写入");
        Expect(Reg.KeyExists(TestKey + @"\Sub"), "新键已创建");
        Expect(Reg.KeyExists(TestKey + @"\Ps"), "PowerShell 动作已执行");
        Expect((await engine.Detect(tweak)).HasBackup, "已记录备份");

        var reverted = await engine.Revert(tweak);
        Expect(reverted.Ok, $"还原成功（{reverted.Error}）");
        Expect(Reg.Read(TestKey, "Existing").Value is int e1 && e1 == 5, "已有值还原成原来的 5");
        Expect(!Reg.Read(TestKey, "Created").Exists, "新写入的值已删除");
        Expect(!Reg.KeyExists(TestKey + @"\Sub"), "新建的键已删除");
        Expect(!Reg.KeyExists(TestKey + @"\Ps"), "PowerShell 还原已执行");
        Expect((await engine.Detect(tweak)).State == "default", "还原后检测为“系统默认”");
        Expect(!(await engine.Detect(tweak)).HasBackup, "备份记录已清除");

        // 命令失败：应报失败，且不留下假备份。
        var failing = new Tweak { Id = "selftest-fail", Actions = { new TweakAction { Type = "cmd", Shell = "powershell", Apply = "throw 'boom'", Revert = "$null" } } };
        var f = await engine.Apply(failing);
        Expect(!f.Ok, "失败的命令报告为失败");
        Expect(!(await engine.Detect(failing)).HasBackup, "失败后没有留下备份记录");

        // 命令没报错但没生效：应通过复核报失败，并给出提示。
        var silent = new Tweak
        {
            Id = "selftest-silent",
            FailHint = "没有生效",
            Actions = { new TweakAction { Type = "cmd", Shell = "powershell", Apply = "$null", Revert = "$null" } },
            Detect = new DetectRule { Type = "cmd", Shell = "powershell", Run = @"if(Test-Path 'HKCU:\Software\WinSlimSelfTest\Never'){'yes'}else{'no'}", Contains = "yes" },
        };
        var s = await engine.Apply(silent);
        Expect(!s.Ok && s.Error == "没有生效", "“执行了但没生效”被复核发现");
        Expect(!(await engine.Detect(silent)).HasBackup, "未生效时没有留下备份记录");

        return steps;
    }
}
