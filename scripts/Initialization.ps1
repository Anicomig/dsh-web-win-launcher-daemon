<#
    Initialization.ps1 - 初始化（用于「安装文件夹被移动/复制之后」）

    目的只有一个
    ------------
    把整个安装文件夹挪到新位置（或改了名）之后运行一次，让所有
    「写死了绝对路径」的东西重新指向新位置，从而恢复正常使用：

      1. DshLauncher\DshLauncher.json   ->  daemonExe 指向新位置
      2. autostart\LaunchDshLauncherDaemon.lnk  ->  三个路径字段重建
         autostart\LaunchDshLauncher.lnk        ->  同上（托盘那个）
      3. 启动文件夹里【已经存在】同名 .lnk 时，用新目录的版本覆盖；
         不存在就绝不新增 —— 是否开机自启由用户在配置窗口里决定

    不做什么
    --------
      - **不修改 DshLauncherDaemon_Config.ps1**。里面四项路径是
        「机器/用户相关」而不是「文件夹位置相关」，换机器请用 Install.cmd。
      - 不生成 defaults.json（它不含安装目录相关的路径，移动后仍然有效）
      - 不注册协议、不写注册表、不碰管理员权限

    用法
    ----
    双击 ..\Initialization.cmd，或者：
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Initialization.ps1
        powershell ... -File scripts\Initialization.ps1 -Root "D:\somewhere\dsh-web-launcher-daemon"
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
    Write-Host '  DSH Web Launcher Daemon - 初始化（移动目录后修复路径）' -ForegroundColor White
    Write-Host '=====================================================' -ForegroundColor White

    Invoke-Initialization -Layout $layout

    Show-Summary -Layout $layout

    Write-Host ''
    Write-Host '提示：如果之后还要手动启动，直接运行:' -ForegroundColor DarkGray
    Write-Host ("   {0}" -f $layout.TrayExe) -ForegroundColor DarkGray
    Write-Host '      改配置请托盘右键 →「配置」（DshConfigHost 窗口）' -ForegroundColor DarkGray
    Write-Host ''

    exit 0
}
catch {
    Write-Host ''
    Write-Host '初始化失败：' -ForegroundColor Red
    Write-Host ("   {0}" -f $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    exit 1
}
