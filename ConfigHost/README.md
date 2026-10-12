# ConfigHost —— DSH 配置窗口

`DshConfigHost.exe` 是一个 C# WinForms + WebView2 程序：它开出配置窗口，窗口里显示
`wwwroot\` 的页面；页面负责全部界面，本程序负责**真正读写文件、管理开机自启、
重启守护进程**。

之所以要有它：用浏览器以 `file://` 打开的 HTML 页，读写磁盘上的配置文件必须弹授权框，
换台机器还要重新授权。本程序是原生进程，没有这些限制，所以配置过程**零授权、零弹窗、
不需要浏览器、不需要联网、不注册任何协议**。

## 目录里都有什么

| 条目 | 说明 |
|---|---|
| `DshConfigHost.exe` | 自包含单文件产物（约 59 MB，打包了 .NET 运行时）。**必须留在 `ConfigHost\` 下**，程序靠自身位置反推安装根目录 |
| `WebView2Loader.dll` 等 6 个原生 dll | WebView2 与 WPF/WinForms 的原生依赖，随产物一起放这里 |
| `wwwroot\index.html` `wwwroot\app.js` | 界面。改这两个文件**不需要重新编译**，程序直接读它们 |
| `defaults.json` | 「恢复默认」用的值表，由 `Install.cmd` 从 `default-config.ps1` 推导生成 |
| `default-config.ps1` | **默认配置模板**，是 `defaults.json` 与 `Fixed.cmd` 整体重置的唯一来源。机器相关四项是 `__DETECT_*__` 占位符，探测不到就保留占位符并报错，不会写假路径 |
| `src\` | C# 源码与 `ConfigHost.csproj` |
| `tests\` | xUnit 测试（配置写入保真 + 路径解析） |
| `build.cmd` | 重新编译本程序，需要 .NET SDK |

## 重新编译

双击 `build.cmd`。它做四件事：清掉框架依赖时代的残留产物 → `dotnet publish`
自包含单文件 → 把 exe 和 6 个原生 dll 拷进本目录 → 报告字节数。

**不会**拷贝 `wwwroot`：页面资产本来就在 `ConfigHost\wwwroot`，它同时是发布输入，
自我复制没有意义。

改 `wwwroot\` 下的页面不需要重编 —— 只有改 `src\` 下的 C# 才需要。

## 前置依赖

自包含只打包了 .NET，**不含 WebView2 Runtime**。Win11 自带；Win10 通常随 Edge 安装。
缺失时程序不崩溃，会给出可读提示。详见 [../docs/config-host.md](../docs/config-host.md)。

## 诊断开关

带参数运行，用于无界面自检（排障和回归测试用）：

| 参数 | 作用 |
|---|---|
| `--selftest` | 33 条只读断言：环境路径、配置读取保真、入参拒绝、路径校验、自启状态 |
| `--page-test` | 76 条真实页面 DOM 断言，零副作用（含「打开方式」写 JSON、Edge 网页应用检测） |
| `--page-test-write` | 上一条 + 真实保存回路 + 开机自启翻转 + 重置撤销 + 真实重启守护进程 |
| `--print-env` | 打印程序看到的路径与版本 |
| `--detect-app` | 扫描 Edge 会话数据，列出可识别的网页应用并试着匹配当前端口 |
| `--detect-app-bridge` | 同上的桥方法形式（页面走的那条路），打印原始 JSON |
| `--openmode-test` | 直接测 `GetOpenMode`/`SetOpenMode`：browser → app → browser，结束还原原值 |
| `--openurl-test` | 只验 `OpenUrl` 的接受/拒绝边界，不真的打开浏览器 |

> 两个自检（`--page-test`、`--page-test-write`）会在**结束前把打开方式还原**成运行前的值，
> 所以它们不会改动你的设置。

## 「打开方式」的存取

它**不在** `DshLauncherDaemon_Config.ps1` 里，而是写进
`DshLauncher\DshLauncher.json` 的 `openMode` / `edgeAppId`，**切换即写盘**。

原因：ps1 的写入是"只替换已存在的行"，字段不在文件里会被静默跳过；JSON 是托盘的完整
配置，写入必然是新增/替换。托盘每次右键重读该文件，所以菜单文本会立刻跟着变。

给这两个键加任何写入路径时都要**保留它们** —— `scripts\_Common.ps1` 的 `Write-JsonConfig`
和 `TrayConfig.BuildJson` 都只负责刷新 `daemonExe`/`webPort`，早期版本会把这两个键抹掉。

## 测试

```cmd
cd tests && dotnet test          :: 配置写入保真 10 条 + 路径解析 9 条
node ..\tools\test-page-logic.mjs :: 页面纯逻辑 16 条
```

页面里 `app.js` 用一行 `/* ===== 以下是 UI ===== */` 把**纯逻辑**（解析、校验、
构造写入集）和 DOM 操作分开，Node 测试只加载前半段，因此不需要浏览器。
