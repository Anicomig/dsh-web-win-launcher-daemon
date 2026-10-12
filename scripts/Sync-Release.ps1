<#
    Sync-Release.ps1 - 把项目里的「发布物」同步到发布副本 dsh-web-launcher-daemon\

    发布副本的布局（与开发目录有意不同）
    ----------------------------------
    根目录  = 只放**运行需要的东西**
        DshLauncherDaemon.exe / DshLauncherDaemon_Config.ps1（后者不覆盖）
        Install.cmd / Initialization.cmd / uninstall.cmd / Fixed.cmd
        scripts\*.ps1            四个入口的实现
        ConfigHost\              DshConfigHost.exe + 原生 dll + wwwroot\ + default-config.ps1
        DshLauncher\             DshLauncher.exe + DshLauncher.json
        autostart\               两个 .lnk 源文件 + VBS
        docs\config-host.md      配置窗口使用说明
        README.md                发布副本自己的说明

    src\    = **镜像开发目录布局**的完整源码树，可以独立重新编译
        src\DshLauncherDaemon.ps1
        src\DshLauncher\          源码 + build.cmd（它按 ..\assets\ 找图标）
        src\ConfigHost\           src\*.cs、csproj、tests、build.cmd
        src\assets\               两个 .ico + original.svg
        src\tools\                Node 测试

    刻意不同步
    ----------
        _backup\        发布副本里不留备份
        log\            运行产物
        .superpowers\   头脑风暴截图
        _ps2exe_dl\     外部构建依赖（PS2EXE 第三方包）
        docs\superpowers\、docs\ui-demos\   内部方案与界面稿
        bin\ obj\ dist\ 编译中间产物与 NuGet 缓存
        DshLauncherDaemon_Config.ps1  含机器/用户相关的绝对路径，同步过去只会带错路径
        ConfigHost\defaults.json      同上：由目标机器上的 Install.cmd 自己生成
        同步到发布版.cmd / 本脚本      开发工具，不该出现在发布物里

    用法
    ----
    双击 ..\同步到发布版.cmd，或：
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Sync-Release.ps1
        powershell ... -File scripts\Sync-Release.ps1 -Target "D:\somewhere\dsh-web-launcher-daemon"
        powershell ... -File scripts\Sync-Release.ps1 -DryRun
#>

[CmdletBinding()]
param(
    [string] $Target,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 文件名常量（与 _Common.ps1 里的 $script:ShortcutName / $script:TrayShortcutName
# / $script:VbsName 保持一致）。Sync-Release 是独立脚本，不 dot-source _Common。
$script:ShortcutName    = 'LaunchDshLauncherDaemon.lnk'
$script:TrayShortcutName = 'LaunchDshLauncher.lnk'
$script:VbsName         = 'LaunchDshLauncherDaemon.vbs'


# 目标相对路径 -> 源相对路径（需要改名的都列在这里）
$renames = @(
    @{ From = 'DshLauncher\dist\DshLauncher.exe'; To = 'DshLauncher\DshLauncher.exe' }
    @{ From = 'DshLauncher\DshLauncher.json';     To = 'DshLauncher\DshLauncher.json' }
)

# ---- ConfigHost 的运行物：只列这几个二进制，绝不整目录拷（里面有 227 MB 的 bin/obj）----
$configHostBins = @(
    'DshConfigHost.exe'
    'WebView2Loader.dll'
    'D3DCompiler_47_cor3.dll'
    'PenImc_cor3.dll'
    'PresentationNative_cor3.dll'
    'vcruntime140_cor3.dll'
    'wpfgfx_cor3.dll'
    'default-config.ps1'
)

# ---- 整目录同步的运行目录（只取该目录下的文件，不递归）----
$runtimeDirs = @(
    @{ From = 'scripts';   To = 'scripts';   Skip = @('README.md','Sync-Release.ps1') }
    @{ From = 'autostart'; To = 'autostart'; Skip = @() }
)

# ---- 源码镜像：保持与开发目录相同的相对路径 ----
$sourceDirs  = @('DshLauncher','ConfigHost','assets','tools')
$sourceFiles = @(
    @{ From = 'DshLauncherDaemon.ps1'; To = 'src\DshLauncherDaemon.ps1' }
    @{ From = 'scripts\README.md';     To = 'src\scripts\README.md' }
    @{ From = 'assets\README.md';      To = 'src\assets\README.md' }
    @{ From = 'ConfigHost\README.md';  To = 'src\ConfigHost\README.md' }
    @{ From = 'DshLauncher\README.md'; To = 'src\DshLauncher\README.md' }
)
$srcExclude = '\\(bin|obj|node_modules|dist)\\'
# 源码树里也排除运行物本身：DshConfigHost.exe 与原生 dll 和 C# 源码同级放着，
# 递归镜像会把那 70 MB 产品再抄一份进 src\，而发布副本根目录已经有了。
$srcSkipNames = @('DshConfigHost.exe','WebView2Loader.dll','D3DCompiler_47_cor3.dll',
                  'PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll',
                  'wpfgfx_cor3.dll','defaults.json','DshLauncher.json',
                  'tray_menu.log','tray_startup.log')

# ---- 发布副本里绝不该存在的东西 ----
$mustNotExist = @(
    'ConfigTools', '_backup', 'log', '.superpowers', '_ps2exe_dl',
    '安装初始化.cmd', '迁移初始化.cmd', '卸载.cmd',
    '卸载-仅清理注册表与自启项（保留文件）.cmd',
    '同步到发布版.cmd', '编译宿主.cmd',
    'scripts\Install-Init.ps1', 'scripts\Migrate-Init.ps1',
    'scripts\Uninstall-Clean.ps1', 'scripts\Uninstall-Full.ps1',
    'scripts\Sync-Release.ps1', 'scripts\README.md',
    'DshLauncher\DshLauncher.exe - 快捷方式.lnk',
    'DshLauncher\tray_menu.log', 'DshLauncher\tray_startup.log',
    'DshLauncher.lnk', 'autostart\LaunchDshLauncherDaemon.lnk', 'autostart\LaunchDshLauncher.lnk',
    'docs\superpowers', 'docs\ui-demos',
    'ConfigHost\src', 'ConfigHost\tests', 'ConfigHost\build.cmd', 'ConfigHost\README.md',
    'ConfigHost\defaults.json', 'assets'
)

function Place {
    param([string] $From, [string] $ToRel, [ref] $Count)
    $t = Join-Path $Target $ToRel
    if ($DryRun) { Write-Host ("      [DryRun] {0}" -f $ToRel) -ForegroundColor Magenta; return }
    $td = Split-Path -Parent $t
    if (-not (Test-Path -LiteralPath $td)) { New-Item -ItemType Directory -Force -Path $td | Out-Null }
    Copy-Item -LiteralPath $From -Destination $t -Force
    $Count.Value++
}

$n = 0
try {
    if (-not $Target) { $Target = Join-Path $root 'dsh-web-launcher-daemon' }
    if (-not (Test-Path -LiteralPath $Target -PathType Container)) {
        Write-Host ("发布副本目录不存在：{0}" -f $Target) -ForegroundColor Red
        exit 1
    }

    Write-Host ''
    Write-Host '=====================================================' -ForegroundColor White
    Write-Host '  同步到发布副本' -ForegroundColor White
    Write-Host '=====================================================' -ForegroundColor White
    Write-Host ("  源   ：{0}" -f $root) -ForegroundColor DarkGray
    Write-Host ("  目标 ：{0}" -f $Target) -ForegroundColor DarkGray
    if ($DryRun) { Write-Host '  *** DryRun：只列出将要做的事，不写不删 ***' -ForegroundColor Magenta }

    Write-Host ''
    Write-Host '── [1/4] 清掉发布副本里不该存在的东西 ─────────────────' -ForegroundColor Cyan
    foreach ($x in $mustNotExist) {
        $p = Join-Path $Target $x
        if (-not (Test-Path -LiteralPath $p)) { continue }
        if ($DryRun) { Write-Host ("      [DryRun] 删 {0}" -f $x) -ForegroundColor Magenta }
        else { Remove-Item -LiteralPath $p -Recurse -Force; Write-Host ("      删 {0}" -f $x) -ForegroundColor Yellow }
        $n++
    }

    Write-Host ''
    Write-Host '── [2/4] 运行物 ───────────────────────────────────────' -ForegroundColor Cyan
    foreach ($f in @('DshLauncherDaemon.exe','Install.cmd','Initialization.cmd','uninstall.cmd','Fixed.cmd','docs\config-host.md')) {
        $s = Join-Path $root $f
        if (-not (Test-Path -LiteralPath $s)) { Write-Host ("      缺源文件：{0}" -f $f) -ForegroundColor Red; continue }
        Place -From $s -ToRel $f -Count ([ref]$n)
        Write-Host ("      {0}" -f $f) -ForegroundColor Green
    }
    foreach ($r in $renames) {
        $s = Join-Path $root $r.From
        if (-not (Test-Path -LiteralPath $s)) { Write-Host ("      缺源文件：{0}" -f $r.From) -ForegroundColor Red; continue }
        Place -From $s -ToRel $r.To -Count ([ref]$n)
        Write-Host ("      {0}   ← {1}" -f $r.To, $r.From) -ForegroundColor Green
    }
    foreach ($b in $configHostBins) {
        $s = Join-Path $root "ConfigHost\$b"
        if (-not (Test-Path -LiteralPath $s)) { Write-Host ("      缺 ConfigHost\{0}" -f $b) -ForegroundColor Red; continue }
        Place -From $s -ToRel "ConfigHost\$b" -Count ([ref]$n)
        Write-Host ("      ConfigHost\{0}" -f $b) -ForegroundColor Green
    }
    $wr = Join-Path $root 'ConfigHost\wwwroot'
    foreach ($file in @(Get-ChildItem -LiteralPath $wr -File -Force -ErrorAction SilentlyContinue)) {
        Place -From $file.FullName -ToRel ("ConfigHost\wwwroot\" + $file.Name) -Count ([ref]$n)
        Write-Host ("      ConfigHost\wwwroot\{0}" -f $file.Name) -ForegroundColor Green
    }
    foreach ($d in $runtimeDirs) {
        $srcDir = Join-Path $root $d.From
        if (-not (Test-Path -LiteralPath $srcDir -PathType Container)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $srcDir -File -Force)) {
            if ($d.Skip -contains $file.Name) { continue }
            # autostart\ 里的 .lnk 是绝对路径模板，每个机器都得重建。
            # 因此 sync 不复制它们；它们会在下面的「修正副本内的绝对路径」
            # 段里通过 Write-AutostartShortcut 重新生成（与根目录的
            # DshLauncher.lnk 同一处理路径）。
            if ($d.From -eq 'autostart' -and $file.Extension -eq '.lnk') { continue }
            Place -From $file.FullName -ToRel ($d.To + '\' + $file.Name) -Count ([ref]$n)
            Write-Host ("      {0}\{1}" -f $d.To, $file.Name) -ForegroundColor Green
        }
    }

    Write-Host ''
    Write-Host '── [3/4] 源码镜像到 src\（保留可重新编译的能力）───────' -ForegroundColor Cyan

    # 镜像必须先清空：同步只增不删的话，上一版留在 src\ 里的文件会一直赖着
    # （实测就出现过：修好排除规则之后，src\ 里仍留着早先误拷的 defaults.json）。
    $srcRoot = Join-Path $Target 'src'
    if (Test-Path -LiteralPath $srcRoot) {
        if ($DryRun) { Write-Host "      [DryRun] 将清空 src\ 后重建" -ForegroundColor Magenta }
        else { Remove-Item -LiteralPath $srcRoot -Recurse -Force; Write-Host "      清空 src\ 后重建" -ForegroundColor DarkGray }
    }
    foreach ($sf in $sourceFiles) {
        $s = Join-Path $root $sf.From
        if (-not (Test-Path -LiteralPath $s)) { Write-Host ("      缺源文件：{0}" -f $sf.From) -ForegroundColor Yellow; continue }
        Place -From $s -ToRel $sf.To -Count ([ref]$n)
        Write-Host ("      {0}" -f $sf.To) -ForegroundColor Green
    }
    foreach ($d in $sourceDirs) {
        $srcDir = Join-Path $root $d
        if (-not (Test-Path -LiteralPath $srcDir -PathType Container)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $srcDir -Recurse -File -Force | Where-Object { $_.FullName -notmatch $srcExclude -and $srcSkipNames -notcontains $_.Name })) {
            $rel = $file.FullName.Substring($srcDir.Length).TrimStart('\')
            Place -From $file.FullName -ToRel ("src\{0}\{1}" -f $d, $rel) -Count ([ref]$n)
        }
        $c = @(Get-ChildItem (Join-Path $Target ("src\" + $d)) -Recurse -File -ErrorAction SilentlyContinue).Count
        Write-Host ("      src\{0}\   （{1} 个文件）" -f $d, $c) -ForegroundColor Green
    }

    Write-Host ''
    Write-Host '── 修正副本内的绝对路径 ────────────────────────────────' -ForegroundColor Cyan

    # DshLauncher.json 里的 daemonExe：发布副本会被打包分发，写死任何绝对路径
    # （哪怕是"副本内"的路径）都会把**打包这台机器**的目录结构泄露出去，而且
    # 收件人解压到别处后那个路径照样是错的。所以这里刻意写成空串：
    #   - 托盘读到空值会按自身位置推算默认 daemon 路径（TrayConfig）；
    #   - Install.cmd / Initialization.cmd 跑一次就把真实路径填回去。
    # openMode / edgeAppId 照常保留（配置窗口写入的打开方式）。
    $relJson = Join-Path $Target 'DshLauncher\DshLauncher.json'
    if ($DryRun)
    {
        Write-Host '      [DryRun] 将把 DshLauncher.json 的 daemonExe 置空（由 Install 回填）' -ForegroundColor Magenta
    }
    elseif (Test-Path -LiteralPath $relJson)
    {
        $text = [System.IO.File]::ReadAllText($relJson, [System.Text.Encoding]::UTF8)

        # 保留打开方式，缺省 browser / 空
        $mode = 'browser'
        $appId = ''
        $mm = [regex]::Match($text, '"openMode"\s*:\s*"([^"]*)"')
        if ($mm.Success -and $mm.Groups[1].Value -in @('browser','app')) { $mode = $mm.Groups[1].Value }
        $aa = [regex]::Match($text, '"edgeAppId"\s*:\s*"([^"]*)"')
        if ($aa.Success -and $aa.Groups[1].Value -match '^[a-p]{32}$') { $appId = $aa.Groups[1].Value }

        # 端口沿用 JSON 里已有的值（没有就用 3080）
        $port = 3080
        $pp = [regex]::Match($text, '"webPort"\s*:\s*(\d+)')
        if ($pp.Success) { $port = [int]$pp.Groups[1].Value }

        $new = "{`r`n  `"daemonExe`": `"`",`r`n  `"webPort`": $port,`r`n"
        $new += "  `"openMode`": `"$mode`",`r`n  `"edgeAppId`": `"$appId`"`r`n}`r`n"
        [System.IO.File]::WriteAllText($relJson, $new, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host ("      DshLauncher.json -> daemonExe 置空（Install 回填），openMode={0}" -f $mode) -ForegroundColor Green
        $n++
    }

    # 快捷方式：发布副本会被打包分发，而 .lnk 里存的是**绝对路径** ——
    # 在打包机上生成只会泄露打包机的目录结构，且收件人解压到别处后照样是错的。
    # 所以这里不生成、只删除；三个 .lnk 由目标机器上的 Install.cmd /
    # Initialization.cmd 创建（它们本来就会重建这三个）。
    $lnkRel = @('DshLauncher.lnk',
                "autostart\$script:ShortcutName",
                "autostart\$script:TrayShortcutName")
    foreach ($l in $lnkRel) {
        $p = Join-Path $Target $l
        if ($DryRun) { Write-Host ("      [DryRun] 删 {0}（Install 会在目标机重建）" -f $l) -ForegroundColor Magenta; continue }
        if (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Force
            Write-Host ("      删 {0}（含绝对路径，由目标机 Install 重建）" -f $l) -ForegroundColor Yellow
            $n++
        }
    }

    Write-Host ''
    Write-Host '── [4/4] 结果 ─────────────────────────────────────────' -ForegroundColor Cyan
    $all = @(Get-ChildItem -LiteralPath $Target -Recurse -File -Force)
    Write-Host ("  发布副本 {0} 个文件，{1:N1} MB（本次处理 {2} 项）" -f $all.Count, (($all | Measure-Object Length -Sum).Sum/1MB), $n) -ForegroundColor White
    Write-Host ''
    Write-Host '  刻意没同步（目标机器上自己生成）：' -ForegroundColor DarkGray
    Write-Host '    DshLauncherDaemon_Config.ps1   含机器/用户相关的绝对路径' -ForegroundColor DarkGray
    Write-Host '    ConfigHost\defaults.json       由 Install.cmd 从模板推导' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  到目标机器后在那个副本里跑一次 Install.cmd。' -ForegroundColor Gray
    Write-Host ''
}
catch {
    Write-Host ''
    Write-Host ("同步失败：{0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}