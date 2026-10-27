using System.Text.Json;
using Microsoft.Win32;

namespace WinSlim.Core;

/// <summary>按 "HKLM\..." 形式的路径读写注册表（始终使用 64 位视图）。</summary>
public static class Reg
{
    public static (RegistryKey Root, string Sub) Split(string path)
    {
        var i = path.IndexOf('\\');
        var hive = (i < 0 ? path : path[..i]).ToUpperInvariant();
        var sub = i < 0 ? "" : path[(i + 1)..];
        var h = hive switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => throw new ArgumentException("未知的注册表根键：" + hive),
        };
        return (RegistryKey.OpenBaseKey(h, RegistryView.Registry64), sub);
    }

    public static bool KeyExists(string path)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var k = root.OpenSubKey(sub))
            return k != null;
    }

    public static (bool Exists, object? Value, RegistryValueKind Kind) Read(string path, string name)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var k = root.OpenSubKey(sub))
        {
            if (k == null) return (false, null, RegistryValueKind.Unknown);
            var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v == null) return (false, null, RegistryValueKind.Unknown);
            return (true, v, k.GetValueKind(name));
        }
    }

    public static void Write(string path, string name, object value, RegistryValueKind kind)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var k = root.CreateSubKey(sub, true))
            k.SetValue(name, value, kind);
    }

    public static void DeleteValue(string path, string name)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var k = root.OpenSubKey(sub, true))
            k?.DeleteValue(name, false);
    }

    public static void DeleteKey(string path)
    {
        var (root, sub) = Split(path);
        using (root)
            root.DeleteSubKeyTree(sub, false);
    }

    public static RegistryValueKind KindOf(string? kind) => kind?.ToLowerInvariant() switch
    {
        "qword" => RegistryValueKind.QWord,
        "string" => RegistryValueKind.String,
        "expand" => RegistryValueKind.ExpandString,
        _ => RegistryValueKind.DWord,
    };

    /// <summary>把目录里的 JSON 值换成可以写进注册表的对象。</summary>
    public static object ToRegValue(JsonElement value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => value.ValueKind == JsonValueKind.String ? int.Parse(value.GetString()!) : value.GetInt32(),
        RegistryValueKind.QWord => value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!) : value.GetInt64(),
        _ => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString(),
    };

    public static bool Matches(object? actual, JsonElement expected)
    {
        if (actual == null) return false;
        return actual switch
        {
            int i => expected.ValueKind == JsonValueKind.Number && expected.GetInt64() == (uint)i || expected.ValueKind == JsonValueKind.Number && expected.GetInt64() == i,
            long l => expected.ValueKind == JsonValueKind.Number && expected.GetInt64() == l,
            string s => (expected.ValueKind == JsonValueKind.String ? expected.GetString() : expected.ToString()) == s,
            _ => false,
        };
    }
}
