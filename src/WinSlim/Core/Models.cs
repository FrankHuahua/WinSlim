using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinSlim.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}

public sealed class TweakCatalog
{
    public List<Category> Categories { get; set; } = new();
    public List<Tweak> Tweaks { get; set; } = new();
}

public sealed class Category
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class Tweak
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Effect { get; set; } = "";
    public string Risk { get; set; } = "";
    /// <summary>safe | moderate | high</summary>
    public string Level { get; set; } = "safe";
    /// <summary>all | 10 | 11</summary>
    public string Os { get; set; } = "all";
    /// <summary>none | explorer | logoff | reboot</summary>
    public string Restart { get; set; } = "none";
    public bool Recommended { get; set; }
    public List<TweakAction> Actions { get; set; } = new();
    public DetectRule? Detect { get; set; }

    public bool AppliesTo(OsInfo os) => Os switch
    {
        "10" => !os.IsWindows11,
        "11" => os.IsWindows11,
        _ => true,
    };
}

public sealed class TweakAction
{
    /// <summary>reg | service | cmd</summary>
    public string Type { get; set; } = "";
    public string? Path { get; set; }
    public string? Name { get; set; }
    /// <summary>dword | qword | string | expand</summary>
    public string? Kind { get; set; }
    public JsonElement? Value { get; set; }
    public JsonElement? RevertValue { get; set; }
    public string? RevertDeleteKey { get; set; }
    public string? StartType { get; set; }
    public string? RevertStartType { get; set; }
    public string? Apply { get; set; }
    public string? Revert { get; set; }
    /// <summary>cmd 动作的执行环境：powershell 或默认 cmd。</summary>
    public string? Shell { get; set; }
}

public sealed class DetectRule
{
    /// <summary>reg | cmd</summary>
    public string Type { get; set; } = "";
    public string? Path { get; set; }
    public string? Name { get; set; }
    public JsonElement? Value { get; set; }
    public string? Run { get; set; }
    public string? Contains { get; set; }
    /// <summary>cmd 检测的执行环境：powershell 或默认 cmd。</summary>
    public string? Shell { get; set; }
}

public sealed class AppCatalog
{
    public List<Category> Groups { get; set; } = new();
    public List<AppEntry> Apps { get; set; } = new();
}

public sealed class AppEntry
{
    public string Package { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Store { get; set; } = "";
    public string Level { get; set; } = "safe";
    public string Note { get; set; } = "";
}

public sealed class MaintenanceCatalog
{
    public List<Cleaner> Cleaners { get; set; } = new();
}

public sealed class Cleaner
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Effect { get; set; } = "";
    public string Risk { get; set; } = "";
}

public sealed record OsInfo(string ProductName, string Edition, string DisplayVersion, int Build, int Ubr, bool IsWindows11)
{
    public string Label => $"Windows {(IsWindows11 ? "11" : "10")} {Edition} {DisplayVersion}".Trim();
}
