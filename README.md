# WinSlim 轻装

给原生 Windows 10 / 11 减负的优化工具。每一项优化都会告诉你**这是什么、有什么效果、要付出什么代价**，应用前先备份原值，随时可以一键还原。

WinSlim 是在“Windows11轻松设置 v1.07”的基础上重新编写的（对原版做过逆向分析）。它保留了“去广告、去推广、去臃肿”这部分思路，去掉了会削弱系统安全或者带来供应链风险的做法。

## 功能

| 页面 | 作用 |
|---|---|
| 概览 | 减负账本：每个分类做了多少、还剩多少；这台电脑的系统、内存和磁盘情况；可清理的空间 |
| 优化项 | 40 多项优化，分为隐私与遥测、广告与推广、Copilot 与 AI、任务栏与开始菜单、资源管理器、性能与后台、Windows 更新、安全取舍 8 类 |
| 预装应用 | 卸载资讯、纸牌、Clipchamp 等预装应用，可以同时阻止新用户自动安装；卸错了可以从微软商店重新安装 |
| 磁盘清理 | 临时文件、更新缓存、传递优化缓存、错误报告、缩略图、回收站、WinSxS 组件存储 |
| 系统防护 | 防火墙开关与规则、Windows Defender 实时保护开关。全部通过微软官方命令（Set-NetFirewallProfile、Set-MpPreference 等）实现，带醒目高风险提示，可一键还原 |
| 改动记录 | 查看 WinSlim 做过的所有改动，逐项或全部还原；创建系统还原点；查看日志 |
| 关于与声明 | 免责声明、非商业使用许可、作者与项目链接 |

> 关于“系统防护”：关闭防火墙或杀毒会直接降低系统安全性，仅建议在清楚风险时使用。WinSlim **不会**绕过 Windows 的“篡改防护”，关闭 Defender 需要你在 Windows 安全中心亲自确认——这既更安全，也不把工具做成对抗杀软的性质。

### 每一项都有三段说明

- **这是什么**：这项设置本身是干什么的
- **效果**：改了之后会怎样
- **后果 / 代价**：会失去什么功能，什么情况下不该改

每项还标了风险等级（安全 / 需留意 / 高风险），以及生效方式（立即生效、需要重启资源管理器、注销或重启电脑）。展开一项就能看到它具体会写入哪些注册表值、修改哪个服务、运行哪条命令。

### 先检查，再应用

打开开关只是把这一项加入“待处理”。点击“检查改动并应用”后，会列出全部改动和高风险项的后果，确认之后才会真正写入。默认会先创建一个系统还原点。

### 怎么还原

第一次应用某一项时，WinSlim 会把它要改的每个注册表值和服务启动类型的原样记录到 `%ProgramData%\WinSlim\backup.json`。还原时写回这些原值，而不是笼统地套用“默认值”。所有操作都会记录在 `%ProgramData%\WinSlim\winslim.log`。

## 和原版相比，有意没有保留的功能

| 原版功能 | 为什么去掉 |
|---|---|
| 以 TrustedInstaller 身份关闭 Defender、关闭 DEP、关闭 UAC、关闭智能应用控制 | 属于削弱安全，不是减负，而且容易让系统处于半残状态 |
| 从作者的私人服务器通过 HTTP 下载 40 个软件，并以管理员身份运行 | 没有校验，存在中间人攻击和供应链风险。WinSlim 只使用微软商店和 winget |
| 强制删除 Edge 和 WebView2 | 会破坏小组件、多个商店应用和 WinSlim 自己的界面 |
| 把更新暂停到 2042 年 | 改为只放宽暂停上限，暂停多久由你自己在“设置”里决定 |
| 修改远程桌面端口、清空系统事件日志 | 和“减负”无关，而且清空日志会妨碍排查问题 |

## 系统要求

- Windows 10 1809 及以上，或 Windows 11，64 位
- Microsoft Edge WebView2 运行时（Windows 11 自带；Windows 10 一般会随 Edge 自动安装。缺少时程序会给出微软官方下载链接）
- 管理员权限

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
dotnet publish src/WinSlim/WinSlim.csproj -c Release -o dist
```

会生成单文件 `dist/WinSlim.exe`（自带 .NET 运行时，不需要另外安装）。

### 只预览界面

不需要 .NET，也不需要管理员权限，界面会使用模拟数据：

```bash
node tools/dev-server.mjs
```

然后打开 http://localhost:5317 。

## 项目结构

```
src/WinSlim/
  Program.cs              入口：检查系统版本和管理员权限，单实例运行
  MainForm.cs             WebView2 窗口；从程序内嵌资源加载界面，外部链接一律交给系统浏览器
  Bridge.cs               界面与后台之间的 JSON-RPC
  Core/
    TweakEngine.cs        按目录声明检测、应用、还原优化项，并负责备份原值
    AppxService.cs        通过 PowerShell Appx 模块列出、卸载和重装应用
    CleanupService.cs     扫描和清理缓存
    SystemInfo.cs         系统信息、创建还原点、重启资源管理器
  wwwroot/
    catalog/tweaks.json   全部优化项：说明、效果、后果、风险等级和具体改动
    catalog/protection.json   系统防护项（防火墙 / Defender）
    catalog/apps.json     预装应用目录
    catalog/maintenance.json  清理项
    index.html, app.css, app.js  界面
  dev/mock.js             浏览器预览用的模拟后台（不会打包进程序）
tools/dev-server.mjs      界面预览服务器
```

### 添加一项优化

在 `src/WinSlim/wwwroot/catalog/tweaks.json` 里加一条即可，不需要改代码：

```json
{
  "id": "example",
  "category": "privacy",
  "title": "标题",
  "desc": "这是什么",
  "effect": "效果",
  "risk": "后果或代价",
  "level": "safe",
  "os": "all",
  "restart": "none",
  "recommended": false,
  "actions": [
    { "type": "reg", "path": "HKCU\\Software\\...", "name": "ValueName", "kind": "dword", "value": 0 }
  ]
}
```

`actions` 支持三种类型：

- `reg`：可以加 `revertValue`（没有备份时还原成这个值）或 `revertDeleteKey`（还原时删除整个键）
- `service`：需要 `startType` 和 `revertStartType`
- `cmd`：需要 `apply` 和 `revert`，并且要配一条 `detect` 规则，用来判断当前状态。
  加 `"shell": "powershell"` 可用 PowerShell 执行（系统防护项就是这样调用官方 cmdlet 的）

## 作者

- FrankHuahua
- 邮箱：wxl995232151@gmail.com
- GitHub：https://github.com/FrankHuahua

## 许可

[非商业使用许可](LICENSE)：源代码可查看、可个人免费使用和修改、可非商业分发，**禁止任何商业用途**（需商用请联系作者）。项目在功能完善后计划正式开源。
