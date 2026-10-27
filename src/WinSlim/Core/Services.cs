namespace WinSlim.Core;

/// <summary>读写服务的启动类型。启动类型存在注册表里，修改则交给 sc.exe 完成。</summary>
public static class Services
{
    const string Root = @"HKLM\SYSTEM\CurrentControlSet\Services\";

    public static bool Exists(string name) => Reg.KeyExists(Root + name);

    /// <returns>auto | delayed-auto | manual | disabled | boot | system | null（服务不存在）</returns>
    public static string? GetStartType(string name)
    {
        var (exists, value, _) = Reg.Read(Root + name, "Start");
        if (!exists || value is not int start) return null;
        var (_, delayed, _) = Reg.Read(Root + name, "DelayedAutostart");
        return start switch
        {
            0 => "boot",
            1 => "system",
            2 => delayed is int d && d == 1 ? "delayed-auto" : "auto",
            3 => "manual",
            4 => "disabled",
            _ => null,
        };
    }

    public static async Task<ShellResult> SetStartType(string name, string startType)
    {
        var sc = startType switch
        {
            "manual" => "demand",
            "auto" or "delayed-auto" or "disabled" => startType,
            _ => throw new ArgumentException("不支持的启动类型：" + startType),
        };
        var result = await Shell.Run("sc.exe", $"config \"{name}\" start= {sc}");
        if (startType == "disabled")
            await Shell.Run("sc.exe", $"stop \"{name}\"");
        else if (startType is "auto" or "delayed-auto")
            await Shell.Run("sc.exe", $"start \"{name}\"");
        return result;
    }
}
