#     Fixed.ps1 - 修复（专门处理「用户改坏了」的情况）
#
#     能修什么
#     --------
#       [1] 彻底重置 DshLauncherDaemon_Config.ps1
#             用 ConfigHost\default-config.ps1 整体覆盖，四项机器相关路径
#             重新探测本机写入。会先备份原文件。
#             —— 这是唯一会整体覆盖配置的入口，Install / Initialization 都不碰它。
#       [2] 重建 ConfigHost\defaults.json（由默认模板推导）
#       [3] 重建 autostart\ 下两个 .lnk 源文件（绝对路径指向当前目录）
#       [4] 修复 DshLauncher\DshLauncher.json（daemonExe / webPort）
#       [5] 修复配置文件的编码与换行（统一 UTF-8 带 BOM + 纯 LF，不改任何值）
#
#     安全约定
#     --------
#       · 每一项动手前都把受影响文件复制到 _backup\<时间戳>-fixed\
#       · 不申请管理员权限，不写注册表
#       · 结束跑一次完整体检，把还遗留的问题列出来
#
#     用法
#     ----
#     双击 ..\Fixed.cmd（菜单式，可多选），或者：
#         powershell ... -File scripts\Fixed.ps1 -All
#         powershell ... -File scripts\Fixed.ps1 -Fix 1,3   （或直接 Fixed.cmd 1,5）
#         powershell ... -File scripts\Fixed.ps1 -Fix 1 -DryRun

[CmdletBinding()]
param(
    # 位置 0：让用户可以直接敲 Fixed.cmd 1 或 Fixed.cmd 1,5。
    # 故意收字符串自己拆：命令行传过来的 "2,3" 是一个字符串，交给 [int[]]
    # 绑定器会静默变成空集合，于是脚本什么都不做还退出 0。
    [Parameter(Position=0)][string] $Fix,
    # cmd 把逗号当参数分隔符，所以 "2,3" 会变成两个独立实参；这里把它们收拢回来
    [Parameter(ValueFromRemainingArguments=$true)][string[]] $Rest,
    [string]  $Root,
    [switch]  $All,
    [switch]  $DryRun
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_Common.ps1')

function Write-FixedFile {
    param([string] $Path, [string] $Text, [byte[]] $Bytes, [string] $Label)

    if ($DryRun) {
        Write-Host ("      [DryRun] 将写入 {0}" -f $Path) -ForegroundColor Magenta
        return
    }
    if ($Bytes) { [System.IO.File]::WriteAllBytes($Path, $Bytes) }
    else { Write-Utf8Bom -Path $Path -Text $Text }
    Write-Host ("      [OK] {0}" -f $Label) -ForegroundColor Green
}

function Fix-ResetConfig {
    param($Layout, [string] $BackupRoot)

    Write-Host ''
    Write-Host '  [1] 彻底重置 DshLauncherDaemon_Config.ps1' -ForegroundColor White
    if (-not (Test-Path -LiteralPath $Layout.Template -PathType Leaf)) {
        Write-Host ("      缺少默认模板 {0}，无法重置" -f $script:TemplateRelPath) -ForegroundColor Red
        Add-Problem '缺少默认模板，重置失败'
        return
    }
    $r = New-ConfigFromTemplate -TemplatePath $Layout.Template
    if ($null -eq $r) { Write-Host '      模板读取失败' -ForegroundColor Red; return }

    if ($r.Undetected.Count) {
        Write-Host ('      这几项没探测到，保留占位符，请手动填写：{0}' -f ($r.Undetected -join ', ')) -ForegroundColor Yellow
        Add-Problem ('未探测到: ' + ($r.Undetected -join ', '))
    }
    Backup-File -Path $Layout.ConfigPs1 -BackupRoot $BackupRoot | Out-Null
    Write-FixedFile -Path $Layout.ConfigPs1 -Text $r.Text -Label '配置已按默认模板重置'
}

function Fix-DefaultsJson {
    param($Layout)
    Write-Host ''
    Write-Host '  [2] 重建 ConfigHost\defaults.json' -ForegroundColor White
    if ($DryRun) { Write-Host '      [DryRun] 跳过' -ForegroundColor Magenta; return }
    [void](Write-DefaultsJsonFromTemplate -Root $Layout.Root)
}

function Fix-Shortcuts {
    param($Layout, [string] $BackupRoot)
    Write-Host ''
    Write-Host '  [3] 重建快捷方式（根目录入口 + autostart 两个源文件）' -ForegroundColor White
    # 根目录那个是用户双击启动托盘的入口，属于安装必备项
    $lnkPath = Get-LauncherShortcutPath -Layout $Layout
    if (Test-Path -LiteralPath $lnkPath -PathType Leaf) {
        Backup-File -Path $lnkPath -BackupRoot $BackupRoot | Out-Null
    }
    foreach ($sc in (Get-AutostartShortcuts -Layout $Layout)) {
        if (Test-Path -LiteralPath $sc.Src -PathType Leaf) {
            Backup-File -Path $sc.Src -BackupRoot $BackupRoot | Out-Null
        }
    }
    if ($DryRun) { Write-Host '      [DryRun] 跳过重建' -ForegroundColor Magenta; return }
    Write-LauncherShortcut -Layout $Layout
    Write-AllAutostartShortcuts -Layout $Layout
}

function Fix-TrayJson {
    param($Layout, [string] $BackupRoot)
    Write-Host ''
    Write-Host '  [4] 修复 DshLauncher\DshLauncher.json' -ForegroundColor White
    $port = Resolve-Port -JsonPath $Layout.Json -ConfigPs1Path $Layout.ConfigPs1
    Backup-File -Path $Layout.Json -BackupRoot $BackupRoot | Out-Null
    if ($DryRun) { Write-Host '      [DryRun] 跳过写入' -ForegroundColor Magenta; return }
    Write-JsonConfig -JsonPath $Layout.Json -DaemonExePath $Layout.DaemonExe -Port $port
    Write-Host ("      [OK] daemonExe 指向本目录，webPort={0}" -f $port) -ForegroundColor Green
}

function Fix-Encoding {
    param($Layout, [string] $BackupRoot)

    Write-Host ''
    Write-Host '  [5] 修复配置文件的编码与换行（不改任何值）' -ForegroundColor White
    if (-not (Test-Path -LiteralPath $Layout.ConfigPs1 -PathType Leaf)) {
        Write-Host '      配置文件不存在，跳过' -ForegroundColor Yellow; return
    }
    $bytes = [System.IO.File]::ReadAllBytes($Layout.ConfigPs1)
    $hadBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $txt = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hadBom) { $txt = $txt.Substring(1) }        # 去掉 BOM 字符
    $hadCr = ($txt.IndexOf("`r") -ge 0)

    # 正确的形状是「有 BOM 且 无 CR」——之前写成 -not $hadBom，等于反过来
    if ($hadBom -and -not $hadCr) {
        Write-Host '      已经是 UTF-8 带 BOM + 纯 LF，无需修改' -ForegroundColor DarkGray
        return
    }

    $fixed = ($txt -replace "`r`n", "`n")
    # 值必须一个都没变：先解析前后再比对
    $before = Get-ConfigValuesFromText -Text $txt
    $after  = Get-ConfigValuesFromText -Text $fixed
    if ($before.Count -ne $after.Count) {
        Write-Host '      归一化会改变可解析的字段数，已中止' -ForegroundColor Red
        Add-Problem '编码修复中止：字段数变化'
        return
    }
    foreach ($k in $before.Keys) {
        if ([string]$before[$k] -ne [string]$after[$k]) {
            Write-Host ("      归一化会改变 {0} 的值，已中止" -f $k) -ForegroundColor Red
            Add-Problem '编码修复中止：值发生变化'
            return
        }
    }

    Write-Host ("      原来：BOM={0} 含 CR={1} -> 统一为 UTF-8 带 BOM + 纯 LF" -f $hadBom, $hadCr) -ForegroundColor Gray
    Backup-File -Path $Layout.ConfigPs1 -BackupRoot $BackupRoot | Out-Null

    $out = ($fixed -replace "`n", "`r`n")   # Write-Utf8Bom 内部再统一
    $na = $fixed.Replace("`n", "`r`n")
    $na2 = $na.Replace("`r`n", "`n")        # 保证纯 LF
    $enc = New-Object System.Text.UTF8Encoding($true)
    $textBytes = $enc.GetPreamble() + $enc.GetBytes($na2)
    Write-FixedFile -Path $Layout.ConfigPs1 -Bytes $textBytes -Label '编码已归一'
}

try {
    Clear-Problems
    $root   = Get-InstallRoot -ScriptsDir $PSScriptRoot -Override $Root
    $layout = Get-Layout -Root $root

    Write-Host ''
    Write-Host '=====================================================' -ForegroundColor Cyan
    Write-Host '  DSH Web Launcher Daemon - 修复' -ForegroundColor Cyan
    Write-Host '=====================================================' -ForegroundColor Cyan
    if ($DryRun) { Write-Host '  *** DryRun 模式：只演示，不写任何文件 ***' -ForegroundColor Magenta }

    if (-not $Fix -and -not $All) {
        Write-Host ''
        Write-Host '  1) 彻底重置 DshLauncherDaemon_Config.ps1（用默认模板覆盖，先备份）' -ForegroundColor Gray
        Write-Host '  2) 重建 defaults.json' -ForegroundColor Gray
        Write-Host '  3) 重建快捷方式（根目录启动入口 + autostart 两个源文件）' -ForegroundColor Gray
        Write-Host '  4) 修复 DshLauncher.json' -ForegroundColor Gray
        Write-Host '  5) 修复配置编码 / 换行（不改值）' -ForegroundColor Gray
        Write-Host '  a) 全部执行' -ForegroundColor White
        Write-Host ''
        $ans = Read-Host '  要执行哪些？（如 1,5 或 a）'
        if ($ans -match '^(a|A|全部)$') { $All = $true } else { $Fix = $ans }
    }
    # 统一在这里把 "1,3" / "1 3" / "1, 3" 拆成整数列表，并报告无法识别的输入。
    # 注意：结果必须存进新变量 $fixPick —— 之前存回 $Fix 时，因为 $Fix 声明为
    # [string]，@(2,3) 被静默强转成 "2 3"，switch 一项都匹配不上，脚本看起来
    # 正常跑完（退出码 0）却什么都没做。
    $fixList = @()
    $rawTokens = (@($Fix) + @($Rest)) -join ','
    if ($rawTokens) {
        foreach ($tok in ($rawTokens -split '[,\s]+')) {
            if ($tok -eq '') { continue }
            if ($tok -match '^[1-5]$') { $fixList += [int] $tok }
            else { Write-Host ("   忽略无法识别的修复项：{0}（可用 1-5）" -f $tok) -ForegroundColor Yellow }
        }
    }
    if ($All) { $fixList = @(1,2,3,4,5) }
    # @() 是必须的：单个修复项时 += 的结果会被解包成标量
    $fixPick = @($fixList | Sort-Object -Unique)

    if (@($fixPick).Count -eq 0) {
        Write-Host ''
        Write-Host '  没有选择任何修复项，什么都没改。' -ForegroundColor DarkGray
        exit 0
    }

    $backupRoot = Join-Path $layout.BackupDir ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-fixed')
    if (-not $DryRun) {
        New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
        Write-Host ("  修复前的文件会备份到：{0}" -f $backupRoot) -ForegroundColor DarkGray
    }

    Write-Host ("   将执行修复项：{0}" -f (($fixPick | Sort-Object) -join ', ')) -ForegroundColor White
    foreach ($n in ($fixPick | Sort-Object)) {
        switch ($n) {
            1 { Fix-ResetConfig  -Layout $layout -BackupRoot $backupRoot }
            2 { Fix-DefaultsJson -Layout $layout }
            3 { Fix-Shortcuts    -Layout $layout -BackupRoot $backupRoot }
            4 { Fix-TrayJson     -Layout $layout -BackupRoot $backupRoot }
            5 { Fix-Encoding     -Layout $layout -BackupRoot $backupRoot }
        }
    }

    Show-Integrity -Layout $layout | Out-Null
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Host '修复失败：' -ForegroundColor Red
    Write-Host ("   {0}" -f $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    exit 1
}
