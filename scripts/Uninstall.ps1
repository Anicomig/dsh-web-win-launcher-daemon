#
#     Uninstall.ps1 - 卸载（三段式，逐项问，默认全部不动）
#
#     三段各自管什么
#     --------------
#     [1] 清理「安装数据」= 安装时写到安装目录**之外**的东西
#           · %LOCALAPPDATA%\DshConfigHost      （DshConfigHost 的 WebView2 缓存，可再生）
#           · 启动文件夹里的两个自启 .lnk        （只认这两个确切文件名）
#           · HKCU\Software\Classes\dshlauncher （老版本遗留的协议注册，新版根本不注册）
#         注意：不包含安装目录里的任何东西。
#
#     [2] 删除「安装目录」= 清空本文件夹的内容
#           · 保留 _backup\（历次快照可能还有用）
#           · 需要额外输入确认词
#           · 正在使用的文件可能删不掉，会列出来让你手动处理
#
#     [3] 清理「用户数据」= 你在这台机器上产生的东西
#           · log\                              （运行日志）
#           · DshLauncherDaemon_Config.ps1      （你的全部自定义配置）
#         如果第 [2] 步选了删除安装目录，这一步**自动跳过** —— 目录都没了，再清就没意义。
#
#     默认值一律是「不」。不申请管理员权限。
#
#     测试用开关（普通用户不需要）
#     --------------------------
#       -DryRun            只打印将要做什么，一个文件都不动
#       -Answer1/2/3 yes|no 预先给定答案，跳过提问
#       -Root <路径>       对指定目录操作（配合临时克隆做验证）
#
#     用法
#     ----
#     双击 ..\uninstall.cmd，或者：
#         powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Uninstall.ps1

[CmdletBinding()]
param(
    [string]  $Root,
    [switch]  $DryRun,
    # 以下两个仅供测试注入，默认走真实位置
    [string]  $StartupDir,
    [string]  $LocalAppDataDir,
    [string]  $Answer1,
    [string]  $Answer2,
    [string]  $Answer3,
    [switch]  $Yes,
    # 供自动化/测试使用：跳过第 [2] 步的确认词输入。
    # 双击 uninstall.cmd 时不带这个参数，仍然必须手输确认词。
    [switch]  $ForceDeleteDir
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_Common.ps1')

$CONFIRM_WORD = '确认删除安装目录'

function Ask-YesNo {
    param(
        [Parameter(Mandatory=$true)][string] $Question,
        [string] $Preset
    )
    if ($Preset) {
        $yes = ($Preset -match '^(y|yes)$')
        Write-Host ("   {0} {1}（预设）" -f $Question, $(if ($yes) { '是' } else { '否' })) -ForegroundColor Gray
        return $yes
    }
    $ans = Read-Host ("{0} [y/N]" -f $Question)
    return ($ans -match '^(y|Y|yes|YES)$')
}

function Describe-Targets {
    param([string[]] $Paths)
    foreach ($p in $Paths) {
        if (Test-Path -LiteralPath $p) {
            $it = Get-Item -LiteralPath $p
            if ($it.PSIsContainer) {
                $n = @(Get-ChildItem -LiteralPath $p -Recurse -File -Force -ErrorAction SilentlyContinue).Count
                Write-Host ("      · {0}   （目录，{1} 个文件）" -f $p, $n) -ForegroundColor DarkGray
            } else {
                Write-Host ("      · {0}   （{1:N0} B）" -f $p, $it.Length) -ForegroundColor DarkGray
            }
        } else {
            Write-Host ("      · {0}   （不存在，跳过）" -f $p) -ForegroundColor DarkGray
        }
    }
}

function Remove-Target {
    param([string] $Path, [string] $Label)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ($DryRun) { Write-Host ("      [DryRun] 将删除 {0}" -f $Path) -ForegroundColor Magenta; return }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force
        Write-Host ("      [OK] 已删除 {0}" -f $Label) -ForegroundColor Green
    } catch {
        Write-Host ("      [跳过] {0} 删不掉：{1}" -f $Label, $_.Exception.Message) -ForegroundColor Yellow
        Add-Problem ("无法删除 " + $Path)
    }
}

try {
    Clear-Problems
    $root   = Get-InstallRoot -ScriptsDir $PSScriptRoot -Override $Root
    $layout = Get-Layout -Root $root
    $startup = $StartupDir
    if (-not $startup) { $startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup) }
    $lad = $LocalAppDataDir
    if (-not $lad) { $lad = $env:LOCALAPPDATA }
    $udf = Join-Path $lad 'DshConfigHost'
    $regKey  = 'HKCU:\Software\Classes\dshlauncher'

    Write-Host ''
    Write-Host '=====================================================' -ForegroundColor Yellow
    Write-Host '  DSH Web Launcher Daemon - 卸载' -ForegroundColor Yellow
    Write-Host '=====================================================' -ForegroundColor Yellow
    if ($DryRun) {
        Write-Host '  *** DryRun 模式：只演示，不会改动任何东西 ***' -ForegroundColor Magenta
    }

    # 正在运行的程序会让删除失败，先提示
    $running = @()
    foreach ($pn in @('DshConfigHost','DshLauncher','DshLauncherDaemon')) {
        $ps = @(Get-Process -Name $pn -ErrorAction SilentlyContinue)
        if ($ps.Count) { $running += ("{0} (PID {1})" -f $pn, (($ps | ForEach-Object { $_.Id }) -join ',')) }
    }
    if ($running.Count) {
        Write-Host ''
        Write-Host ('   下列程序正在运行，相关文件可能删不掉：{0}' -f ($running -join '、')) -ForegroundColor Yellow
        Write-Host      '   建议先退出托盘 / 关闭配置窗口再继续。' -ForegroundColor DarkGray
    }

    # ---------- [1] 安装数据 ----------
    Write-Host ''
    Write-Host '[1/3] 清理安装数据（写在安装目录之外的东西）' -ForegroundColor White
    Write-Host '        · DshConfigHost 的 WebView2 缓存（可再生，不含你的配置）' -ForegroundColor Gray
    Write-Host '        · 启动文件夹里的两个自启 .lnk（只认这两个确切文件名）' -ForegroundColor Gray
    Write-Host '        · 老版本遗留的 dshlauncher:// 协议注册（新版根本不注册）' -ForegroundColor Gray
    Describe-Targets @($udf, (Join-Path $startup 'LaunchDshLauncherDaemon.lnk'),
                          (Join-Path $startup 'LaunchDshLauncher.lnk'))
    Write-Host ("        · 注册表 {0}  存在 = {1}" -f $regKey, (Test-Path $regKey)) -ForegroundColor DarkGray

    $do1 = Ask-YesNo -Question '   要清理这些安装数据吗？' -Preset $(if ($Yes) { 'y' } else { $Answer1 })
    if ($do1) {
        Remove-Target -Path $udf -Label 'WebView2 缓存目录'
        foreach ($n in @('LaunchDshLauncherDaemon.lnk','LaunchDshLauncher.lnk')) {
            Remove-Target -Path (Join-Path $startup $n) -Label "启动项 $n"
        }
        if (Test-Path $regKey) {
            if ($DryRun) { Write-Host "      [DryRun] 将删除注册表 $regKey" -ForegroundColor Magenta }
            else {
                Remove-Item -Path $regKey -Recurse -Force
                Write-Host '      [OK] 已删除遗留协议注册' -ForegroundColor Green
            }
        }
    } else {
        Write-Host '      未清理安装数据' -ForegroundColor DarkGray
    }

    # ---------- [2] 安装目录 ----------
    Write-Host ''
    Write-Host '[2/3] 删除安装目录的内容' -ForegroundColor White
    Write-Host ("      位置：{0}" -f $root) -ForegroundColor DarkGray
    # 根目录那个快捷方式在安装目录里，随它一起删；单独点出来是为了让用户知道
    # 「托盘入口也会消失」，而不是以为只剩一个空壳。
    Write-Host ('        · 含 {0}（双击启动托盘的入口）' -f $script:LauncherShortcutName) -ForegroundColor Gray
    $keep = @('_backup')
    Write-Host ('      保留：{0}\' -f ($keep -join '\, ')) -ForegroundColor DarkGray
    $do2 = $false
    if (-not (Ask-YesNo -Question '   要删除安装目录里的内容吗？' -Preset $(if ($Yes) { 'y' } else { $Answer2 }))) {
        Write-Host '      未删除安装目录' -ForegroundColor DarkGray
    } else {
        if ($DryRun) {
            $do2 = $true
            Write-Host '      [DryRun] 跳过确认词输入，仅演示' -ForegroundColor Magenta
        } elseif ($ForceDeleteDir) {
            $do2 = $true
            Write-Host '      （带了 -ForceDeleteDir，跳过确认词）' -ForegroundColor Yellow
        } else {
            Write-Host ''
            Write-Host ('   这一步不可撤销。请输入「{0}」确认：' -f $CONFIRM_WORD) -ForegroundColor Red
            $ans = Read-Host '   确认词'
            if ($ans -ne $CONFIRM_WORD) {
                Write-Host '   确认词不匹配，跳过删除安装目录。' -ForegroundColor DarkGray
            } else { $do2 = $true }
        }
    }

    if ($do2) {
        $locked = @()
        # scripts\ 排到最后删：本脚本自己就住在这里，先删它会让后半程失去依托
        $children = @(Get-ChildItem -LiteralPath $root -Force |
                      Sort-Object { if ($_.Name -eq 'scripts') { 1 } else { 0 } })
        foreach ($child in $children) {
            if ($keep -contains $child.Name) { continue }
            if ($DryRun) { Write-Host ("      [DryRun] 将删除 {0}" -f $child.FullName) -ForegroundColor Magenta; continue }
            try {
                Remove-Item -LiteralPath $child.FullName -Recurse -Force
                Write-Host ("      [OK] 已删除 {0}" -f $child.Name) -ForegroundColor Green
            } catch {
                $locked += $child.Name
            }
        }
        if ($locked.Count) {
            Write-Host ''
            Write-Host ('   这些删不掉（多半还在运行）：{0}' -f ($locked -join '、')) -ForegroundColor Yellow
            Write-Host      '   重启后再运行一次本脚本即可。' -ForegroundColor DarkGray
            Add-Problem ('部分条目未能删除: ' + ($locked -join '、'))
        }
    }

    # ---------- [3] 用户数据 ----------
    Write-Host ''
    Write-Host '[3/3] 清理用户数据（你在本机产生的东西）' -ForegroundColor White
    if ($do2) {
        Write-Host '      安装目录已在上一步清空，这一步没有意义，自动跳过。' -ForegroundColor DarkGray
    } else {
        Describe-Targets @($layout.LogDir, $layout.ConfigPs1)
        $do3 = Ask-YesNo -Question '   要删除 log\ 与 DshLauncherDaemon_Config.ps1 吗？' -Preset $Answer3
        if ($do3) {
            Remove-Target -Path $layout.LogDir    -Label 'log 目录'
            Remove-Target -Path $layout.ConfigPs1 -Label 'DshLauncherDaemon_Config.ps1'
        } else {
            Write-Host '      未清理用户数据' -ForegroundColor DarkGray
        }
    }

    Write-Host ''
    Write-Host '说明：dsh web 的 node 进程与开机自启的开关状态不受本脚本影响；' -ForegroundColor DarkGray
    Write-Host '      要停服务请在托盘菜单里操作。' -ForegroundColor DarkGray
    Write-Host ''
    Show-Summary -Layout $layout
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Host '卸载失败：' -ForegroundColor Red
    Write-Host ("   {0}" -f $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    exit 1
}
