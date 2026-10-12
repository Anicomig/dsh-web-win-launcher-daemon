# DSH Web Launcher Daemon（Windows）

[中文](README.md) | [English](README.en.md)

一个 Windows 上的守护进程 + 托盘程序，负责让
[DeepSeek Harness](https://github.com/deepseek-ai/dsh) 的 `dsh web` 网页界面
保持运行。**免安装、不申请管理员权限**，日常操作全在托盘右键菜单里。

- **守护进程**（`DshLauncherDaemon.exe`，约 50 KB，PS2EXE 编译）——静默看护
  `node "<dsh>\lib\bin.js" web …`：崩溃自动重启、抓取就绪标记行写日志、
  全程无控制台窗口。
- **托盘**（`DshLauncher.exe`，约 100 KB，csc 编译）——WinForms 托盘图标，
  暗色圆角右键菜单：打开网页（默认浏览器或 Edge 网页应用）、启停守护进程、
  启动/重启/关闭 dsh web、打开 log 目录、打开配置窗口、退出。
- **配置窗口**（`ConfigHost\DshConfigHost.exe`，自包含单文件约 60 MB）——
  WebView2 本地窗口，读写守护进程的 `.ps1` 配置、切换打开方式、管理两个
  开机自启开关。

> **整个文件夹可以随便移动。** 所有写死的绝对路径都由 `Initialization.cmd`
> 按*当前所在目录*重写——复制/改名之后跑一次它即可恢复。

## Windows 前提条件

- **Windows 10（1803 及以上）或 Windows 11**。Win11 开箱即用；Win10 可能
  需要补装下面两项。
- **Microsoft Edge WebView2 Runtime**。Win11 与多数 Win10 1803+ 已内置；
  缺失时配置窗口会给出可读提示而不是崩溃。下载页：
  <https://developer.microsoft.com/microsoft-edge/webview2/>
  （Evergreen Standalone Installer，约 110 MB，按用户安装）。
- **[Node.js](https://nodejs.org/) ≥ 18**。`dsh web` 本身是 Node 程序。
  `node` 需在 `PATH` 里，**或者**在配置窗口的「路径 → Node 可执行」里指全路径。
- **通过 npm 安装的 [`dsh`](https://github.com/deepseek-ai/dsh)**——被看护的
  对象就是它：
  ```cmd
  npm install -g @deepseek-ai/dsh
  ```
  `Install.cmd` 首次运行时会自动找到
  `<npm 全局根>\node_modules\@deepseek-ai\dsh\lib\bin.js`。

> 全程不申请管理员权限、不写注册表、不注册任何 URL 协议。
> 两个开机自启快捷方式只放在**当前用户**的启动文件夹里。

## 安装与首次使用

1. **解压** `dsh-web-launcher-daemon-v0.1.0.zip` 到任意位置
   （例如 `D:\tools\dsh-web-launcher-daemon`），避开需要管理员才能写入的目录。
2. **双击 `Install.cmd`**，它会：
   - 校验守护进程、托盘、配置窗口三个二进制都在；
   - 按默认模板生成 `ConfigHost\defaults.json` 与 `DshLauncherDaemon_Config.ps1`，
     本机探测并填入 `node.exe`、`dsh` CLI、`~\.dsh`、`Documents\workspace`；
   - 重建三个 `.lnk` 快捷方式（根目录的 `DshLauncher.lnk`、
     `autostart\` 里两个自启源文件），全部指向当前目录；
   - 跑一遍完整数据体检并报告结果。
3. **双击 `DshLauncher.lnk`** 启动托盘。图标出现在通知区
   （可能在折叠的 `^` 里），右键就是菜单。
4. **托盘右键 →「启动后台 Daemon」**。守护进程静默启动；几秒后
   **右键 →「打开网页 (3080)」**就会在默认浏览器里打开 DSH 网页界面。

> 第一次打开网页时页面会要求登录——那是 `dsh web` 自己的认证，与本守护进程无关。
> 登录一次之后就会记住。

## 四个入口脚本各管什么

| 脚本 | 什么时候用 | 会不会改 `DshLauncherDaemon_Config.ps1`？ |
|---|---|---|
| **`Install.cmd`** | 新机器 / 新用户 / 想校验环境 | **不会**（仅当配置文件不存在时按模板生成一份） |
| **`Initialization.cmd`** | 把安装文件夹移动或改名之后 | **不会** |
| **`uninstall.cmd`** | 卸载：三段逐项问，默认全是「不」 | 第 3 段删除它 |
| **`Fixed.cmd`** | 修复被改坏的数据；唯一会整体重置配置的入口 | **会**（动手前先备份） |

改配置只有两条路：直接编辑 `DshLauncherDaemon_Config.ps1`，或在 ConfigHost
窗口里改。`Install.cmd` 与 `Initialization.cmd` 绝不会悄悄覆盖你的配置。

## 打开方式：网页 / Edge 网页应用

托盘那个菜单项有两种形态，由配置窗口「运行参数 → 打开方式」决定：

| 托盘菜单项 | 行为 |
|---|---|
| `打开网页 (3080)` | 用系统默认浏览器打开 `http://127.0.0.1:<端口>/` |
| `打开网页应用 (3080)` | 用已安装的 Edge 网页应用开独立窗口（`msedge --app-id=<id>`，无地址栏，像桌面应用） |

**切换即生效，不需要点保存**——这一项存在 `DshLauncher\DshLauncher.json` 里
（不在 `.ps1` 配置里），托盘每次右键都重新读该文件，所以菜单文本跟着变、
不用重启托盘。

选「Edge 网页应用」时窗口会读 Edge 的会话数据来定位 app-id（**只读**、共享
方式加锁，绝不碰 Cookie、历史、登录状态、书签）。检测不到通常是因为那个应用
还没「打开过」——窗口会给出三步引导：打开网址 → ⋮ 菜单 → 「将此站点作为应用
安装」→ 打开一次 → 回来点「重新检测」。

## 配置窗口

`ConfigHost\DshConfigHost.exe` 是一个暗色单窗口编辑器，对应
`DshLauncherDaemon_Config.ps1` 里的五个分组：

- **运行参数** —— 端口、监听地址、profile、`dsh` 追加参数、打开方式
- **路径** —— `node.exe`、`dsh` CLI、DSH 家目录、工作目录
- **检测与超时** —— 5 个轮询间隔与超时参数
- **开机启动** —— 守护进程 / 托盘两个开关（徽标显示启动文件夹的**真实状态**，不是缓存标记）
- **环境信息** —— 只读的排障信息

路径字段填完就地校验（绿勾 / 红叉）。「保存并重启」一步完成写文件 + 重启守护
进程——因为配置只在守护进程启动时读取一次。

完整说明见 [`docs\config-host.md`](docs/config-host.md)。

## 开机自启

配置窗口「开机启动」里有两个独立开关：

- **后台守护进程** —— 把 `autostart\LaunchDshLauncherDaemon.lnk` 复制到
  `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`。该快捷方式指向
  `autostart\LaunchDshLauncherDaemon.vbs`，静默拉起 `DshLauncherDaemon.exe`：
  无窗口、无控制台、成功失败都不弹框。
- **托盘程序** —— 把 `autostart\LaunchDshLauncher.lnk`（直接指向
  `DshLauncher.exe`）复制到同一个启动文件夹。

关掉开关就是把对应文件删掉，然后重新读目录状态。
`Initialization.cmd` 对启动文件夹的处理是**「已有才覆盖，没有绝不新增」**——
是否开机自启完全由你决定，脚本不会替你加。

## 卸载

双击 `uninstall.cmd`。三段逐个问，默认全是「不」：

1. **安装数据**（写到文件夹之外的）：`%LOCALAPPDATA%\DshConfigHost\` 的
   WebView2 缓存（可再生，不含配置）、启动文件夹里那两个 `.lnk`、以及很老的
   版本遗留的 `HKCU\Software\Classes\dshlauncher` 键。
2. **安装目录**内容：清空本文件夹但**保留 `_backup\`**；需要额外输入确认词。
3. **用户数据**：`log\` 目录与 `DshLauncherDaemon_Config.ps1`
   （若第 2 段已删目录则自动跳过）。

## 源码布局

```
DshLauncherDaemon.exe          守护进程源码经 PS2EXE 的产物（发布 zip 内含，不入 git）
DshLauncherDaemon.ps1          守护进程源码
DshLauncherDaemon_Config.ps1   运行时配置（你自己改，或用 ConfigHost；首次 Install 生成）
DshLauncher\                   托盘程序源码 + build.cmd
ConfigHost\                    配置窗口源码 + wwwroot 页面 + default-config.ps1 模板
autostart\                     静默启动用的 VBS（.lnk 由 Install 生成，不入库）
assets\                        托盘图标
scripts\                       Install / Initialization / uninstall / Fixed 的实现
docs\                          使用文档
tools\                         页面纯逻辑的 Node 测试（无依赖）
```

跑页面逻辑测试（16 条断言，无需构建）：

```cmd
node tools\test-page-logic.mjs
```

## 从源码重新编译

| 产物 | 命令 | 需要 |
|---|---|---|
| 托盘（`DshLauncher.exe`） | `cd DshLauncher && build.cmd` | 无额外依赖——用系统自带 `csc.exe` |
| 配置窗口（`DshConfigHost.exe`） | `ConfigHost\build.cmd` | .NET SDK（实测 10.0.401） |
| 守护进程（`DshLauncherDaemon.exe`） | 用 PS2EXE 编译 `DshLauncherDaemon.ps1`（见 `README_DSH.md`） | [PS2EXE](https://github.com/MScholtes/PS2EXE)，编完执行 `icacls … /setintegritylevel Medium` |

预编译好的三个 exe 都在
[Releases](https://github.com/Anicomig/dsh-web-win-launcher-daemon/releases)
的 zip 包里，git 仓库只含源码。

## 已知的 Windows 坑（也是本项目长成这样的原因）

- **WebView2 Runtime** 是配置窗口唯一的外部依赖；`DshConfigHost.exe` 自身是
  60 MB 自包含单文件，不需要另装 .NET 运行时。
- **`.ps1` 必须 UTF-8 带 BOM**。Windows PowerShell 5.1 会把无 BOM 的文件按
  系统 ANSI 代码页解码，中文注释变乱码并破坏 here-string 边界，报出指向错误
  行号的「字符串缺少终止符」。`scripts\README.md` 与 `docs\config-host.md`
  记录了本项目实际踩过的四个坑。
- **托盘 EXE 必须以 Medium 完整性运行**。若编译进程是 Low 完整性，产出的 EXE
  继承该标签，`Shell_NotifyIcon` 会**静默失败**——进程活着、日志干净、图标
  永远不出现。`DshLauncher\build.cmd` 已自动执行
  `icacls dist\DshLauncher.exe /setintegritylevel Medium`。
- **托盘图标必须是经典 DIB 格式**（16/32/48 DIB + 256 PNG）。`csc.exe` 读不了
  纯 PNG 的小尺寸项。详见 `DshLauncher\README.md`「三个必须知道的实现约束」。

## 诊断开关

`ConfigHost\DshConfigHost.exe` 带参数运行可做可复现的自检，都不改变持久状态
（`--page-test-write` 结束后逐字节还原它碰过的文件）：

```cmd
ConfigHost\DshConfigHost.exe --selftest          :: 33 条桥方法检查
ConfigHost\DshConfigHost.exe --page-test         :: 76 条页面内断言（不写文件）
ConfigHost\DshConfigHost.exe --page-test-write   :: 76 + 2 条真实保存回路，事后还原
ConfigHost\DshConfigHost.exe --print-env         :: 打印解析出的安装根、版本、路径
```

## 许可

本项目只是 `dsh web` 的一层看护，不包含 DeepSeek Harness 本身的任何代码。
它所依赖的两个外部运行时——Node.js 与 WebView2——各自遵循其许可证。
