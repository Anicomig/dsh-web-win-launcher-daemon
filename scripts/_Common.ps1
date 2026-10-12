#     _Common.ps1 - Install / Initialization / uninstall / Fixed 共用的函数库
#
#     这个文件不直接运行，由同目录下四个脚本用 dot-source 载入。
#
#     职责边界（重要）：
#       - Install / Initialization 都**不修改** DshLauncherDaemon_Config.ps1。
#         改配置只有两条路：用户手改，或在 DshConfigHost 窗口里改。
#       - 只有 Fixed.cmd 会整体覆盖那个文件（先备份）。
#
#     设计要点
#     --------
#     1. 所有路径都从「脚本自身位置」推导（调用方传 $PSScriptRoot 进来），
#        因此整个文件夹可以随便移动或改名。
#     2. 写文件一律用 UTF-8：JSON 写「无 BOM」（托盘解析器按无 BOM 读），
#        Config.ps1 写「带 BOM」（Windows PowerShell 5.1 读中文必须带 BOM）。
#     3. 任何一步失败都不静默：收集到 $script:Problems 里，最后统一汇报。

Set-StrictMode -Version 2.0

# 文件名常量
$script:DaemonExeName = 'DshLauncherDaemon.exe'
$script:ConfigPs1Name = 'DshLauncherDaemon_Config.ps1'
$script:JsonName      = 'DshLauncher.json'
$script:ShortcutName  = 'LaunchDshLauncherDaemon.lnk'   # 守护进程自启（经 VBS 静默启动）
$script:TrayShortcutName = 'LaunchDshLauncher.lnk'      # 托盘自启（直接指 exe）
# 安装根目录里的「手动启动托盘」快捷方式，始终存在
$script:LauncherShortcutName = 'DshLauncher.lnk'
$script:ConfigHostExeName = 'DshConfigHost.exe'
$script:VbsName       = 'LaunchDshLauncherDaemon.vbs'
$script:DefaultPort   = 3080

$script:Problems = New-Object System.Collections.ArrayList

function Add-Problem {
    param([string] $Message)
    [void]$script:Problems.Add($Message)
}

function Get-Problems {
    # 逗号运算符阻止 PowerShell 展平集合：空 ArrayList 直接 return 会变成 $null。
    return ,$script:Problems
}

function Clear-Problems {
    $script:Problems.Clear()
}

# --------------------------------------------------------------------------
# 路径推导
# --------------------------------------------------------------------------

# 安装根目录 = scripts 文件夹的上一级。
#
# 调用方必须把 scripts 目录传进来（用 $PSScriptRoot）：在函数内部
# $MyInvocation.MyCommand 指向的是「函数」而不是「脚本」，拿不到 .Path。
function Get-InstallRoot {
    param(
        [Parameter(Mandatory=$true)][string] $ScriptsDir,
        [string] $Override
    )

    if ($Override) {
        if (-not (Test-Path -LiteralPath $Override)) {
            throw "指定的根目录不存在: $Override"
        }
        return (Resolve-Path -LiteralPath $Override).Path.TrimEnd('\')
    }

    if (-not (Test-Path -LiteralPath $ScriptsDir)) {
        throw "找不到 scripts 目录: $ScriptsDir"
    }

    $scriptsFull = (Resolve-Path -LiteralPath $ScriptsDir).Path.TrimEnd('\')
    return (Split-Path -Parent $scriptsFull).TrimEnd('\')
}

# 托盘 EXE 的位置在两版里不同：
#   安装版 / 发布副本： <Root>\DshLauncher\DshLauncher.exe
#   项目源码树：        <Root>\DshLauncher\dist\DshLauncher.exe   （EXE 是 build.cmd 的产物）
# 优先取前者；不存在则回退后者。这样同一份脚本在项目和安装版都能跑，
# 而安装版的行为完全不变。
function Resolve-TrayExe {
    param([Parameter(Mandatory=$true)][string] $TrayDir)

    $flat = Join-Path $TrayDir 'DshLauncher.exe'
    if (Test-Path -LiteralPath $flat -PathType Leaf) { return $flat }
    return (Join-Path (Join-Path $TrayDir 'dist') 'DshLauncher.exe')
}


# 写 ConfigHost\defaults.json —— 给配置窗口的「恢复默认」按钮用。
#
# 页面点了「恢复默认」后，会把这里给出的**建议默认配置**填进表单，
# 由用户确认后再点「保存」写盘 —— 绝不直接覆盖 Config.ps1。
#
# 值与「Install.cmd」用的是同一套探测逻辑（Find-NodeExe / Find-DshCli、
# 用户目录下的 .dsh 与 Documents\workspace、默认端口），保证两边一致。
#
# 与 paths.js 一样：这个文件是**机器相关**的，同步到发布副本时会被跳过，
# 由目标机自己的初始化脚本生成。
# 生成 ConfigHost\defaults.json —— 供 DshConfigHost 的「恢复默认」读取。
#
# 与旧的 Write-DefaultsJs 是同一份数据源（Find-NodeExe / Find-DshCli /
# $script:DefaultPort），只是输出格式从 JS 常量改成纯 JSON：
# DshConfigHost 是原生进程，读 JSON 比解析 JS 干净得多。
# 刻意用 ConvertTo-Json 生成，反斜杠转义交给它，不手工拼字符串。

# 返回安装内的标准路径集合。
function Get-Layout {
    param([Parameter(Mandatory=$true)][string] $Root)

    $trayDir = Join-Path $Root 'DshLauncher'
    $autoDir = Join-Path $Root 'autostart'

    return [PSCustomObject]@{
        Root         = $Root
        DaemonExe    = Join-Path $Root $script:DaemonExeName
        ConfigPs1    = Join-Path $Root $script:ConfigPs1Name
        LogDir       = Join-Path $Root 'log'
        TrayDir      = $trayDir
        TrayExe      = Resolve-TrayExe -TrayDir $trayDir
        Json         = Join-Path $trayDir $script:JsonName
        AutostartDir = $autoDir
        Vbs          = Join-Path $autoDir $script:VbsName
        Shortcut     = Join-Path $autoDir $script:ShortcutName
        TrayShortcut = Join-Path $autoDir $script:TrayShortcutName
        ConfigHostDir  = Join-Path $Root 'ConfigHost'
        ConfigHostExe  = Join-Path (Join-Path $Root 'ConfigHost') $script:ConfigHostExeName
        WwwRoot        = Join-Path (Join-Path $Root 'ConfigHost') 'wwwroot'
        Template       = Join-Path (Join-Path $Root 'ConfigHost') 'default-config.ps1'
        DefaultsJson   = Join-Path (Join-Path $Root 'ConfigHost') 'defaults.json'
        BackupDir    = Join-Path $Root '_backup'
    }
}

# --------------------------------------------------------------------------
# 端口 / 配置读取
# --------------------------------------------------------------------------

# 从 Config.ps1 里正则取 $CONFIG_PORT，取不到返回 $null。不执行该脚本。
function Read-PortFromConfigPs1 {
    param([string] $ConfigPs1Path)

    if (-not (Test-Path -LiteralPath $ConfigPs1Path)) { return $null }

    $text = Get-Content -LiteralPath $ConfigPs1Path -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if (-not $text) { return $null }

    $m = [regex]::Match($text, '\$CONFIG_PORT\s*=\s*(\d+)')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return $null
}

# 决定写进 JSON 的端口：现有 JSON > Config.ps1 > 默认值。
function Resolve-Port {
    param(
        [string] $JsonPath,
        [string] $ConfigPs1Path
    )

    if (Test-Path -LiteralPath $JsonPath) {
        try {
            $raw = Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8
            $m = [regex]::Match($raw, '"webPort"\s*:\s*(\d+)')
            if ($m.Success) { return [int]$m.Groups[1].Value }
        } catch { }
    }

    $fromPs1 = Read-PortFromConfigPs1 -ConfigPs1Path $ConfigPs1Path
    if ($fromPs1) { return $fromPs1 }

    return $script:DefaultPort
}

# --------------------------------------------------------------------------
# 写文件
# --------------------------------------------------------------------------

function Write-Utf8NoBom {
    param([string] $Path, [string] $Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

function Write-Utf8Bom {
    param([string] $Path, [string] $Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $enc = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

# 写 DshLauncher.json。路径按 Windows 习惯书写，反斜杠转义为 \\。
#
# 注意：这份 JSON 里除了 daemonExe/webPort，还有「打开方式」的 openMode 与
# edgeAppId（由 DshConfigHost 写入）。这两个键不属于本函数的职责，但**必须原样
# 保留** —— 否则 Install / Initialization 跑一次就会把用户的打开方式重置掉。
function Write-JsonConfig {
    param(
        [string] $JsonPath,
        [string] $DaemonExePath,
        [int]    $Port
    )

    $escaped = $DaemonExePath.Replace('\', '\\')
    $extra = ''

    # 读旧文件，尽量保住 openMode / edgeAppId
    $openMode = 'browser'
    $edgeAppId = ''
    if (Test-Path -LiteralPath $JsonPath) {
        try {
            $old = [System.IO.File]::ReadAllText($JsonPath, [System.Text.Encoding]::UTF8)
            $m = [regex]::Match($old, '"openMode"\s*:\s*"([^"]*)"')
            if ($m.Success -and $m.Groups[1].Value -in @('browser','app')) { $openMode = $m.Groups[1].Value }
            $a = [regex]::Match($old, '"edgeAppId"\s*:\s*"([^"]*)"')
            if ($a.Success -and $a.Groups[1].Value -match '^[a-p]{32}$') { $edgeAppId = $a.Groups[1].Value }
        } catch { }
    }
    $extra = ",`r`n  `"openMode`": `"$openMode`",`r`n  `"edgeAppId`": `"$edgeAppId`""

    $json = "{`r`n  `"daemonExe`": `"$escaped`",`r`n  `"webPort`": $Port$extra`r`n}`r`n"
    Write-Utf8NoBom -Path $JsonPath -Text $json
}

# 重建开机自启快捷方式，指向同目录下的 VBS。
function Write-AutostartShortcut {
    param(
        [string] $ShortcutPath,
        [string] $TargetPath,
        [string] $WorkingDirectory,
        [string] $IconPath,
        [string] $Description = 'DSH Web Launcher Daemon (autostart, no install)',
        # 7 = 最小化（自启时配合 VBS 完全无感）；根目录那个用户手动双击的入口用 1（正常）
        [int]    $WindowStyle = 7
    )

    $dir = Split-Path -Parent $ShortcutPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($ShortcutPath)
    $lnk.TargetPath       = $TargetPath
    $lnk.Arguments        = ''
    $lnk.WorkingDirectory = $WorkingDirectory
    $lnk.IconLocation     = "$IconPath,0"
    $lnk.WindowStyle      = $WindowStyle
    $lnk.Description      = $Description
    $lnk.Save()
}

# --------------------------------------------------------------------------
# 自动探测（Install.cmd 与 Fixed.cmd 用）
# --------------------------------------------------------------------------

# 找 node.exe：先 PATH，再常见安装位置。
function Find-NodeExe {
    try {
        $cmd = Get-Command node.exe -ErrorAction SilentlyContinue
        if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source)) { return $cmd.Source }
    } catch { }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'nodejs\node.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'nodejs\node.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\nodejs\node.exe'),
        'D:\Program Files\nodejs\node.exe',
        'C:\Program Files\nodejs\node.exe'
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

# 找 dsh CLI 的 bin.js：先 AppData 惯例位置，再 npm 全局根。
function Find-DshCli {
    $candidates = New-Object System.Collections.ArrayList
    if ($env:APPDATA) {
        [void]$candidates.Add((Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh\lib\bin.js'))
    }

    try {
        $npmRoot = & npm root -g 2>$null
        if ($LASTEXITCODE -eq 0 -and $npmRoot) {
            [void]$candidates.Add((Join-Path ($npmRoot.Trim()) '@deepseek-ai\dsh\lib\bin.js'))
        }
    } catch { }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

# 生成 Config.ps1 的默认内容。

function Backup-File {
    param([string] $Path, [string] $BackupRoot)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }

    if (-not (Test-Path -LiteralPath $BackupRoot)) {
        New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dest = Join-Path $BackupRoot ("{0}.{1}.bak" -f (Split-Path -Leaf $Path), $stamp)
    Copy-Item -LiteralPath $Path -Destination $dest -Force
    return $dest
}

# --------------------------------------------------------------------------
# 迁移：修正所有依赖「文件夹在哪」的设置
# --------------------------------------------------------------------------

function Invoke-Initialization {
    param(
        [Parameter(Mandatory=$true)] $Layout,
        [string] $StartupDir      # 测试可注入；默认真实启动文件夹
    )

    Write-Host ''
    Write-Host '── 初始化（修正与安装位置相关的东西）──────────────────────────────────────────' -ForegroundColor Cyan
    Write-Host ("   安装根目录: {0}" -f $Layout.Root)

    # 1) daemon 本体
    if (Test-Path -LiteralPath $Layout.DaemonExe) {
        Write-Host '   [1/5] DshLauncherDaemon.exe .......... 存在' -ForegroundColor Green
    } else {
        Write-Host '   [1/5] DshLauncherDaemon.exe .......... 缺失' -ForegroundColor Red
        Add-Problem ("缺少 {0}" -f $Layout.DaemonExe)
    }

    # 2) JSON：把 daemonExe 指向新位置
    $port = Resolve-Port -JsonPath $Layout.Json -ConfigPs1Path $Layout.ConfigPs1
    Write-JsonConfig -JsonPath $Layout.Json -DaemonExePath $Layout.DaemonExe -Port $port
    Write-Host ("   [2/5] DshLauncher.json ............... daemonExe 指向新位置, webPort={0}" -f $port) -ForegroundColor Green

    # 3) 三个快捷方式：TargetPath / WorkingDirectory / IconLocation 全是绝对路径
    #    以前只重建守护进程那一个，托盘那个没人管 —— 换目录后就指向旧位置了。
    Write-Host '   [3/5] 快捷方式 .......................' -ForegroundColor Green
    # 根目录的用户入口（双击启动托盘），始终存在
    Write-LauncherShortcut -Layout $Layout
    # autostart\ 下的两个自启源文件
    Write-AllAutostartShortcuts -Layout $Layout

    # 4) 启动文件夹：已有同名 .lnk 才覆盖，没有就不新增
    Sync-StartupShortcuts -Layout $Layout -StartupDir $StartupDir

    # 5) Config.ps1
    #    存在：只检查端口读得出来。
    #    不存在：从默认模板生成（探测本机四项路径）—— 全新装一台机器时
    #    应当立即可用，而不是再要用户去跑 Fixed.cmd。
    #    这只是「首次安装」的一次性创建；之后 Installation 跑过再跑，
    #    因为有文件了所以走 else 分支，行为不变。
    if (Test-Path -LiteralPath $Layout.ConfigPs1) {
        $p = Read-PortFromConfigPs1 -ConfigPs1Path $Layout.ConfigPs1
        $shown = if ($p) { $p } else { '未读到' }
        Write-Host ("   [5/5] {0} .. 存在 (端口 {1})" -f $script:ConfigPs1Name, $shown) -ForegroundColor Green
    } else {
        $r = New-ConfigFromTemplate -TemplatePath $Layout.Template
        if ($null -eq $r) {
            Write-Host ("   [5/5] {0} .. 缺失且默认模板不可用，请跑 Fixed.cmd" -f $script:ConfigPs1Name) -ForegroundColor Red
            Add-Problem ("缺少 {0}；默认模板也不可读" -f $script:ConfigPs1Name)
        } else {
            Write-Utf8Bom -Path $Layout.ConfigPs1 -Text ($r.Text -replace "`r`n", "`n")
            $det = @('NODE_EXE','DSH_CLI','DSH_HOME','WORKSPACE_DIR') | Where-Object { $r.Undetected -notcontains $_ }
            $undet = $r.Undetected
            $shownDet = if ($det) { '已探测: ' + ($det -join ', ') } else { '' }
            $shownUndet = if ($undet) { '; 未探测: ' + ($undet -join ', ') } else { '' }
            Write-Host ("   [5/5] {0} .. 缺失，已按默认模板生成 ({1}{2})" -f $script:ConfigPs1Name, $shownDet, $shownUndet) -ForegroundColor Green
            if ($undet) {
                Add-Problem ("首次生成 {0} 时未探测到: {1}；请在配置窗口里手动填好后点「保存」" -f $script:ConfigPs1Name, ($undet -join ', '))
            }
        }
    }

    # log 目录
    if (-not (Test-Path -LiteralPath $Layout.LogDir)) {
        New-Item -ItemType Directory -Force -Path $Layout.LogDir | Out-Null
        Write-Host '         已创建日志目录 log\' -ForegroundColor Green
    }
}

# --------------------------------------------------------------------------
# 安装：恢复默认配置
# --------------------------------------------------------------------------

# --------------------------------------------------------------------------
# 汇报
# --------------------------------------------------------------------------

function Show-Summary {
    param([Parameter(Mandatory=$true)] $Layout)

    Write-Host ''
    $problems = $script:Problems
    if ($problems.Count -eq 0) {
        Write-Host '完成，没有问题。' -ForegroundColor Green
    } else {
        Write-Host '完成，但有需要注意的问题：' -ForegroundColor Yellow
        foreach ($p in $problems) { Write-Host ("   - {0}" -f $p) -ForegroundColor Yellow }
    }

    Write-Host ''
    Write-Host '当前布局:' -ForegroundColor DarkGray
    Write-Host ("   daemon        {0}" -f $Layout.DaemonExe) -ForegroundColor DarkGray
    Write-Host ("   托盘          {0}" -f $Layout.TrayExe)   -ForegroundColor DarkGray
    Write-Host ("   配置文件      {0}" -f $Layout.ConfigPs1) -ForegroundColor DarkGray
    Write-Host ("   端口配置      {0}" -f $Layout.Json)      -ForegroundColor DarkGray
    Write-Host ("   自启快捷方式  {0}" -f $Layout.Shortcut)  -ForegroundColor DarkGray
}

# ==========================================================================
# 默认模板（唯一的默认值来源）
# ==========================================================================
#
# ConfigHost\default-config.ps1 是所有「默认值」的唯一出处：
#   - Fixed.cmd 的彻底重置 = 读模板 + 替换机器相关占位符 + 写盘
#   - defaults.json        = 读模板 + 替换 + 解析出 15 个值
# 以前这两处各自维护一份字段清单，加字段必然漂移，现在不会了。

$script:TemplateRelPath  = 'ConfigHost\default-config.ps1'
$script:DefaultsJsonRel  = 'ConfigHost\defaults.json'
$script:TokenPattern     = '__DETECT_([A-Z_]+)_'
$script:MachineKeys      = @('NODE_EXE','DSH_CLI','DSH_HOME','WORKSPACE_DIR')

function Get-TemplatePath {
    param([Parameter(Mandatory=$true)][string] $Root)
    return (Join-Path $Root $script:TemplateRelPath)
}

# 读模板并去掉「只属于模板」的说明块；返回 $null 表示模板不存在
function Get-TemplateRuntimeText {
    param([Parameter(Mandatory=$true)][string] $TemplatePath)

    if (-not (Test-Path -LiteralPath $TemplatePath -PathType Leaf)) { return $null }

    $t = [System.IO.File]::ReadAllText($TemplatePath, [System.Text.Encoding]::UTF8)
    # 整段（含首尾两行标记）删掉；用非贪婪匹配，只删这一处
    $t = [regex]::Replace($t, '(?ms)^.*TEMPLATE-ONLY-BEGIN.*?TEMPLATE-ONLY-END\r?\n?', '')
    return ($t -replace "`r`n", "`n")
}

# 探测本机四项；探测不到的键不出现在结果里（宁可留占位符也不写假路径）
function Get-DetectedMachineValues {
    $h = @{}
    $node = Find-NodeExe
    $cli  = Find-DshCli
    if ($node) { $h['NODE_EXE']     = $node }
    if ($cli)  { $h['DSH_CLI']      = $cli }
    $h['DSH_HOME']     = Join-Path $env:USERPROFILE '.dsh'
    $h['WORKSPACE_DIR'] = Join-Path $env:USERPROFILE 'Documents\workspace'
    return $h
}

# 占位符 -> 探测值。返回 @{ Text=内容; Undetected=@(未探测到的键) }
function New-ConfigFromTemplate {
    param([Parameter(Mandatory=$true)][string] $TemplatePath)

    $t = Get-TemplateRuntimeText -TemplatePath $TemplatePath
    if ($null -eq $t) { return $null }

    $det = Get-DetectedMachineValues
    $undetected = @()

    foreach ($m in [regex]::Matches($t, '__DETECT_([A-Z0-9_]+)__')) {
        $key = $m.Groups[1].Value
        if ($det.ContainsKey($key)) {
            $t = $t.Replace($m.Value, $det[$key])
        } else {
            if ($undetected -notcontains $key) { $undetected += $key }
        }
    }
    return @{ Text = $t; Undetected = $undetected }
}

# 从模板解析出 defaults.json 需要的值表（带类型：int / bool / string）
# EXTRA_ARGS 是数组，DshConfigHost 的「恢复默认」不支持，跳过。
function Get-TemplateConfigValues {
    param([Parameter(Mandatory=$true)][string] $TemplatePath)

    $r = New-ConfigFromTemplate -TemplatePath $TemplatePath
    if ($null -eq $r) { return $null }

    return (Get-ConfigValuesFromText -Text $r.Text -SkipPlaceholders)
}

# 从任意配置文本里解析 $CONFIG_X = 值（带类型）。
# 模板解析和运行时配置校验共用这一份，规则不会两边漂。
function Get-ConfigValuesFromText {
    param(
        [Parameter(Mandatory=$true)][string] $Text,
        [switch] $SkipPlaceholders
    )

    $out = [ordered]@{}
    foreach ($line in ($Text -split "`n")) {
        $m = [regex]::Match($line.TrimEnd(), '^\$CONFIG_([A-Z0-9_]+)[ \t]*=[ \t]*(.+?)[ \t]*(?:#.*)?$')
        if (-not $m.Success) { continue }

        $name = $m.Groups[1].Value
        $raw  = $m.Groups[2].Value.Trim()

        if ($name -eq 'EXTRA_ARGS') { continue }        # 数组，恢复默认不支持
        if ($SkipPlaceholders -and $raw -match '__DETECT_') { continue }

        if     ($raw -eq '$true')       { $out[$name] = $true }
        elseif ($raw -eq '$false')      { $out[$name] = $false }
        elseif ($raw -match '^-?\d+$')  { $out[$name] = [int] $raw }
        elseif ($raw -match '^"(.*)"$') { $out[$name] = $Matches[1] }
        elseif ($raw -match "^'(.*)'$") { $out[$name] = $Matches[1] }
        else { $out[$name] = $raw }
    }
    return $out
}

function Write-DefaultsJsonFromTemplate {
    param([Parameter(Mandatory=$true)][string] $Root)

    $tpl = Get-TemplatePath -Root $Root
    $dir = Split-Path -Parent (Join-Path $Root $script:DefaultsJsonRel)
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        Write-Host '   [页面] 没有 ConfigHost 目录，跳过 defaults.json' -ForegroundColor DarkGray
        return $false
    }
    if (-not (Test-Path -LiteralPath $tpl -PathType Leaf)) {
        Write-Host ("   [页面] 缺少默认模板 {0}，无法生成 defaults.json" -f $script:TemplateRelPath) -ForegroundColor Red
        Add-Problem ("缺少默认模板 " + $script:TemplateRelPath)
        return $false
    }

    $vals = Get-TemplateConfigValues -TemplatePath $tpl
    if ($null -eq $vals -or $vals.Count -eq 0) {
        Write-Host '   [页面] 模板里没解析出任何 CONFIG_* 值' -ForegroundColor Red
        Add-Problem 'defaults.json 生成失败：模板解析为空'
        return $false
    }

    $json = $vals | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText((Join-Path $dir 'defaults.json'), $json,
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("   [页面] defaults.json 已按模板生成（{0} 项）-> $dir" -f $vals.Count) -ForegroundColor Green
    return $true
}

# ==========================================================================
# 开机自启快捷方式：两个都要生成，启动文件夹「已存在才覆盖」
# ==========================================================================

# 返回两个 (名称, 源文件, 目标可执行, 工作目录, 说明) 的表
function Get-AutostartShortcuts {
    param([Parameter(Mandatory=$true)] $Layout)

    $list = @()
    $list += [PSCustomObject]@{
        Name    = $script:ShortcutName
        Src     = $Layout.Shortcut
        Target  = $Layout.Vbs
        WorkDir = $Layout.Root
        Icon    = $Layout.DaemonExe
        Desc    = 'DSH Web Launcher Daemon (autostart, no install)'
    }
    $list += [PSCustomObject]@{
        Name    = $script:TrayShortcutName
        Src     = $Layout.TrayShortcut
        Target  = $Layout.TrayExe
        WorkDir = (Split-Path -Parent $Layout.TrayExe)
        Icon    = $Layout.TrayExe
        Desc    = 'DSH Launcher tray (autostart, no install)'
    }
    return $list
}

# ==========================================================================
# 根目录的启动快捷方式 DshLauncher.lnk
# ==========================================================================
#
# 它是给用户双击启动托盘的入口。与 autostart\ 里那两个不同：
#   - 那两个是「开机自启的源文件」，装不装进启动文件夹由用户决定；
#   - 这个就放在安装根目录，**始终存在**（安装时建、移动后重建）。
# 它指向托盘 EXE，不是 VBS —— 用户手动双击时不该有任何隐藏行为。

function Get-LauncherShortcutPath {
    param([Parameter(Mandatory=$true)] $Layout)
    return (Join-Path $Layout.Root $script:LauncherShortcutName)
}

# 建立/修复根目录的 DshLauncher.lnk。目标就是托盘 EXE。
function Write-LauncherShortcut {
    param([Parameter(Mandatory=$true)] $Layout)

    $target = $Layout.TrayExe
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        Write-Host ("   {0,-30} 跳过（托盘 EXE 不存在：{1}）" -f $script:LauncherShortcutName, $target) -ForegroundColor Yellow
        Add-Problem ("快捷方式目标不存在: " + $target)
        return
    }

    $path = Get-LauncherShortcutPath -Layout $Layout
    Write-AutostartShortcut -ShortcutPath $path -TargetPath $target `
        -WorkingDirectory (Split-Path -Parent $target) -IconPath $target `
        -Description 'DSH Launcher（启动托盘）' -WindowStyle 1

    if (Test-Path -LiteralPath $path) {
        Write-Host ("   {0,-30} 已就绪，指向托盘 EXE" -f $script:LauncherShortcutName) -ForegroundColor Green
    }
}

# 重建 autostart\ 下的两个源快捷方式（绝对路径全部指向当前安装目录）
function Write-AllAutostartShortcuts {
    param([Parameter(Mandatory=$true)] $Layout)

    foreach ($sc in (Get-AutostartShortcuts -Layout $Layout)) {
        if (-not (Test-Path -LiteralPath $sc.Target -PathType Leaf)) {
            Write-Host ("   {0,-30} 跳过（目标不存在：{1}）" -f $sc.Name, $sc.Target) -ForegroundColor Yellow
            Add-Problem ("快捷方式目标不存在: " + $sc.Target)
            continue
        }
        Write-AutostartShortcut -ShortcutPath $sc.Src -TargetPath $sc.Target `
            -WorkingDirectory $sc.WorkDir -IconPath $sc.Icon -Description $sc.Desc
        Write-Host ("   {0,-30} 已重建，指向当前目录" -f $sc.Name) -ForegroundColor Green
    }
}

# 启动文件夹里【已有】同名 .lnk 才覆盖；没有就不新增（用户明确要求）
function Sync-StartupShortcuts {
    param(
        [Parameter(Mandatory=$true)] $Layout,
        # 默认用真实启动文件夹；测试时可注入别处，避免动到用户机器
        [string] $StartupDir
    )

    $sd = $StartupDir
    if (-not $sd) { $sd = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup) }
    Write-Host "   [4/5] 启动文件夹同步 ................" -ForegroundColor Green
    Write-Host ("         位置: {0}" -f $sd) -ForegroundColor DarkGray

    foreach ($sc in (Get-AutostartShortcuts -Layout $Layout)) {
        $dst = Join-Path $sd $sc.Name
        if (-not (Test-Path -LiteralPath $dst -PathType Leaf)) {
            Write-Host ("   {0,-30} 启动文件夹里没有，按约定不新增" -f $sc.Name) -ForegroundColor DarkGray
            continue
        }
        if (-not (Test-Path -LiteralPath $sc.Src -PathType Leaf)) {
            Write-Host ("   {0,-30} 启动文件夹里有，但源缺失，未覆盖" -f $sc.Name) -ForegroundColor Red
            Add-Problem ("启动文件夹有 " + $sc.Name + " 但 autostart 源缺失，未覆盖")
            continue
        }
        try {
            Copy-Item -LiteralPath $sc.Src -Destination $dst -Force
            Write-Host ("   {0,-30} 已用新目录的版本覆盖" -f $sc.Name) -ForegroundColor Green
        } catch {
            Write-Host ("   {0,-30} 覆盖失败: {1}" -f $sc.Name, $_.Exception.Message) -ForegroundColor Red
            Add-Problem ("覆盖启动文件夹 " + $sc.Name + " 失败")
        }
    }
}

# ==========================================================================
# 安装体检：Install 收尾调用，Fixed 也可单独调用
# ==========================================================================
# 只读，不修任何东西。返回 Level/Item/Detail 列表，由调用方决定怎么显示。
# Level: OK / WARN / FAIL

function Test-InstallIntegrity {
    param([Parameter(Mandatory=$true)] $Layout)

    $r = @()

    # --- 关键文件在位 ---
    $need = @(
        @{ L='OK';   N='守护进程 DshLauncherDaemon.exe'; P=$Layout.DaemonExe;   Miss='FAIL' },
        @{ L='OK';   N='托盘 DshLauncher.exe';           P=$Layout.TrayExe;     Miss='FAIL' },
        @{ L='OK';   N='DshConfigHost.exe';              P=$Layout.ConfigHostExe; Miss='FAIL' },
        @{ L='OK';   N='页面 index.html';                P=(Join-Path $Layout.WwwRoot 'index.html'); Miss='FAIL' },
        @{ L='OK';   N='页面 app.js';                    P=(Join-Path $Layout.WwwRoot 'app.js');     Miss='FAIL' },
        @{ L='OK';   N='默认模板 default-config.ps1';    P=$Layout.Template;    Miss='FAIL' },
        @{ L='OK';   N='defaults.json';                  P=$Layout.DefaultsJson; Miss='WARN' }
    )
    foreach ($it in $need) {
        if ($it.P -and (Test-Path -LiteralPath $it.P -PathType Leaf)) {
            $sz = (Get-Item -LiteralPath $it.P).Length
            $r += [PSCustomObject]@{ Level='OK'; Item=$it.N; Detail=('{0:N0} B' -f $sz) }
        } else {
            $r += [PSCustomObject]@{ Level=$it.Miss; Item=$it.N; Detail='缺失：' + $it.P }
        }
    }

    # DshConfigHost.exe 缺失是最常见的（它不在版本库里，要自己编译）
    if (-not (Test-Path -LiteralPath $Layout.ConfigHostExe -PathType Leaf)) {
        $r += [PSCustomObject]@{ Level='WARN'; Item='怎么办'; Detail='运行 ConfigHost\build.cmd（需要 .NET SDK）' }
    }

    # --- 运行时配置 ---
    if (-not (Test-Path -LiteralPath $Layout.ConfigPs1 -PathType Leaf)) {
        $r += [PSCustomObject]@{ Level='FAIL'; Item='DshLauncherDaemon_Config.ps1'; Detail='缺失' }
        return $r
    }
    $bytes = [System.IO.File]::ReadAllBytes($Layout.ConfigPs1)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $txt = [System.IO.File]::ReadAllText($Layout.ConfigPs1, [System.Text.Encoding]::UTF8)
    $r += [PSCustomObject]@{
        Level  = $(if ($hasBom) { 'OK' } else { 'WARN' })
        Item   = '配置编码'
        Detail = $(if ($hasBom) { 'UTF-8 带 BOM' } else { '缺 BOM —— Windows PowerShell 5.1 读中文会乱码，可用 Fixed.cmd 修' })
    }
    $cr = ([regex]::Matches($txt, "`r")).Count
    if ($cr -gt 0) {
        $r += [PSCustomObject]@{ Level='WARN'; Item='配置换行'; Detail="含 $cr 个 CR（运行时文件约定纯 LF），可用 Fixed.cmd 归一" }
    }

    $vals = Get-ConfigValuesFromText -Text $txt
    $r += [PSCustomObject]@{ Level='OK'; Item='配置可解析'; Detail=("$($vals.Count) 个 CONFIG_* 值") }

    # 与模板比对字段齐全度
    $tplVals = Get-TemplateConfigValues -TemplatePath $Layout.Template
    if ($null -ne $tplVals) {
        $missing = @()
        foreach ($k in $tplVals.Keys) { if (-not $vals.Contains($k)) { $missing += $k } }
        if ($missing.Count) {
            $r += [PSCustomObject]@{ Level='FAIL'; Item='配置缺字段'; Detail=($missing -join ', ') }
        } else {
            $r += [PSCustomObject]@{ Level='OK'; Item='配置字段齐全'; Detail='与模板一致' }
        }
    }
    if ($txt -match '__DETECT_') {
        $r += [PSCustomObject]@{ Level='FAIL'; Item='配置含未替换占位符'; Detail='重置过但探测失败，请填真实路径' }
    }

    # 数值合法性（与 DshConfigHost 页面里的规则保持一致）
    $port = $vals['PORT']
    if ($port -is [int] -and ($port -lt 1 -or $port -gt 65535)) {
        $r += [PSCustomObject]@{ Level='FAIL'; Item='端口'; Detail="$port 不在 1-65535" }
    } elseif ($port) {
        $r += [PSCustomObject]@{ Level='OK'; Item='端口'; Detail="$port" }
    }
    $grace = $vals['LAUNCH_GRACE_SEC']; $hard = $vals['STARTUP_TIMEOUT_SEC']
    if ($grace -is [int] -and $hard -is [int] -and $grace -ge $hard) {
        $r += [PSCustomObject]@{ Level='WARN'; Item='宽限期/硬超时'; Detail="宽限期 $grace 须小于硬超时 $hard，否则端口永远等不到判死" }
    }
    foreach ($iv in @('PORT_CHECK_INTERVAL','PROC_CHECK_INTERVAL','DAEMON_PORT_ONLY_INTERVAL')) {
        if ($vals[$iv] -is [int] -and $vals[$iv] -eq 0) {
            $r += [PSCustomObject]@{ Level='FAIL'; Item="间隔 $iv"; Detail='为 0 会让主循环空转（实测日志 1 分钟涨 1.2 MB）' }
        }
    }

    # 四个路径是否指向真实存在的东西
    foreach ($k in @('NODE_EXE','DSH_CLI','DSH_HOME','WORKSPACE_DIR')) {
        $v = [string] $vals[$k]
        if (-not $v) { $r += [PSCustomObject]@{ Level='WARN'; Item="路径 $k"; Detail='为空' }; continue }
        if (Test-Path -LiteralPath $v) {
            $r += [PSCustomObject]@{ Level='OK'; Item="路径 $k"; Detail='存在' }
        } else {
            $r += [PSCustomObject]@{ Level='WARN'; Item="路径 $k"; Detail="不存在：$v" }
        }
    }

    # --- 托盘 JSON ---
    if (Test-Path -LiteralPath $Layout.Json -PathType Leaf) {
        $jt = [System.IO.File]::ReadAllText($Layout.Json, [System.Text.Encoding]::UTF8)
        $de = ([regex]::Match($jt, '"daemonExe"\s*:\s*"((?:\\.|[^"\\])*)"')).Groups[1].Value
        $wp = ([regex]::Match($jt, '"webPort"\s*:\s*(\d+)')).Groups[1].Value
        if ($de -and ($de -replace '\\\\','\') -eq $Layout.DaemonExe) {
            $r += [PSCustomObject]@{ Level='OK'; Item='DshLauncher.json daemonExe'; Detail='指向本目录' }
        } else {
            $r += [PSCustomObject]@{ Level='FAIL'; Item='DshLauncher.json daemonExe'; Detail="指向别处：$de（应为 $($Layout.DaemonExe)）→ 运行 Initialization.cmd" }
        }
        if ($wp -and $port -and ([int]$wp -ne [int]$port)) {
            $r += [PSCustomObject]@{ Level='WARN'; Item='端口不一致'; Detail="托盘 webPort=$wp 而配置 CONFIG_PORT=$port" }
        }
    } else {
        $r += [PSCustomObject]@{ Level='FAIL'; Item='DshLauncher.json'; Detail='缺失' }
    }

    # --- 快捷方式：目标必须落在本安装目录内 ---
    try {
        $shell = New-Object -ComObject WScript.Shell

        # 根目录那个「双击启动托盘」的入口：属于安装必备项，缺失或指错都要报
        $lnkPath = Get-LauncherShortcutPath -Layout $Layout
        if (-not (Test-Path -LiteralPath $lnkPath -PathType Leaf)) {
            $r += [PSCustomObject]@{ Level='FAIL'; Item=$script:LauncherShortcutName
                Detail='缺失（双击启动托盘的入口）→ 运行 Initialization.cmd' }
        } else {
            $lt = $shell.CreateShortcut($lnkPath).TargetPath
            if (-not $lt -or -not (Test-Path -LiteralPath $lt)) {
                $r += [PSCustomObject]@{ Level='FAIL'; Item=$script:LauncherShortcutName
                    Detail="目标不存在：$lt → 运行 Initialization.cmd" }
            } elseif ($lt -ne $Layout.TrayExe) {
                $r += [PSCustomObject]@{ Level='WARN'; Item=$script:LauncherShortcutName
                    Detail="指向 $lt（期望托盘 EXE）→ 运行 Initialization.cmd 校正" }
            } else {
                $r += [PSCustomObject]@{ Level='OK'; Item=$script:LauncherShortcutName; Detail='指向托盘 EXE' }
            }
        }

        # 两个自启快捷方式（autostart\ 里的源文件）
        foreach ($sc in (Get-AutostartShortcuts -Layout $Layout)) {
            if (-not (Test-Path -LiteralPath $sc.Src -PathType Leaf)) {
                $r += [PSCustomObject]@{ Level='FAIL'; Item=$sc.Name; Detail='源文件缺失 → 运行 Initialization.cmd' }
                continue
            }
            $tp = $shell.CreateShortcut($sc.Src).TargetPath
            if (-not $tp -or -not (Test-Path -LiteralPath $tp)) {
                $r += [PSCustomObject]@{ Level='FAIL'; Item=$sc.Name; Detail="目标不存在：$tp → 运行 Initialization.cmd" }
            } elseif (-not $tp.StartsWith($Layout.Root, [StringComparison]::OrdinalIgnoreCase)) {
                $r += [PSCustomObject]@{ Level='FAIL'; Item=$sc.Name; Detail="指向安装目录之外：$tp → 运行 Initialization.cmd" }
            } else {
                $r += [PSCustomObject]@{ Level='OK'; Item=$sc.Name; Detail='目标在本目录内' }
            }
        }
    } catch {
        $r += [PSCustomObject]@{ Level='WARN'; Item='快捷方式检查'; Detail='读不了 .lnk（' + $_.Exception.Message + '）' }
    }

    # --- WebView2 Runtime：DshConfigHost 的前置 ---
    # 先查文件系统（能拿到版本号，也不依赖 GUID 有没有变），再退回注册表。
    # 实测本机：EdgeWebView\Application\154.0.4258.62\msedgewebview2.exe
    #           注册表 HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients
    #             \{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5} 的 pv 同值
    $ver = $null
    foreach ($dir in @("${env:ProgramFiles(x86)}\Microsoft\EdgeWebView\Application",
                       "$env:ProgramFiles\Microsoft\EdgeWebView\Application")) {
        if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { continue }
        foreach ($v in (Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue)) {
            if (Test-Path -LiteralPath (Join-Path $v.FullName 'msedgewebview2.exe') -PathType Leaf) {
                $ver = $v.Name; break
            }
        }
        if ($ver) { break }
    }
    if (-not $ver) {
        foreach ($rk in ('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
                         'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
                         'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')) {
            if (Test-Path $rk) {
                $pv = (Get-ItemProperty -Path $rk -Name pv -ErrorAction SilentlyContinue).pv
                if ($pv) { $ver = $pv; break }
            }
        }
    }
    if ($ver) { $r += [PSCustomObject]@{ Level='OK'; Item='WebView2 Runtime'; Detail="已安装 $ver" } }
    else { $r += [PSCustomObject]@{ Level='WARN'; Item='WebView2 Runtime'; Detail='没找到，DshConfigHost 可能打不开（Win11 自带，Win10 随 Edge 安装）' } }

    # --- 遗留协议注册（信息性）---
    if (Test-Path 'HKCU:\Software\Classes\dshlauncher') {
        $r += [PSCustomObject]@{ Level='WARN'; Item='遗留协议注册'; Detail='HKCU\Software\Classes\dshlauncher 仍在，可用 uninstall.cmd 清理' }
    }
    return $r
}
# 把体检结果按级别打印并计数
function Show-Integrity {
    param([Parameter(Mandatory=$true)] $Layout)

    Write-Host ''
    Write-Host '── 数据校验 ────────────────────────────────────────────' -ForegroundColor Cyan
    $r = Test-InstallIntegrity -Layout $Layout
    foreach ($x in $r) {
        $col = switch ($x.Level) { 'OK' { 'Green' } 'WARN' { 'Yellow' } default { 'Red' } }
        $mark = switch ($x.Level) { 'OK' { '  ok ' } 'WARN' { 'WARN' } default { 'FAIL' } }
        Write-Host ("   [{0}] {1,-30} {2}" -f $mark, $x.Item, $x.Detail) -ForegroundColor $col
    }
    $nf = @($r | Where-Object { $_.Level -eq 'FAIL' }).Count
    $nw = @($r | Where-Object { $_.Level -eq 'WARN' }).Count
    Write-Host ''
    if ($nf -gt 0) {
        Write-Host ("   有 $nf 项不通过。多数可用 Fixed.cmd 修复；缺 DshConfigHost.exe 请先运行 ConfigHost\build.cmd。") -ForegroundColor Red
    } elseif ($nw -gt 0) {
        Write-Host ("   全部关键项通过，$nw 项提醒。") -ForegroundColor Yellow
    } else {
        Write-Host '   全部通过。' -ForegroundColor Green
    }
    return $r
}