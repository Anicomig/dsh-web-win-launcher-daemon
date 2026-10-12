<#
    Install.ps1 - 安装（发布版与项目版通用）

    做什么
    ------
      1. 修正与「安装位置」相关的东西：DshLauncher.json 的 daemonExe、
         autostart\ 下两个 .lnk 源文件（守护进程 + 托盘）
      2. 启动文件夹里【已有】同名 .lnk 时，用新目录的版本覆盖它；
         没有就什么都不加（不会擅自往启动文件夹塞东西）
      3. 创建 log 目录
      4. 由 ConfigHost\default-config.ps1 推导并写入 ConfigHost\defaults.json
         （DshConfigHost 的「恢复默认」用它）
      5. 做一次完整数据校验并报告

    不做什么
    --------
      - **不修改 DshLauncherDaemon_Config.ps1**。改配置只有两条路：
        用户直接编辑该文件，或在 DshConfigHost 窗口里改。
        整体重置是 Fixed.cmd 的职责。
      - 不注册任何协议、不写注册表（DshConfigHost 是本地程序，不需要）
      - 不申请管理员权限，不会弹 UAC

    用法
    ----
    双击 ..\Install.cmd，或者：
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Install.ps1
        powershell ... -File scripts\Install.ps1 -Root "D:\somewhere\dsh-web-launcher-daemon"
#>

[CmdletBinding()]
param(
    [string] $Root
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_Common.ps1')

try {
    Clear-Problems
    $root   = Get-InstallRoot -ScriptsDir $PSScriptRoot -Override $Root
    $layout = Get-Layout -Root $root

    Write-Host ''
    Write-Host '=====================================================' -ForegroundColor White
    Write-Host '  DSH Web Launcher Daemon - 安装' -ForegroundColor White
    Write-Host '=====================================================' -ForegroundColor White

    Invoke-Initialization -Layout $layout

    # defaults.json：只依赖默认模板与本机探测，不碰运行时配置
    [void](Write-DefaultsJsonFromTemplate -Root $layout.Root)

    Show-Summary -Layout $layout

    $r  = Show-Integrity -Layout $layout
    $nf = @($r | Where-Object { $_.Level -eq 'FAIL' }).Count

    Write-Host ''
    Write-Host '下一步:' -ForegroundColor White
    Write-Host ("   1. 双击启动托盘: {0}" -f $layout.TrayExe) -ForegroundColor Gray
    Write-Host      '      托盘图标在通知区（可能在折叠的 ^ 里），右键就是菜单' -ForegroundColor DarkGray
    Write-Host      '   2. 改配置：托盘右键 →「配置」，打开 DshConfigHost 窗口' -ForegroundColor Gray
    Write-Host      '      也可以直接编辑 DshLauncherDaemon_Config.ps1（改完需重启守护进程）' -ForegroundColor DarkGray
    Write-Host      '   3. 需要开机自启：在配置窗口的「开机启动」里拨开关' -ForegroundColor Gray
    Write-Host      '      改坏了想恢复：运行 Fixed.cmd' -ForegroundColor Gray
    if ($script:Problems.Count -gt 0) {
        Write-Host ''
        Write-Host '注意：' -ForegroundColor Yellow
        foreach ($p in $script:Problems) { Write-Host ("   - {0}" -f $p) -ForegroundColor Yellow }
    }
    Write-Host ''

    if ($nf -gt 0) { exit 1 }
    exit 0
}
catch {
    Write-Host ''
    Write-Host '安装失败：' -ForegroundColor Red
    Write-Host ("   {0}" -f $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    exit 1
}
