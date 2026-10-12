# 配置窗口（ConfigHost）

DSH Launcher 的配置界面。一个本地窗口，直接读写配置文件、管理开机自启、重启守护进程。

---

## 怎么打开

两种都一样：

- **托盘菜单**：右键通知区的托盘图标 → 「配置」
- **直接双击** `ConfigHost\DshConfigHost.exe`

不需要浏览器，不需要联网，不需要管理员权限，**也不会弹任何授权框**。

> 早期版本是「浏览器打开 `file://` 页面 + `dshlauncher://` 自定义协议」，
> 需要给页面授权目录、每次打开重新授权、操作要等最长 25 秒轮询。
> 这些现在都不存在了 —— 页面由本地 DshConfigHost 承载，它本身就是原生进程。

---

## 界面结构

顶部胶囊是分组导航，点一下跳到该组，滚动时自动高亮当前组。底部操作栏常驻。

| 分组 | 内容 |
|---|---|
| **运行参数** | 端口、监听地址、配置名、启动后打开页面、捕获 dsh web 输出、**打开方式** |
| **路径** | DSH 主目录、工作目录、Node 可执行、DSH CLI |
| **检测与超时** | 5 个轮询间隔与超时参数 |
| **开机启动** | 后台守护进程 / 托盘程序 两个开关，打开即显示当前状态 |
| **环境信息** | 安装目录、配置文件路径、DshConfigHost 与 WebView2 版本、守护进程是否在跑 |

- 带 **?** 的字段有说明，点开看，默认收起。
- 路径类字段与环境信息里的三个地址，右侧有一个**文件夹图标**：点开该路径**所在的文件夹**
  （目录开它自己，文件开它的上级目录，例如配置文件会打开到 `DshWebLauncherDaemon\`）。
  只执行 `explorer.exe`，从不执行你填的那个路径；路径不存在时报错而不会创建。
- 窗口标题栏与滚动条均为深色（标题栏由 DshConfigHost 调 `DwmSetWindowAttribute` 开启，
  需要 Windows 10 1809 及以上；更早版本会保持系统原样，不影响功能）。
- 路径类字段**填完自动校验**（就地显示"存在 / 不存在"），不用点任何按钮。
- 「保存并重启」= 先保存，再重启后台守护进程。**配置只在守护进程启动时读取一次，所以改完必须重启才生效。**

---

## 打开方式：网页 / Edge 网页应用

「运行参数」里的**打开方式**决定托盘那个菜单项怎么打开 dsh web：

| 选项 | 托盘菜单项 | 行为 |
|---|---|---|
| 默认浏览器 | `打开网页 (3080)` | 系统默认浏览器打开 `http://127.0.0.1:<端口>/` |
| Edge 网页应用 | `打开网页应用 (3080)` | 开 Edge 独立应用窗口（`--app-id=<id>`，无地址栏） |

### 它和别的字段不一样

**切换即写盘，不需要点「保存」**，也不参与表单的"未保存"标记。

原因是它**不存在 `DshLauncherDaemon_Config.ps1` 里，而是写进 `DshLauncher\DshLauncher.json`**：

- 这一项只影响托盘怎么打开网页，守护进程根本不关心；
- ps1 的写入是"只替换已存在的行"，字段不在文件里会被**静默跳过** —— 早先把它放 ps1
  时就出现过「保存完重开又变回默认浏览器」；
- JSON 是托盘的完整配置，写入必然是新增/替换，不存在跳过。

托盘每次右键都重新读这个文件，所以**不用重启托盘**，菜单文本就会变。

### 选「Edge 网页应用」时会自动检测

窗口会读 Edge 的会话数据（`User Data\Default\Sessions`，**只读**，不碰书签、历史、
登录状态、Cookie）找出与该端口对应的 app-id 并填入。文件被 Edge 独占时用共享方式读取，
读不了就跳过。

**检测不到通常是因为那个应用还没「打开过」** —— Edge 只在打开过的应用里留下记录。
窗口会给出三步引导：

1. 确认 dsh web 在跑，用 Edge 打开提示里的地址（面板上有「打开」「复制」两个按钮）
2. Edge 右上角菜单 → **应用** → **将此站点作为应用安装**
3. 把装好的应用**打开一次**，回来点「重新检测」

面板下方还会列出**当前已检测到的其它网页应用**，可以直接勾选其中一个。

> app-id 是 Edge 内部给应用分配的 32 位标识（字母只可能是 `a`–`p`）。
> 它**不能从网址推算**（实测 MD5/SHA1/SHA256/SHA512 各种切法都对不上），
> 只能从 Edge 自己的数据里读出来。

---

## 配套脚本分工

| 入口 | 什么时候用 | 会不会改 `DshLauncherDaemon_Config.ps1` |
|---|---|---|
| **`Install.cmd`** | 新机器 / 新用户 / 想重装并校验数据 | **不会** |
| **`Initialization.cmd`** | 把安装文件夹移动或改名之后 | **不会** |
| **`uninstall.cmd`** | 卸载（三段式逐项问，默认全不） | 由你决定 |
| **`Fixed.cmd`** | 数据被改坏了要修 | **只有它会整体覆盖**（先备份） |

### 三个快捷方式各管什么

| 快捷方式 | 位置 | 谁维护 | 用途 |
|---|---|---|---|
| `DshLauncher.lnk` | 安装根目录 | `Install.cmd` / `Initialization.cmd`（**始终重建**） | 双击启动托盘。窗口样式=正常 |
| `LaunchDshLauncherDaemon.lnk` | `autostart\` | 同上（源文件） | 守护进程开机自启的源，经 VBS 静默启动 |
| `LaunchDshLauncher.lnk` | `autostart\` | 同上（源文件） | 托盘开机自启的源 |

根目录那个**始终存在**，因为它是用户手动启动托盘的入口；`autostart\` 那两个只是源文件，
**装不装进启动文件夹由你在配置窗口的「开机启动」里决定**，脚本不会替你加。
`Initialization.cmd` 只会在启动文件夹**已有同名项**时用新目录的版本覆盖它。

改配置只有两条路：直接编辑 `DshLauncherDaemon_Config.ps1`，或在 DshConfigHost 窗口里改。
`Install.cmd` 与 `Initialization.cmd` 都不再碰它 —— 这是和旧版「安装初始化」最大的区别。

默认值的唯一来源是 `ConfigHost\default-config.ps1`：`defaults.json`（窗口里的「恢复默认」）
和 `Fixed.cmd` 的整体重置都从它推导，不再各自维护字段清单。

---
## 依赖：Microsoft Edge WebView2 Runtime

配置窗口的界面部分需要 WebView2 Runtime。

- Windows 11 与较新的 Windows 10（1803 及以上）通常**已经内置**，无需处理。
- 缺失时窗口会给出提示而不会崩溃。下载地址：
  <https://developer.microsoft.com/microsoft-edge/webview2/>

> DshConfigHost 本身是**自包含**的（.NET 运行时已打进 exe），所以只有 WebView2 Runtime 这一项外部依赖。
> 代价是 `DshConfigHost.exe` 约 59 MB。

---

## `defaults.json` 与「恢复默认」

`ConfigHost\defaults.json` 存的是**这台机器上**的建议默认值（node 路径、DSH 主目录、工作目录等），
由「Install.cmd」生成。

- 点「恢复默认」只是把这些值填进表单，**不会立刻写文件** —— 你确认后仍需点「保存」。
- 如果这个文件缺失或损坏，「恢复默认」会提示你去运行「Install.cmd」，并且**不会改动任何字段**。
- 这个文件含机器绝对路径，所以**不参与同步**，每台机器自己生成。

---

## 开机自启写在哪里

复制到当前用户的启动文件夹：

```
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
```

源文件在项目里的 `autostart\` 下，两个确切的名字：

- `LaunchDshLauncherDaemon.lnk` —— 后台守护进程
- `LaunchDshLauncher.lnk` —— 托盘程序

状态判定是**逐字节比对**：目标与源一致算"已启用"；不存在算"未启用"；存在但不一致算"异常"并提示你检查（不猜测、不擅自覆盖）。

只支持当前用户的启动文件夹。`ProgramData` 那份需要管理员权限，本项目刻意不做。

---

## 重新编译 DshConfigHost

改了 `ConfigHost\src\` 或 `ConfigHost\wwwroot\` 之后，双击 **`ConfigHost\build.cmd`**（生成 `ConfigHost\DshConfigHost.exe`）。

它做的事：

1. 清掉 `ConfigHost\` 里框架依赖时代的残留
2. `dotnet publish` 成自包含单文件
3. 把 exe 和必需的原生 DLL 复制回 `ConfigHost\`

需要 .NET SDK（写这份文档时是 10.0.401）。页面资产在 `ConfigHost\wwwroot\`，
它就是发布源，所以脚本不会去拷贝它。

### 内置的三个诊断开关

都不改变正常使用行为，排查问题比读日志快：

```powershell
ConfigHost\DshConfigHost.exe --print-env          # 打印它认出来的安装根、配置路径、版本
ConfigHost\DshConfigHost.exe --selftest           # 33 条无界面自检（桥方法、入参安全、只读性）
ConfigHost\DshConfigHost.exe --page-test          # 44 条真实页面断言（不写文件）
ConfigHost\DshConfigHost.exe --page-test-write    # 60 条，额外做真实保存回路，结束后逐字节还原
```

后两个退出码 0 表示全通过。

---

## 排障

| 现象 | 原因 / 怎么办 |
|---|---|
| 双击 exe 没反应 | 看是否弹了"界面初始化失败"——多半是缺 WebView2 Runtime |
| 提示"配置窗口已经打开" | 已经有一个窗口了，去任务栏/通知区找 |
| 托盘点「配置」提示找不到 exe | 没编译过。双击 `ConfigHost\build.cmd` |
| 提示"找不到安装目录" | exe 被从 `ConfigHost\` 里挪走了。它必须待在 `<安装根>\ConfigHost\`，DshConfigHost 靠自身位置反推安装根 |
| 保存后行为没变 | 配置只在守护进程启动时读一次。点「保存并重启」 |
| 路径显示"不存在"但你觉得应该在 | 看右侧括号里 DshConfigHost 解析出的绝对路径 —— 相对路径是相对守护进程的工作目录解析的 |
| 想彻底重来 | 删掉 `%LOCALAPPDATA%\DshConfigHost\`（界面缓存），再重新打开 |

---

配置窗口**不写注册表、不注册协议**，所以卸载主要就是删东西本身。
双击 **`uninstall.cmd`**，它会分三段逐个问你，默认全是「不」：

| 段 | 问什么 | 包含 |
|---|---|---|
| 1 | 清理**安装数据**（写到安装目录之外的） | `%LOCALAPPDATA%\DshConfigHost`（WebView2 缓存，可再生）、启动文件夹里那两个自启 `.lnk`、老版本遗留的 `HKCU\Software\Classes\dshlauncher` |
| 2 | 删除**安装目录**内容 | 清空本文件夹，**保留 `_backup\`**；需要额外输入确认词 |
| 3 | 清理**用户数据** | `log\`、`DshLauncherDaemon_Config.ps1`；若第 2 段已删目录则自动跳过 |

第 1 段里的 WebView2 缓存删掉没有任何损失：下次打开配置窗口会自动重建。