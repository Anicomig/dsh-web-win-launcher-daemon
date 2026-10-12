# DshLauncher

`DshWebLauncherDaemon` 的托盘工具：无窗口常驻，右键菜单控制守护进程。

## 怎么启动它

双击**安装根目录**的 `DshLauncher.lnk`（也可以直接双击 `DshLauncher.exe`）。

那个快捷方式由 `Install.cmd` / `Initialization.cmd` / `Fixed.cmd` 维护，**始终存在**且
指向当前目录里的托盘 EXE（`WindowStyle = 1`，正常窗口）——
文件夹被移动或复制后跑一次 `Initialization.cmd` 就会重新指向新位置。
它和 `autostart\` 里那两个自启源文件不是一回事（那两个是 `WindowStyle = 7` + VBS 静默）。

## 功能（已实现）

`DshLauncher.exe` 在通知区显示图标，右键弹出**暗色 + 圆角**菜单。

菜单是**状态驱动**的：两个条目的标签随 daemon 运行状态变化，但功能不变。

| 菜单项 | 行为 |
|---|---|
| 打开网页 (3080) / **打开网页应用 (3080)** | 两种标签，由 `DshLauncher.json` 的 `openMode` 决定：默认浏览器打开 URL，或 `msedge --app-id=<id>` 开 Edge 网页应用独立窗口 |
| **启动后台 Daemon** / **关闭后台 Daemon** | **同一项的两种标签**。未运行→启动 `DshLauncherDaemon.exe`（**不弹 UAC**）；运行中→结束该进程 |
| **启动 dsh web** / **重启 dsh web** / **关闭 dsh web** | **同一项的三种标签**。<br>· **daemon 运行中** → 「重启 dsh web」：杀掉 node 一次，daemon 会在约 15 秒内拉回来<br>· **无 daemon、web 在跑** → 「关闭 dsh web」：杀掉 node 一次，没人再拉起<br>· **两者都没跑** → 「启动 dsh web」：托盘**直接拉起 node**（`node "<bin.js>" web --host <host> --port <port> [--no-open]`，cwd 与 `DSH_HOME` 均与 daemon 一致） |
| 打开 log | 用资源管理器打开 daemon 所在目录的 `log\` |
| 配置 | 启动 `ConfigHost\DshConfigHost.exe`（配置窗口）。它读写 `DshLauncherDaemon_Config.ps1`、分别管理两项开机自启（`autostart\` 下的 `.lnk` ↔ 启动文件夹）、校验路径、重启守护进程。**不需要浏览器、不注册协议、不弹授权框** |
| ───────── | 分隔线 |
| 退出 Daemon 及 Launcher | 结束 daemon 再退出托盘，**dsh web 继续运行** |
| 关闭 dsh web，Daemon 及 Launcher | 杀 node + 结束 daemon + 退出托盘（**彻底停止**） |
| 仅退出 Launcher | 只退出托盘 |

**动态刷新**（每次右键都会执行）：

- 重新读取 JSON，把「打开网页 (**端口**)」的端口刷新为当前值
- 同时读 `openMode`/`edgeAppId`，据此在「打开网页」与「打开网页应用」之间切换文本
  （配置窗口改完立即生效，不需要重启托盘）
- 检查 daemon 是否在运行，据此决定上面两个条目的标签
- JSON 读取失败时，菜单顶部会出现一条**禁用的告警项**，说明原因

**结果反馈**：成功走**气泡提示**（不打断），失败走 **MessageBox**（需要确认）。

> **配置入口的历史**：早先版本用默认浏览器打开 `ConfigTools\index.html`，靠同目录的
> `paths.js` 传路径、靠 `dshlauncher://` 协议回写配置 —— 需要浏览器授权，且换机器就失效。
> 该方案已废弃，`ConfigTools\` 与协议注册都已从项目中移除。现在托盘只是
> `Process.Start` 一下 `ConfigHost\DshConfigHost.exe`；找不到它时气泡会提示先运行
> `ConfigHost\build.cmd`。老用户机器上残留的 `HKCU\Software\Classes\dshlauncher`
> 可以由 `uninstall.cmd` 的第 1 段清掉。

### 菜单的外观与弹出位置

- **圆角**：通过 `DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND)`
  让 DWM 画圆角 —— 与 Win11 原生菜单同机制，**平滑抗锯齿**。
  不用 GDI+ `Region` 裁剪，因为那样边缘会有明显锯齿。
  Windows 10 及更早版本该调用失败，菜单退回直角，**不是错误**。
- **弹出位置**：以**右上角对齐光标**，菜单向左展开（默认是左上角对齐、向右展开）。
  因为 `NotifyIcon.ContextMenuStrip` 内部走 `TrackPopupMenu`，无法改锚点，
  所以改成**不挂 ContextMenuStrip**，而是在 `MouseUp` 里手动调
  `ContextMenuBuilder.ShowAtCursor()`：
  1. 先刷新标签/可见性（会影响尺寸）
  2. `GetPreferredSize` 量出尺寸
  3. `x = 光标X - 菜单宽`，`y = 光标Y`
  4. 按光标所在屏幕的**工作区**钳制：左边放不下就翻到光标右侧，下方放不下就向上展开

### 配置文件 `DshLauncher.json`

位置：`DshWebLauncherDaemon\DshLauncher\DshLauncher.json`

```json
{
  "daemonExe": "<install-root>\\DshLauncherDaemon.exe",
  "webPort": 3080,
  "openMode": "browser",
  "edgeAppId": ""
}
```

- 路径按 Windows 习惯书写，JSON 要求反斜杠转义为 `\\`；
  **加载器也容忍手改成的单反斜杠**，两种写法都能用
- `log` 目录 = `daemonExe` 所在目录 + `\log`，无需单独配置

#### `openMode` / `edgeAppId`（打开方式）

由配置窗口的「打开方式」写入，**切换即写盘**，托盘每次右键重新读，所以改完不用重启托盘。

| `openMode` | 菜单项文本 | 点击行为 |
|---|---|---|
| `browser`（默认） | `打开网页 (端口)` | `Process.Start(http://127.0.0.1:<端口>/)` |
| `app` + 有效 `edgeAppId` | `打开网页应用 (端口)` | `msedge --profile-directory=Default --app-id=<id>` |

两个键缺省或值非法时一律退回 `browser`，不影响其它功能。`edgeAppId` 必须是
32 个 `a`–`p` 字符，否则当作空。

> 两个写入路径（`TrayConfig.BuildJson` 刷新端口时、`scripts\_Common.ps1` 的
> `Write-JsonConfig`）都必须**保留**这两个键，否则会把用户的打开方式重置回默认。

**端口自动同步**：

- 启动托盘时，读取 `daemonExe` 同目录下的 `DshLauncherDaemon_Config.ps1`，
  用正则取出 `$CONFIG_PORT`，写回 JSON
- 点击「启动后台 Daemon」时**再同步一次**
- 因此改 `Config.ps1` 的端口后无需手动改 JSON，托盘会自动跟上

**查找顺序**（第一个存在的生效）：

1. `<exe 所在目录>\DshLauncher.json`（即 `dist\`）
2. `<上一层>\DshLauncher.json`（即 `DshLauncher\`）← 实际位置
3. `<再上一层>\DshLauncher.json`

全部找不到时会用合理默认值（daemon 路径按 exe 位置推算、端口 3080），并把原因写进菜单顶部的告警项。

## 编译

```cmd
cd DshLauncher
build.cmd
```

产物：`dist\DshLauncher.exe`（PE 子系统 2，无控制台窗口）。
`build.cmd` 会在编译后自动执行 `icacls /setintegritylevel Medium`（见下文第 3 条）。

编译只用系统自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，
**不需要 NuGet、不需要 dotnet SDK**。

## 运行

双击 `dist\DshLauncher.exe`，通知区出现图标，右键即可看到菜单。

## 文件职责

| 文件 | 负责 | 不负责 |
|------|------|--------|
| `Program.cs` | 进程入口，`ApplicationContext` 宿主，退出分发 | 图标、菜单 |
| `Tray\NotifyIconHost.cs` | `NotifyIcon` 实例与生命周期（可见性、Dispose）、事件路由 | 具体菜单项 |
| `Tray\ContextMenuBuilder.cs` | `ContextMenuStrip`、全部菜单项、每次打开时的动态刷新与标签切换 | 图标、配置解析、具体动作 |
| `Tray\DarkMenuRenderer.cs` | 暗色主题（`ToolStripProfessionalRenderer` + 暗色 `ProfessionalColorTable`） | 菜单结构 |
| `Tray\TrayConfig.cs` | 读写 `DshLauncher.json`（含 `openMode`/`edgeAppId`）、解析 `DshLauncherDaemon_Config.ps1` 的端口 | UI |
| `Tray\DaemonController.cs` | daemon 启停与状态、dsh web 的启/停/重启（按 `webPort` 收窄，见下文）、开网页（按 `openMode` 二选一）、打开 log | UI、配置解析 |
| `Tray\DshWebLauncher.cs` | 无 daemon 时托盘**直接**拉起 `node … web --host --port`，cwd 与 `DSH_HOME` 与 daemon 一致 | daemon 生命周期 |
| `Tray\ConfigPageLauncher.cs` | 启动 `ConfigHost\DshConfigHost.exe`；找不到时给出可操作的提示 | 配置读写本身 |
| `Properties\Resources.Designer.cs` | 从嵌入资源读图标（明暗两套） | 其他 |
| `Properties\Resources.resx` | 资源清单（IDE 用；csc 构建不依赖它） | 运行时逻辑 |
| `DshLauncher.json` | daemon 路径 + 网页端口 | 运行时逻辑 |
| `build.cmd` | 一键编译 + 修复完整性标签 | — |

## 三个必须知道的实现约束

### 1. 图标必须用经典 DIB 格式（≤48px）

`csc.exe` 的 `/win32icon` 用的是 Win32 图标解析器，它**只接受 PNG 格式的 256×256 entry**。
如果给小尺寸也用 PNG，编译会报：

```
error CS1567: 生成 Win32 资源时出错: 读取图标失败 -- 参数无效
```

所以 `DshLauncher.ico` 采用混合格式（与 Visual Studio 输出一致）：

| 尺寸 | 格式 |
|------|------|
| 16×16 | DIB (BITMAPINFOHEADER + 32bpp XOR + AND 掩码) |
| 32×32 | DIB |
| 48×48 | DIB |
| 256×256 | PNG |

重新生成 ICO 时必须保持这个组合。

### 2. 不能用 `ComponentResourceManager` 读图标

`build.cmd` 用 `/resource:DshLauncher.ico,DshLauncher.Properties.Resources.dshLauncherIcon`
把图标嵌成**顶层清单资源**。`ComponentResourceManager` 会去找
`DshLauncher.Properties.Resources.resources` 容器，找不到就抛 `MissingManifestResourceException`
—— 表现为**进程静默退出，退出码 `0xE0434352`**（.NET 未处理异常），且因为是 winexe 没有控制台，
完全看不到错误。

所以 `Resources.Designer.cs` 直接用 `Assembly.GetManifestResourceStream(...)` 读取。

### 3. ⚠️ 最隐蔽的一个：EXE 不能带「低完整性标签」

**这是托盘图标完全不显示的真正原因，排查花了很久。**

因果链：

1. 若编译 `DshLauncher.exe` 的进程以**低完整性（Low integrity）**运行，
   生成的文件会**继承 Low 完整性标签**（Windows 的强制性完整性控制）
2. Windows 启动该 EXE 时，进程也以 **Low 完整性**运行
3. 低完整性进程**不能向中等完整性的 Explorer 托盘窗口发消息**（UIPI 拦截）
4. 于是 `Shell_NotifyIcon(NIM_ADD)` 返回 **FALSE**，`GetLastError` = **5 (ACCESS_DENIED)**
   —— **连 Windows 自带的系统图标也一样被拒**
5. `.NET` 的 `NotifyIcon.Visible = true` **静默吞掉这个失败**，不抛异常
6. 结果：进程正常常驻、日志一切正常、**图标永不出现**，且没有任何错误提示

**修复**（`build.cmd` 已内置这一步）：

```cmd
icacls "%ROOT%\dist\DshLauncher.exe" /setintegritylevel Medium
```

**排查这个问题的工具**：`_traytest\NicProbe2.exe`
它直接调用底层 `Shell_NotifyIcon` 并记录返回值、错误码、进程完整性级别。
判据：

- `integrity = 0x2000 (Medium)` + `NIM_ADD=True` → 正常
- `integrity = 0x1000 (Low)`  + `NIM_ADD=False err=5` → 就是这个坑

> 注意：`whoami /groups` 可能显示 `Medium Mandatory Level`，但**实际进程可能是 Low**。
> 不要相信 `whoami`，要用 `NicProbe2.exe` 读令牌里的完整性 RID。

## 源码文件必须是 UTF-8 with BOM

PowerShell 5.1 与部分工具链按 ANSI 读取无 BOM 的文本文件。中文注释在无 BOM 时会被解码成乱码。
任何工具改写这些文件后，请重新写入 BOM：

```powershell
$p = ".\DshLauncher\Tray\ContextMenuBuilder.cs"
[System.IO.File]::WriteAllText($p, [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8),
    (New-Object System.Text.UTF8Encoding $true))
```

## 已验证

- `build.cmd` 编译成功，无警告无错误
- `dist\DshLauncher.exe` = 100,864 字节，PE 子系统 = 2（无控制台窗口）
- 图标从 `..\assets\` 读取并成功嵌入（`build.cmd` 有 pre-flight 检查，缺文件会直接
  报错退出，不会产出一个没图标的 exe）
- 通知区图标可见，深/浅任务栏两套都能正确取到
- 右键菜单各项**都是真实功能**（不再是早期的 MessageBox 占位）
- 「仅退出 Launcher」只退托盘、不影响 daemon 与 dsh web
- 守护进程重启**不牵连** dsh web：实测重启后 3080 上的 node PID 全程未变
- 「重启 / 关闭 dsh web」只命中配置端口上的实例（见下一节）

## 早期占位实现的历史

最初几版菜单项只做「弹出 MessageBox 报告将要做什么」，用于先验收图标与菜单外观。
该阶段早已过去，现在每一项都接了真实动作。

## 「重启 / 关闭 dsh web」只针对配置端口上的实例

托盘按 `DshLauncher.json` 里的 `webPort` 收窄，判定顺序（`Tray\DshWebScope.cs`）：

1. 命令行里有 `--port N`：`N == webPort` 才算我们的；`N != webPort` **永不触碰**。
   守护进程与托盘自己起的 dsh web 都带显式 `--port`，所以这一条就足够。
2. 命令行里没有 `--port`：退回到 socket 归属判断（`Tray\TcpListenerProbe.cs`，
   `GetExtendedTcpTable`）—— 它正在监听我们的端口才算我们的。
3. `webPort` 拿不到（≤0）时**不做收窄**，保持原有行为。探测失败也不会扩大杀伤面。

`IsDshWebRunning()` 与 `StopDshWeb()` 共用同一份判定，所以菜单文案不会和点击实际
做的事不一致。找不到目标时消息会写明端口，例如
「端口 3080 上没有发现 dsh web 进程（其它端口上的实例不会被触碰）」。

> 为什么不用 `netstat`：它的列布局随系统语言变化（中文 Windows 与英文不同）。
> 为什么不用 `IPGlobalProperties`：`GetActiveTcpListeners()` 不返回 PID。

验证方式：把 `DaemonController` / `TrayConfig` / `DshWebLauncher` / `DshWebScope` /
`TcpListenerProbe` 连同探针一起用 `csc.exe` 编到 `dist\` 下运行，读真实
`DshLauncher.json`（webPort=3080），反射调用 `FindDshWebProcessIds()`，
确认返回 `[22140]`（3080 那个）且不含 `22784`（手动跑在 3300 的那个）。
