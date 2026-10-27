using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WinSlim.Core;

namespace WinSlim;

public sealed class MainForm : Form
{
    const string Origin = "https://app.winslim";
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    Bridge? _bridge;

    public MainForm()
    {
        Text = "WinSlim 轻装";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(960, 640);
        var dark = IsDarkTheme();
        BackColor = dark ? Color.FromArgb(0x16, 0x18, 0x17) : Color.FromArgb(0xF3, 0xF1, 0xEC);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        HandleCreated += (_, _) => UseDarkTitleBar(dark);
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSlim", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var go = MessageBox.Show(this,
                "WinSlim 的界面需要 Microsoft Edge WebView2 运行时，但这台电脑上没有找到它。\n\n点击“确定”打开微软官方下载页面，安装后重新启动 WinSlim。",
                "缺少 WebView2", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (go == DialogResult.OK) Shell.OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;

        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var path = new Uri(e.Request.Uri).AbsolutePath;
            if (path == "/") path = "/index.html";
            var stream = Resources.Open(path);
            e.Response = stream == null
                ? core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                : core.Environment.CreateWebResourceResponse(stream, 200, "OK",
                    $"Content-Type: {Resources.ContentType(path)}\r\nCache-Control: no-store");
        };
        // 只允许本地界面，任何外部链接都交给系统浏览器打开。
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin)) { e.Cancel = true; Shell.OpenUrl(e.Uri); }
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; Shell.OpenUrl(e.Uri); };

        _bridge = new Bridge(json => BeginInvoke(() => core.PostWebMessageAsJson(json)));
        core.WebMessageReceived += (_, e) =>
        {
            var message = e.WebMessageAsJson;
            _ = Task.Run(() => _bridge.Handle(message));
        };

        core.Navigate(Origin + "/index.html");
    }

    static bool IsDarkTheme()
    {
        var (exists, value, _) = Reg.Read(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme");
        return exists && value is int v && v == 0;
    }

    void UseDarkTitleBar(bool dark)
    {
        int on = dark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20（Windows 10 20H1 及以上）
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
