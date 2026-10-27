using System.Text.Json;

namespace WinSlim.Core;

public sealed record InstalledApp(string Package, string Version, bool Installed, bool Provisioned);

public sealed record AppOutcome(string Package, bool Ok, string? Error);

/// <summary>通过 PowerShell 的 Appx 模块查询、卸载和重装商店应用。</summary>
public static class AppxService
{
    public static async Task<List<InstalledApp>> List(IEnumerable<string> packages)
    {
        var wanted = packages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = await Query("Get-AppxPackage | Select-Object Name,Version | ConvertTo-Json -Compress", "Name");
        var provisioned = await Query("Get-AppxProvisionedPackage -Online | Select-Object DisplayName,Version | ConvertTo-Json -Compress", "DisplayName");
        return wanted
            .Select(p => new InstalledApp(
                p,
                installed.GetValueOrDefault(p) ?? provisioned.GetValueOrDefault(p) ?? "",
                installed.ContainsKey(p),
                provisioned.ContainsKey(p)))
            .ToList();
    }

    static async Task<Dictionary<string, string>> Query(string script, string nameField)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var r = await Shell.PowerShell(script, TimeSpan.FromMinutes(2));
        if (!r.Ok || string.IsNullOrWhiteSpace(r.Output)) return map;
        try
        {
            using var doc = JsonDocument.Parse(r.Output);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            foreach (var item in items)
            {
                var name = item.GetProperty(nameField).GetString();
                var version = item.TryGetProperty("Version", out var v) ? v.GetString() ?? v.ToString() : "";
                if (name != null) map[name] = version;
            }
        }
        catch (JsonException) { }
        return map;
    }

    /// <param name="deprovision">同时从系统映像里移除，阻止新用户登录时再装上。</param>
    public static async Task<AppOutcome> Remove(string package, bool deprovision)
    {
        var name = Quote(package);
        var script = $"$ErrorActionPreference='Stop';Get-AppxPackage -Name {name} | Remove-AppxPackage;";
        if (deprovision)
            script += $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq {name} | Remove-AppxProvisionedPackage -Online | Out-Null;";
        var r = await Shell.PowerShell(script, TimeSpan.FromMinutes(3));
        Log.Write($"卸载应用 {package}（移除预配={deprovision}）=> {r.ExitCode}");
        return new AppOutcome(package, r.Ok, r.Ok ? null : Summarize(r.Output));
    }

    /// <summary>用 winget 从微软商店重装。没有 winget 时打开商店页面。</summary>
    public static async Task<AppOutcome> Reinstall(AppEntry app)
    {
        if (string.IsNullOrEmpty(app.Store))
            return new AppOutcome(app.Package, false, "这个应用不在商店上架，无法自动重装。");

        var winget = await Shell.Run("winget.exe",
            $"install --id {app.Store} -s msstore -e --accept-package-agreements --accept-source-agreements --disable-interactivity",
            System.Text.Encoding.UTF8, TimeSpan.FromMinutes(10));
        if (winget.Ok) return new AppOutcome(app.Package, true, null);

        Shell.OpenUrl($"ms-windows-store://pdp/?productid={app.Store}");
        return new AppOutcome(app.Package, false, "winget 安装失败，已为你打开商店页面，请在商店里点“获取”。");
    }

    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    static string Summarize(string output)
    {
        if (output.Contains("0x80073CFA")) return "这是系统组件，Windows 不允许卸载。";
        if (output.Contains("0x80070005")) return "权限不足。";
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "卸载失败" : line;
    }
}
