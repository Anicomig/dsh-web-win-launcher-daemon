# DSH Launcher Daemon（开发目录说明）

Windows 上守护 `dsh web`（DeepSeek Harness Web GUI）的守护进程，已编译为**无窗口 EXE**。

> 这里是**开发目录**；面向使用者的总说明见 [README.md](README.md)，
> 发布副本在 `dsh-web-launcher-daemon\`（由 `同步到发布版.cmd` 生成，不入库）。

---

## 文件清单

| 文件 / 目录 | 用途 |
|------|------|
| `DshLauncherDaemon.exe` | **主程序**，51 KB，双击运行，全程无窗口 |
| `DshLauncherDaemon_Config.ps1` | 配置文件，**必须与 EXE 同目录** |
| `DshLauncherDaemon.ps1` | 守护进程源码（EXE 由它经 PS2EXE 编译而来，运行时不需要它） |
| `Install.cmd` | 安装：修路径 + 建 `defaults.json` + 19 项数据校验。**不改配置文件** |
| `Initialization.cmd` | 安装文件夹移动/改名后修复绝对路径。启动文件夹已有的 `.lnk` 才覆盖，不新增 |
| `uninstall.cmd` | 卸载：三段式逐项问（安装数据 / 安装目录 / 用户数据），默认全不 |
| `Fixed.cmd` | 修复被改坏的数据；唯一会整体重置配置文件的入口（先备份） |
| `同步到发布版.cmd` | 把发布物同步到 `dsh-web-launcher-daemon\` |
| `ConfigHost\` | 配置窗口：`DshConfigHost.exe`（自包含单文件）+ `src\` 源码 + `wwwroot\` 页面 |
| `ConfigHost\build.cmd` | 重新编译 `DshConfigHost.exe`（需要 .NET SDK） |
| `ConfigHost\default-config.ps1` | 默认配置模板，`defaults.json` 与「彻底重置」的**唯一来源** |
| `DshLauncher.lnk` | **双击启动托盘的入口**。由 `Install.cmd` / `Initialization.cmd` 维护，始终存在并指向当前目录的托盘 EXE |
| `DshLauncher\` | 托盘程序：`dist\DshLauncher.exe` + 源码 + `build.cmd` |
| `DshLauncher\DshLauncher.json` | 托盘配置：`daemonExe`、`webPort`，以及**打开方式** `openMode` / `edgeAppId`（切换即写盘） |
| `autostart\` | 两个开机自启 `.lnk` 源文件 + 静默启动用的 `.vbs` |
| `assets\` | 托盘图标 `DshLauncher.ico` / `DshLauncher_light.ico` 与其来源 `original.svg` |
| `scripts\` | 上面四个入口的实现（`.ps1`）与共用库 `_Common.ps1` |
| `tools\` | 页面纯逻辑的 Node 测试 |
| `docs\` | 使用文档（`config-host.md`） |

运行产物不入库（见 `.gitignore`）：`log\`、`_backup\`、`_ps2exe_dl\`（PS2EXE 第三方包，
重新编译守护进程 EXE 时自行获取）、`ConfigHost\src\{bin,obj}\`、`DshLauncher\dist\`、
发布副本 `dsh-web-launcher-daemon\`。

## 使用方式

**双击根目录的 `DshLauncher.lnk`** 启动托盘（它就是托盘 EXE 的快捷方式，图标也跟着托盘走）。
托盘图标出现在通知区，可能在折叠的 `^` 里，右键就是菜单。也可以直接双击
`DshLauncher\dist\DshLauncher.exe`。

这个 `DshLauncher.lnk` **由 `Install.cmd` / `Initialization.cmd` 维护，始终存在**：
安装时建立，文件夹移动后重建，所以它永远指向当前目录里的托盘 EXE（不会因为
复制/改名而失效）。它和 `autostart\` 里那两个自启源文件不是一回事 ——
那两个是否装进启动文件夹由你在配置窗口决定，这个就摆在根目录给你手动双击。

启动守护进程本身：双击 `DshLauncherDaemon.exe` → **没有任何 UAC 弹窗**，静默运行并守护 `dsh web`。

> 本程序**不申请管理员权限**。它只做端口探测、启动 `node.exe`、写自己的日志目录，都不需要管理员。
> 早期版本会自动提权，那会产生两个问题：每次启动弹 UAC；进程变成高完整性后，托盘工具无法结束它。

## 配置窗口

改端口、改路径、开关机自启，都在配置窗口里做：右键托盘图标 →「配置」，
或直接双击 `ConfigHost\DshConfigHost.exe`。**不需要浏览器、不需要联网、不弹授权框。**

详细说明见 [`docs\config-host.md`](docs/config-host.md)。

**外部依赖：Microsoft Edge WebView2 Runtime。** Windows 11 与 1803+ 的 Windows 10 通常已内置；
缺失时窗口会给出提示而不是崩溃，下载页在 
<https://developer.microsoft.com/microsoft-edge/webview2/>。
DshConfigHost 自身是自包含的（.NET 运行时已打进 59 MB 的 exe），所以这是唯一一项外部依赖。

改完 `ConfigHost\` 后双击 `ConfigHost\build.cmd` 重新生成。

### 打开方式（网页 / 网页应用）

配置窗口的「运行参数」里有**打开方式**一项，两种选择：

| 选项 | 右键托盘的菜单项 | 行为 |
|---|---|---|
| 默认浏览器 | `打开网页 (3080)` | 用系统默认浏览器打开 `http://127.0.0.1:<端口>/` |
| Edge 网页应用 | `打开网页应用 (3080)` | `msedge --profile-directory=Default --app-id=<app-id>`，开独立应用窗口（无地址栏，像桌面应用） |

**这一项存在 `DshLauncher\DshLauncher.json` 里，切换即写盘，不需要点保存按钮**；
托盘每次右键都重新读该文件，所以**不用重启托盘**菜单就会跟着变。

选「Edge 网页应用」时窗口会**自动检测**对应的 app-id（读 Edge 的会话数据，只读、
不碰浏览器的任何内容）。前提是那个网页应用已经在 Edge 里「安装为应用」并且**打开过至少一次**
—— Edge 只在打开过的应用里留下记录。检测不到时窗口会给出三步引导（打开网址 →
菜单里「将此站点作为应用安装」→ 打开一次 → 点「重新检测」），也可以从已装列表里手选。

> 为什么不放进 `DshLauncherDaemon_Config.ps1`：那份配置的写入是"只替换已存在的行"，
> 字段不在文件里会被**静默跳过**，于是出现过「保存后重开又变回默认」。JSON 是托盘的
> 完整配置，写入必然是新增/替换，不存在跳过。

## 重新编译

守护进程 EXE 由 `DshLauncherDaemon.ps1` 经 [PS2EXE](https://github.com/MScholtes/PS2EXE)
编译（该第三方包**不入库**，需要时自行获取后放到 `_ps2exe_dl\`）：

```powershell
Import-Module '.\_ps2exe_dl\PS2EXE-master\Module\ps2exe.psd1' -Force
Invoke-PS2EXE -inputFile '.\DshLauncherDaemon.ps1' -outputFile '.\DshLauncherDaemon.exe' `
  -noConsole -noError -title 'DshLauncherDaemon' -description 'DSH Web Launcher Daemon' `
  -product 'DshLauncherDaemon' -version '1.0.0.0'
```

产物为 PE 子系统 2（Windows GUI），因此不会出现控制台窗口。

---

## ⚠️ 改这个脚本时必须知道的 4 个坑（全部实测踩过）

### 1. 文件编码必须是 UTF-8 **with BOM**

PowerShell 5.1 按 ANSI 读取无 BOM 的 `.ps1`。中文注释会变乱码，进而**破坏 here-string 语法**，报出「字符串缺少终止符: "@」这种指向错误行号的误导性错误。

任何工具改写本脚本后，务必重新写入 BOM：

```powershell
$p = ".\DshLauncherDaemon.ps1"
[System.IO.File]::WriteAllText($p, [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8),
    (New-Object System.Text.UTF8Encoding($true)))
```

### 2. 不能点源（dot-source）Config.ps1

Windows 默认执行策略为 `RemoteSigned`，它会**拒绝点源本地未签名的 `.ps1`**，报「未进行数字签名」。
PS2EXE 编译的宿主**不会**传 `-ExecutionPolicy Bypass`，无窗口宿主也没有控制台能显示这个错误 —— 所以配置会**静默失效**。

脚本改为「读取文本 → `[ScriptBlock]::Create()` → 执行」，绕开策略网关。

### 3. 配置加载**不能**放进函数

函数有独立作用域。在函数内执行 ScriptBlock，`CONFIG_*` 变量会被困在函数作用域里，函数返回即消失。

实测证据：函数内部的探针读到 `CONFIG_PORT=3099`，而外层脚本读到 `$null`。
因此配置加载**内联在脚本顶层**，刻意不包函数。

### 4. 空配置会导致无限重启循环

配置全空时 `$lastLaunchAt` 为 `$null`，启动硬超时分支被反复命中且不复位，循环里**没有任何 Sleep** —— 实测产生 **1.26 MB 日志/分钟**。

现在有一道 `Test-ConfigComplete` 闸门：配置不完整就写日志并 `exit 1`，绝不进入循环。
同时硬超时分支补了 `Start-Sleep`，防止任何路径再次退化成忙循环。

---

## 其他注意事项

- **完全无弹窗**：配置错误只写日志，不弹任何对话框（曾经加过 MessageBox，已按需求移除）。
- **`desktop` profile 会被拒绝**：该名称保留给 Electron 应用，`dsh` CLI 拒绝启动它。脚本会提前拦截并给出清晰提示。
- **进程检测依赖 PID**：`Get-CimInstance Win32_Process` 在某些上下文里会被拒绝访问（实测返回空），因此主判据改为「记录自己启动的子进程 PID」，命令行指纹仅作补充。
- **端口不要设 `--port 0`**：由 OS 随机分配，守护进程无法预知端口，健康检查会永久失效。
- **不申请管理员权限**：守护进程以普通权限（Medium 完整性）运行，因此**无 UAC 弹窗**，托盘工具也能自由启停它。
  这同时消除了一个副作用：`dsh web` 内的 agent 工具不再继承管理员级文件访问能力。
- **`DSH_HOME` 已显式钉住**：启动子进程前会写入环境变量，防止它漂移到另一个家目录导致「找不到 sessions / credentials」。
- ⚠️ **不要用低完整性方式编译/运行**：EXE 若带 Low 完整性标签，托盘图标会**静默注册失败**。
  重新编译后请执行 `icacls <exe> /setintegritylevel Medium`（`build.cmd` 已内置）。
  详见 `DshLauncher\README.md` 第 3 条。

## 回滚

本项目已改用 git 管理，历史版本用 `git log` / `git diff` 回溯即可。
（早期从 OpenClaw Gateway 改造为守护 DSH 的过渡文件已清理。）

---

## 验证状态

### 已实测通过（真实端到端）

- PS2EXE 1.0.18 编译成功，PE 子系统 = 2（无控制台窗口）
- **真实 `dsh web` 在 3080 端口成功启动并服务**：`http://127.0.0.1:3080` 返回 HTTP 401（服务健康、认证生效）
- **就绪行被日志捕获机制抓取**（`log/web_*.log`）：
  `dsh web: http://127.0.0.1:3080/?token=...` —— 崩溃取证路径实测可用
- 配置正确加载：`Config: Host=127.0.0.1, Port=3080, Profile=web`
- 参数拼装正确：`node "<dsh>\lib\bin.js" web --host 127.0.0.1 --port 3080 --no-open`
- `DSH_HOME` 与工作目录正确传给子进程
- 状态机：`Initial state: WAITING` → `STATE: WAITING -> ACTIVE (port 3080 now listening)`
- 子进程 PID 被记录用于存活判断
- 单实例保护：非提权父进程记录 `Another instance already running` 后静默退出
- 空配置负面测试：`exit 1` + 精确错误 `text evaluated but defined no CONFIG_* variables`，日志仅 722 字节，**不再空转**
- 用假体复现的完整链路：启动 → WAITING → ACTIVE → 参数/环境/工作目录全部正确

### 未取得干净观测的部分

- **端口消失后的自动重启**：测试服务由提权守护进程持有，低完整性会话既无法终结它，环境变量也无法穿过 UAC 边界传入（看门狗因此无法触发）。观察到的是：服务全程稳定 115 秒，守护进程保持 ACTIVE。
  端口探测与状态切换逻辑本身已被验证，但"崩溃 → 重启"这一跳建议正式部署后人工确认。
- 长时间运行下的内存与日志增长特性。

### 首次正式部署建议确认

1. 双击 EXE，确认只弹 UAC、**没有任何其他窗口**
2. 浏览器打开 `http://127.0.0.1:3080`，确认 GUI 可用
3. 结束占用 3080 的 `node.exe`，确认守护进程在宽限期后自动拉起
   （观察 `log/dsh_daemon_*.log` 出现 `ACTIVE -> WAITING` 与新的 `Starting` 记录）
4. 故意改错 `$CONFIG_DSH_CLI`，确认日志出现 `FATAL: required path missing` 而非静默空转

### 产物校验值

| 文件 | 大小 | SHA256 |
|------|------|--------|
| `DshLauncherDaemon.exe` | 51,712 B | `42CB382FE814CC4A99B6D1D0CE73D86A49608B0CBA4894672BFC16636EB518E3` |
| `DshLauncherDaemon.ps1` | 18,273 B | `8B2379F0CBBF17460D5A5D9445D9D0E78C467B6939F314A61FEABBFBCAC3E0B0` |
| `DshLauncherDaemon_Config.ps1` | 2,470 B | `1180FC81041B206BD96CAEB828FB873F9A65951E1B8278EF331567FAE912460E` |

> 注：`log/web_2026-09-30-13-43_25.log` 是验证期间真实 `dsh web` 的运行日志，
> 其占用的进程仍在运行，因此该文件被锁定，属正常现象。

---

## 开机自启动与托盘工具

| 文件 | 说明 |
|------|------|
| `autostart\LaunchDshLauncherDaemon.vbs` | 无窗口启动守护进程的 VBS。**成功与失败都静默**，不弹任何窗口 |
| `autostart\LaunchDshLauncherDaemon.lnk` | 守护进程自启快捷方式（指向该 VBS；由 Install/Initialization 重建，不入库） |
| `autostart\LaunchDshLauncher.lnk` | 托盘自启快捷方式（直接指向托盘 EXE；同上，不入库） |
| `assets\` | 托盘图标 `DshLauncher.ico` / `DshLauncher_light.ico`（深/浅任务栏两套）与来源 `original.svg` |
| `DshLauncher\build.cmd` | 重新编译托盘（csc.exe，无需 NuGet/SDK）；图标路径为 `..\assets\` |
| `DshLauncher\README.md` | 托盘项目说明（含两个编译陷阱） |

### 装到启动文件夹：用配置窗口，别手动拖

两个 `.lnk` 只是放在 `autostart\` 里作为**源文件**。要启用/关闭开机自启，打开
托盘右键 →「配置」，在「开机启动」区块拨开关即可 —— 它做的就是把对应 `.lnk`
复制到 `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`（关闭则删除），
并即时回读该目录的真实状态作为徽标。

`Initialization.cmd` 只在**启动文件夹里已存在同名 `.lnk`** 时用新目录的版本覆盖它
（用于"整个文件夹被移动过"的场景），绝不会替你新增自启项。

### 重新编译

```cmd
:: 配置窗口 DshConfigHost.exe（需要 .NET SDK）
ConfigHost\build.cmd

:: 托盘 DshLauncher.exe（只需 csc.exe）
cd DshLauncher && build.cmd
```

守护进程 EXE 的重编译命令见上面「重新编译」一节（用 `_ps2exe_dl` 里的 PS2EXE，
该目录不入库需自行获取；编译后需 `icacls <exe> /setintegritylevel Medium`）。

### 验收

- 守护进程：静默启动、无窗口、端口起来后进入 ACTIVE —— 已实测
- 托盘：通知区图标可见（深/浅任务栏两套）、右键菜单各项为真实功能 —— 已实测
- 配置窗口：零授权弹窗、读写配置保真、开机自启可开关、可重启守护进程 —— 已实测
  （机器断言 33 + 48 + 86 条，另有 C# 19 条、Node 16 条）

诊断开关（`ConfigHost\DshConfigHost.exe` 带参数运行）见
[docs\config-host.md](docs/config-host.md)。
