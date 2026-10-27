using System.Text.Json;
using Microsoft.Win32;

namespace WinSlim.Core;

/// <summary>应用某项优化之前，记下它要改动的每一处原值，还原时写回原样。</summary>
public sealed class BackupEntry
{
    /// <summary>reg | service</summary>
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string? Kind { get; set; }
    public string? Data { get; set; }
    public string? StartType { get; set; }
}

public sealed class BackupStore
{
    static readonly string FilePath = System.IO.Path.Combine(Log.Dir, "backup.json");
    readonly Dictionary<string, List<BackupEntry>> _entries;
    readonly object _gate = new();

    public BackupStore()
    {
        try
        {
            _entries = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<BackupEntry>>>(File.ReadAllText(FilePath), Json.Options) ?? new()
                : new();
        }
        catch { _entries = new(); }
    }

    public bool Has(string tweakId) { lock (_gate) return _entries.ContainsKey(tweakId); }

    public List<BackupEntry>? Get(string tweakId) { lock (_gate) return _entries.GetValueOrDefault(tweakId); }

    public void Put(string tweakId, List<BackupEntry> entries)
    {
        lock (_gate) { _entries[tweakId] = entries; Save(); }
    }

    public void Remove(string tweakId)
    {
        lock (_gate) { if (_entries.Remove(tweakId)) Save(); }
    }

    void Save()
    {
        Directory.CreateDirectory(Log.Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, Json.Options));
    }
}

public sealed record TweakStatus(string Id, string State, bool HasBackup);

public sealed record TweakOutcome(string Id, bool Ok, string? Error);

/// <summary>按目录里的声明检测、应用和还原优化项。</summary>
public sealed class TweakEngine
{
    readonly BackupStore _backup = new();

    public TweakCatalog Catalog { get; }

    public TweakEngine(TweakCatalog catalog) => Catalog = catalog;

    public Tweak Find(string id) => Catalog.Tweaks.FirstOrDefault(t => t.Id == id)
        ?? throw new KeyNotFoundException("没有这个优化项：" + id);

    /// <returns>applied（已优化）| partial（部分）| default（系统默认）</returns>
    public async Task<TweakStatus> Detect(Tweak tweak)
    {
        var checks = new List<bool>();
        if (tweak.Detect != null)
        {
            checks.Add(await Check(tweak.Detect));
        }
        else
        {
            foreach (var a in tweak.Actions)
            {
                switch (a.Type)
                {
                    case "reg":
                        var (exists, value, _) = Reg.Read(a.Path!, a.Name ?? "");
                        checks.Add(exists && a.Value is { } v && Reg.Matches(value, v));
                        break;
                    case "service":
                        checks.Add(Services.GetStartType(a.Name!) == a.StartType);
                        break;
                }
            }
        }
        var state = checks.Count == 0 ? "default"
            : checks.All(c => c) ? "applied"
            : checks.Any(c => c) ? "partial"
            : "default";
        return new TweakStatus(tweak.Id, state, _backup.Has(tweak.Id));
    }

    static async Task<bool> Check(DetectRule rule)
    {
        if (rule.Type == "reg")
        {
            var (exists, value, _) = Reg.Read(rule.Path!, rule.Name ?? "");
            return exists && rule.Value is { } v && Reg.Matches(value, v);
        }
        if (rule.Type == "cmd" && rule.Run != null)
        {
            var r = await Shell.Cmd(rule.Run, TimeSpan.FromSeconds(60));
            return r.Output.Contains(rule.Contains ?? "", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public async Task<TweakOutcome> Apply(Tweak tweak)
    {
        try
        {
            if (!_backup.Has(tweak.Id))
                _backup.Put(tweak.Id, Snapshot(tweak));

            foreach (var a in tweak.Actions)
            {
                switch (a.Type)
                {
                    case "reg":
                        var kind = Reg.KindOf(a.Kind);
                        Reg.Write(a.Path!, a.Name ?? "", Reg.ToRegValue(a.Value!.Value, kind), kind);
                        break;
                    case "service":
                        if (Services.Exists(a.Name!))
                            await Services.SetStartType(a.Name!, a.StartType!);
                        break;
                    case "cmd":
                        var r = await Shell.Cmd(a.Apply!, TimeSpan.FromMinutes(3));
                        if (!r.Ok) throw new InvalidOperationException(FirstLine(r.Output));
                        break;
                }
            }
            Log.Write($"应用 {tweak.Id}");
            return new TweakOutcome(tweak.Id, true, null);
        }
        catch (Exception ex)
        {
            Log.Write($"应用 {tweak.Id} 失败：{ex.Message}");
            return new TweakOutcome(tweak.Id, false, Friendly(ex));
        }
    }

    public async Task<TweakOutcome> Revert(Tweak tweak)
    {
        try
        {
            var saved = _backup.Get(tweak.Id);
            foreach (var a in tweak.Actions)
            {
                switch (a.Type)
                {
                    case "reg":
                        RevertReg(a, saved?.FirstOrDefault(b => b.Type == "reg" && b.Path == a.Path && b.Name == (a.Name ?? "")));
                        break;
                    case "service":
                        var start = saved?.FirstOrDefault(b => b.Type == "service" && b.Name == a.Name)?.StartType ?? a.RevertStartType;
                        if (start != null && Services.Exists(a.Name!))
                            await Services.SetStartType(a.Name!, start);
                        break;
                    case "cmd":
                        if (a.Revert != null)
                        {
                            var r = await Shell.Cmd(a.Revert, TimeSpan.FromMinutes(3));
                            if (!r.Ok) throw new InvalidOperationException(FirstLine(r.Output));
                        }
                        break;
                }
            }
            _backup.Remove(tweak.Id);
            Log.Write($"还原 {tweak.Id}");
            return new TweakOutcome(tweak.Id, true, null);
        }
        catch (Exception ex)
        {
            Log.Write($"还原 {tweak.Id} 失败：{ex.Message}");
            return new TweakOutcome(tweak.Id, false, Friendly(ex));
        }
    }

    static void RevertReg(TweakAction a, BackupEntry? saved)
    {
        if (saved != null)
        {
            if (saved.Existed && saved.Data != null)
            {
                var kind = Enum.Parse<RegistryValueKind>(saved.Kind ?? "DWord");
                object value = kind switch
                {
                    RegistryValueKind.DWord => unchecked((int)long.Parse(saved.Data)),
                    RegistryValueKind.QWord => long.Parse(saved.Data),
                    RegistryValueKind.MultiString => saved.Data.Split('\n'),
                    _ => saved.Data,
                };
                Reg.Write(a.Path!, a.Name ?? "", value, kind);
                return;
            }
            if (a.RevertDeleteKey != null && !saved.Existed)
            {
                Reg.DeleteKey(a.RevertDeleteKey);
                return;
            }
            Reg.DeleteValue(a.Path!, a.Name ?? "");
            return;
        }

        // 没有备份（例如本工具以外的方式改过）：按目录声明的默认状态还原。
        if (a.RevertDeleteKey != null) Reg.DeleteKey(a.RevertDeleteKey);
        else if (a.RevertValue is { } rv)
        {
            var kind = Reg.KindOf(a.Kind);
            Reg.Write(a.Path!, a.Name ?? "", Reg.ToRegValue(rv, kind), kind);
        }
        else Reg.DeleteValue(a.Path!, a.Name ?? "");
    }

    static List<BackupEntry> Snapshot(Tweak tweak)
    {
        var list = new List<BackupEntry>();
        foreach (var a in tweak.Actions)
        {
            if (a.Type == "reg")
            {
                var (exists, value, kind) = Reg.Read(a.Path!, a.Name ?? "");
                list.Add(new BackupEntry
                {
                    Type = "reg",
                    Path = a.Path!,
                    Name = a.Name ?? "",
                    Existed = exists,
                    Kind = exists ? kind.ToString() : null,
                    Data = value switch
                    {
                        null => null,
                        string[] multi => string.Join('\n', multi),
                        byte[] => null,
                        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                    },
                });
            }
            else if (a.Type == "service")
            {
                list.Add(new BackupEntry { Type = "service", Name = a.Name!, StartType = Services.GetStartType(a.Name!) });
            }
        }
        return list;
    }

    static string FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "命令执行失败";

    static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有写入权限。这一项可能被系统保护，或者被组织策略锁定了。",
        System.Security.SecurityException => "没有写入权限。这一项可能被系统保护，或者被组织策略锁定了。",
        _ => ex.Message,
    };
}
