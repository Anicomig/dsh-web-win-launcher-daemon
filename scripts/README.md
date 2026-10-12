# scripts —— 四个入口的实现

根目录的 `Install.cmd` / `Initialization.cmd` / `uninstall.cmd` / `Fixed.cmd` 都只是
**薄包装**（纯 ASCII，避免批处理按控制台代码页解析中文出问题），真正的逻辑在这里。

| 文件 | 干什么 | 会不会改 `DshLauncherDaemon_Config.ps1` |
|---|---|---|
| `Install.ps1` | 修安装位置相关的一切 + 建 `log\` + 生成 `defaults.json` + 跑完整数据校验 | **不会** |
| `Initialization.ps1` | 文件夹被移动/改名后修复绝对路径：`DshLauncher.json`、**三个 `.lnk`**；启动文件夹**已有**同名项才覆盖 | **不会** |
| `Uninstall.ps1` | 三段式：安装数据 / 安装目录 / 用户数据，逐项问，默认全不 | 由你决定 |
| `Fixed.ps1` | 五项修复菜单（重置配置、重建 defaults、**重建快捷方式**、修 JSON、修编码） | **只有它会**整体覆盖，且先备份 |
| `_Common.ps1` | 上面四个共用的函数库 | — |

## 职责边界（重要）

改配置只有两条路：直接编辑 `DshLauncherDaemon_Config.ps1`，或在
`ConfigHost\DshConfigHost.exe` 窗口里改。`Install` 与 `Initialization` **都不碰它**。
整体重置是 `Fixed.cmd` 的职责 —— 这样"装一遍就把我的配置冲掉"这种事不会发生。

默认值的唯一来源是 `ConfigHost\default-config.ps1`：`defaults.json` 和整体重置都从它
推导，两处不再各自维护字段清单。

## 写 `DshLauncher.json` 时必须保留打开方式

那份 JSON 里除了本脚本负责的 `daemonExe` / `webPort`，还有配置窗口写入的
**`openMode` / `edgeAppId`**（「打开方式」）。`Write-JsonConfig` 只管前两个键，
但必须把后两个**原样读出来再写回去** —— 否则 `Install.cmd` 或 `Initialization.cmd`
跑一次就会把用户的打开方式重置成默认浏览器。

改动这个函数后请跑一次 `Install.cmd`，确认 `DshLauncher.json` 里的
`openMode` 没被改成 `browser`。

## 三个快捷方式：谁维护、什么时候

| 快捷方式 | 位置 | 是否"默认既有" | 维护者 |
|---|---|---|---|
| `DshLauncher.lnk` | 安装根目录 | **是，始终存在** | `Install.ps1` / `Initialization.ps1` / `Fixed.ps1`（第 3 项） |
| `LaunchDshLauncherDaemon.lnk` | `autostart\` | 是（源文件） | 同上 |
| `LaunchDshLauncher.lnk` | `autostart\` | 是（源文件） | 同上 |

- 根目录那个指向**托盘 EXE**（`DshLauncher\dist\DshLauncher.exe`，发布版是
  `DshLauncher\DshLauncher.exe`），`WindowStyle = 1`（正常窗口）——
  它是给用户**手动双击**的入口，和自启用那两个（`WindowStyle = 7` 最小化 + VBS 静默）不同。
- 三个都由 `Write-AutostartShortcut` 重建，唯一区别是 `-WindowStyle` 参数。
- `Test-InstallIntegrity` 会把 `DshLauncher.lnk` 当必备项检查：
  缺失 → `FAIL`，指向非托盘 EXE → `WARN`，都会提示跑 `Initialization.cmd`。
- 卸载时它随「安装目录的内容」一起删（它在安装目录里），
  `Uninstall.ps1` 的描述里会点明这一点，免得用户以为只是删了个空壳。

## 约定

- **所有 `.ps1` 必须是 UTF-8 带 BOM**。Windows PowerShell 5.1 读无 BOM 的文件时按
  本机 ANSI（中文机器是 GBK）解码，中文注释会变乱码并**破坏字符串/here-string 的
  边界**，报出指向错误行号的"字符串缺少终止符"这种误导性错误。
- 用编辑器/工具改完 `.ps1` 后**务必复查首三字节是 `EF BB BF`**：
  有些编辑器与自动化工具保存时会悄悄去掉 BOM。
- **文件开头的说明请用 `#` 行注释，不要用 `<# ... #>` 块注释。**
  块注释依赖**第一个字符**是 `<#`；一旦那个标记丢了（改文件的工具吃掉首行时就会），
  整段说明会变成裸文本，PowerShell 把每行的第一个词当命令名 →
  运行期报 "`'========='` 不是 cmdlet"，而**语法解析器却报 0 错误**
  （裸词在 PowerShell 里是合法的命令调用）。这个坑在本项目里中过 4 次。
- **判断 `.ps1` 有没有坏，不能只看 `Parser::ParseFile` 的错误数**（见上一条），
  要真正点载一次：`powershell -NoProfile -Command ". '<路径>'"`
- 不注册任何协议、不写注册表（清理遗留的 `dshlauncher` 键除外，那是给老用户擦尾巴）。
- 不申请管理员权限。
- 任何一步失败都不静默：收集到 `$script:Problems`，最后统一汇报。

## 测试用的开关

普通用户不需要，但值得知道它们存在（这也是这些脚本能被验证的原因）：

| 脚本 | 开关 | 作用 |
|---|---|---|
| 全部 | `-Root <路径>` | 对指定目录操作 |
| `Uninstall.ps1` | `-DryRun` | 只打印要做什么，一个文件不动 |
| `Uninstall.ps1` | `-Answer1/2/3 yes\|no` | 预先给定三段的答案 |
| `Uninstall.ps1` | `-ForceDeleteDir` | 跳过确认词（**仅测试用**，双击时仍需手输） |
| `Uninstall.ps1` | `-StartupDir` / `-LocalAppDataDir` | 注入假目录，避免测试动到真实机器 |
| `Fixed.ps1` | `-DryRun` | 同上，只演示 |
| 四个 `.cmd` | 环境变量 `DSH_NO_PAUSE=1` | 结尾不 pause。**故意不用命令行参数**：包装器 `%*` 会把哨兵一起传给 `.ps1`，PowerShell 会当成未知参数报错 |
