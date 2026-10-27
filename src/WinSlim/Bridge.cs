using System.Reflection;
using System.Text.Json;
using WinSlim.Core;

namespace WinSlim;

/// <summary>
/// 界面与后台之间的 JSON-RPC。界面发送 {id, method, params}，后台回复 {id, ok, result | error}。
/// 后台也会主动推送 {event, data}，用来报告长任务的进度。
/// </summary>
public sealed class Bridge
{
    readonly Action<string> _post;
    readonly OsInfo _os = SystemInfo.ReadOs();
    readonly TweakEngine _engine;
    readonly AppCatalog _apps;
    readonly MaintenanceCatalog _maintenance;

    public Bridge(Action<string> post)
    {
        _post = post;
        _engine = new TweakEngine(Load<TweakCatalog>("catalog/tweaks.json"));
        _apps = Load<AppCatalog>("catalog/apps.json");
        _maintenance = Load<MaintenanceCatalog>("catalog/maintenance.json");
    }

    static T Load<T>(string resource)
    {
        using var s = Resources.Open(resource) ?? throw new FileNotFoundException(resource);
        return JsonSerializer.Deserialize<T>(s, Json.Options)!;
    }

    public async Task Handle(string message)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            id = root.GetProperty("id").ToString();
            var method = root.GetProperty("method").GetString()!;
            var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
            var result = await Dispatch(method, p);
            Reply(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            Log.Write("RPC 出错：" + ex);
            Reply(new { id, ok = false, error = ex.Message });
        }
    }

    void Reply(object payload) => _post(JsonSerializer.Serialize(payload, Json.Options));

    void Emit(string evt, object data) => Reply(new { @event = evt, data });

    static List<string> Ids(JsonElement p, string name = "ids") =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var a)
            ? a.EnumerateArray().Select(x => x.GetString()!).ToList()
            : new List<string>();

    static bool Flag(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    Task<object?> Dispatch(string method, JsonElement p) => method switch
    {
        "system" => Wrap(() => SystemInfo.Read()),
        "catalog" => Wrap(Catalog),
        "status" => Status(Ids(p)),
        "apply" => Apply(Ids(p), Flag(p, "restorePoint")),
        "revert" => Revert(Ids(p)),
        "restartExplorer" => Run(SystemInfo.RestartExplorer),
        "apps" => Apps(),
        "removeApps" => RemoveApps(Ids(p, "packages"), Flag(p, "deprovision")),
        "reinstallApp" => ReinstallApp(p.GetProperty("package").GetString()!),
        "scanCleanup" => Box(CleanupService.Scan(Ids(p))),
        "runCleanup" => RunCleanup(Ids(p)),
        "restorePoint" => RestorePoint(),
        "openUrl" => Run(() => { Shell.OpenUrl(p.GetProperty("url").GetString()!); return Task.CompletedTask; }),
        "openLogFolder" => Run(() => { Directory.CreateDirectory(Log.Dir); System.Diagnostics.Process.Start("explorer.exe", Log.Dir); return Task.CompletedTask; }),
        "openSystemRestore" => Run(() => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rstrui.exe") { UseShellExecute = true }); return Task.CompletedTask; }),
        _ => throw new NotSupportedException("未知的方法：" + method),
    };

    static Task<object?> Wrap(Func<object> f) => Task.FromResult<object?>(f());

    static async Task<object?> Box<T>(Task<T> t) => await t;

    static async Task<object?> Run(Func<Task> f) { await f(); return null; }

    object Catalog() => new
    {
        os = new { label = _os.Label, isWindows11 = _os.IsWindows11, build = _os.Build },
        version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
        categories = _engine.Catalog.Categories,
        tweaks = _engine.Catalog.Tweaks.Where(t => t.AppliesTo(_os)).Select(t => new
        {
            t.Id, t.Category, t.Title, t.Desc, t.Effect, t.Risk, t.Level, t.Restart, t.Recommended, t.Os,
            changes = t.Actions.Select(Describe),
        }),
        appGroups = _apps.Groups,
        apps = _apps.Apps,
        cleaners = _maintenance.Cleaners,
    };

    /// <summary>给界面“改动内容”一栏用的一行说明。</summary>
    static string Describe(TweakAction a) => a.Type switch
    {
        "reg" => $"{a.Path} › {(string.IsNullOrEmpty(a.Name) ? "(默认)" : a.Name)} = {(a.Value?.ValueKind == JsonValueKind.String ? "\"" + a.Value?.GetString() + "\"" : a.Value?.ToString())}",
        "service" => $"服务 {a.Name} 启动类型 → {StartTypeName(a.StartType)}",
        "cmd" => $"运行 {a.Apply}",
        _ => a.Type,
    };

    static string StartTypeName(string? t) => t switch
    {
        "auto" => "自动",
        "delayed-auto" => "自动（延迟启动）",
        "manual" => "手动",
        "disabled" => "禁用",
        _ => t ?? "",
    };

    async Task<object?> Status(List<string> ids)
    {
        var tweaks = (ids.Count == 0 ? _engine.Catalog.Tweaks.Where(t => t.AppliesTo(_os)) : ids.Select(_engine.Find)).ToList();
        // 注册表检测很快；DISM 之类的命令检测慢，放到线程池里并行执行。
        var results = await Task.WhenAll(tweaks.Select(t => Task.Run(() => _engine.Detect(t))));
        return results;
    }

    async Task<object?> Apply(List<string> ids, bool restorePoint)
    {
        string? restoreNote = null;
        if (restorePoint)
        {
            Emit("progress", new { stage = "restore", text = "正在创建系统还原点……" });
            var (ok, msg) = await SystemInfo.CreateRestorePoint("WinSlim 优化前");
            restoreNote = ok ? null : msg;
        }
        var outcomes = new List<TweakOutcome>();
        var tweaks = ids.Select(_engine.Find).ToList();
        foreach (var (t, i) in tweaks.Select((t, i) => (t, i)))
        {
            Emit("progress", new { stage = "apply", text = $"正在应用：{t.Title}", done = i, total = tweaks.Count });
            outcomes.Add(await _engine.Apply(t));
        }
        var statuses = await Task.WhenAll(tweaks.Select(t => _engine.Detect(t)));
        return new { outcomes, statuses, restoreNote, restart = Strongest(tweaks.Where((t, i) => outcomes[i].Ok)) };
    }

    async Task<object?> Revert(List<string> ids)
    {
        var tweaks = ids.Select(_engine.Find).ToList();
        var outcomes = new List<TweakOutcome>();
        foreach (var t in tweaks) outcomes.Add(await _engine.Revert(t));
        var statuses = await Task.WhenAll(tweaks.Select(t => _engine.Detect(t)));
        return new { outcomes, statuses, restart = Strongest(tweaks.Where((t, i) => outcomes[i].Ok)) };
    }

    /// <summary>一批改动里要求最高的那种生效方式。</summary>
    static string Strongest(IEnumerable<Tweak> tweaks)
    {
        string[] order = { "none", "explorer", "logoff", "reboot" };
        return tweaks.Select(t => t.Restart).DefaultIfEmpty("none").MaxBy(r => Array.IndexOf(order, r))!;
    }

    async Task<object?> Apps() => await AppxService.List(_apps.Apps.Select(a => a.Package));

    async Task<object?> RemoveApps(List<string> packages, bool deprovision)
    {
        var outcomes = new List<AppOutcome>();
        foreach (var (pkg, i) in packages.Select((p, i) => (p, i)))
        {
            var name = _apps.Apps.FirstOrDefault(a => a.Package == pkg)?.Name ?? pkg;
            Emit("progress", new { stage = "apps", text = $"正在卸载：{name}", done = i, total = packages.Count });
            outcomes.Add(await AppxService.Remove(pkg, deprovision));
        }
        return new { outcomes, apps = await AppxService.List(_apps.Apps.Select(a => a.Package)) };
    }

    async Task<object?> ReinstallApp(string package)
    {
        var app = _apps.Apps.First(a => a.Package == package);
        Emit("progress", new { stage = "apps", text = $"正在从微软商店重新安装：{app.Name}" });
        var outcome = await AppxService.Reinstall(app);
        return new { outcome, apps = await AppxService.List(_apps.Apps.Select(a => a.Package)) };
    }

    async Task<object?> RunCleanup(List<string> ids)
    {
        var outcomes = new List<CleanupOutcome>();
        foreach (var (id, i) in ids.Select((x, i) => (x, i)))
        {
            var title = _maintenance.Cleaners.FirstOrDefault(c => c.Id == id)?.Title ?? id;
            Emit("progress", new { stage = "cleanup", text = $"正在清理：{title}", done = i, total = ids.Count });
            outcomes.Add(await CleanupService.Run(id));
        }
        return new { outcomes, scan = await CleanupService.Scan(_maintenance.Cleaners.Select(c => c.Id)) };
    }

    async Task<object?> RestorePoint()
    {
        Emit("progress", new { stage = "restore", text = "正在创建系统还原点……" });
        var (ok, message) = await SystemInfo.CreateRestorePoint("WinSlim 手动还原点");
        return new { ok, message };
    }
}

/// <summary>读取嵌入在程序里的界面文件。</summary>
public static class Resources
{
    static readonly Assembly Asm = typeof(Resources).Assembly;
    static readonly Dictionary<string, string> Names = Asm.GetManifestResourceNames()
        .Where(n => n.StartsWith("wwwroot"))
        .ToDictionary(n => n.Replace('\\', '/')["wwwroot/".Length..], n => n, StringComparer.OrdinalIgnoreCase);

    public static Stream? Open(string path) =>
        Names.TryGetValue(path.TrimStart('/'), out var name) ? Asm.GetManifestResourceStream(name) : null;

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
}
