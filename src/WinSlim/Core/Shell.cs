using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WinSlim.Core;

public sealed record ShellResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>运行隐藏的外部进程并收集输出。</summary>
public static class Shell
{
    static Shell() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    static Encoding OemEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }
    }

    public static Task<ShellResult> Cmd(string command, TimeSpan? timeout = null) =>
        Run("cmd.exe", "/d /c " + command, OemEncoding, timeout);

    public static Task<ShellResult> PowerShell(string script, TimeSpan? timeout = null)
    {
        var full = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, Encoding.UTF8, timeout);
    }

    public static async Task<ShellResult> Run(string file, string args, Encoding? encoding = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? OemEncoding,
            StandardErrorEncoding = encoding ?? OemEncoding,
        };
        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new ShellResult(-1, sb + "\n[超时，已终止]");
        }
        Log.Write($"{file} {(file.StartsWith("powershell") ? "(script)" : args)} => {p.ExitCode}");
        return new ShellResult(p.ExitCode, sb.ToString().Trim());
    }

    static readonly string[] AllowedSchemes = { "https://", "ms-settings:", "ms-windows-store:", "windowsdefender:" };

    public static void OpenUrl(string url)
    {
        if (!AllowedSchemes.Any(url.StartsWith))
            return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

/// <summary>写到 %ProgramData%\WinSlim\winslim.log 的操作日志。</summary>
public static class Log
{
    public static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinSlim");
    static readonly string FilePath = System.IO.Path.Combine(Dir, "winslim.log");
    static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
