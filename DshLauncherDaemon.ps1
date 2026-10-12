# ========== DSH Launcher Daemon ==========
# DSH（DeepSeek Harness）Web 守护进程 - 自动监控、启动、崩溃重启 dsh web
#
# 守护目标：node.exe "<dsh>\lib\bin.js" web --host <host> --port <port> --no-open
# 健康判据：TCP 端口监听（主） + node 进程指纹（辅）

# Resolve the application directory.
#
# Under PS2EXE-compiled hosts (verified with PS2EXE 1.0.18, -noConsole) BOTH
# $PSScriptRoot and $MyInvocation.MyCommand.Path are EMPTY; the module instead
# provides the predefined $ScriptRoot variable. The exe path is kept as a last
# resort so the script also works under unusual hosts.
$script:APP_DIR = if ($PSScriptRoot) {
    $PSScriptRoot
} elseif ($ScriptRoot) {
    $ScriptRoot
} else {
    try {
        Split-Path -Parent ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
    } catch {
        $PSScriptRoot
    }
}

# Config file loading.
#
# Two traps are handled here; both were verified by experiment on this machine.
#
# 1. Execution policy. Windows defaults to RemoteSigned, which REFUSES to
#    dot-source a local unsigned .ps1 ("未进行数字签名"). PS2EXE-compiled hosts do
#    not pass -ExecutionPolicy Bypass, so that refusal is the normal case rather
#    than an edge case, and a windowless host has no console to reveal it.
#    Parsing the file text into a ScriptBlock avoids the policy gateway.
#
# 2. Scope. A ScriptBlock invoked from INSIDE a function defines its variables in
#    that function's scope, so they disappear when it returns. Measured directly:
#    an inner probe read CONFIG_PORT=3099 while the enclosing script read $null,
#    which made every CONFIG_* value look missing and sent the daemon into an
#    unbounded launch loop. The load is therefore inlined at script top level and
#    deliberately NOT wrapped in a function.
$script:CONFIG_FILE = Join-Path $script:APP_DIR "DshLauncherDaemon_Config.ps1"
$script:CONFIG_ERROR = $null
$script:CONFIG_RAW = $null

if (-not (Test-Path -LiteralPath $script:CONFIG_FILE)) {
    $script:CONFIG_ERROR = "missing file: $script:CONFIG_FILE"
} else {
    try {
        $script:CONFIG_RAW = Get-Content -LiteralPath $script:CONFIG_FILE -Raw -Encoding UTF8
    } catch {
        $script:CONFIG_ERROR = "unreadable: $($_.Exception.Message)"
    }

    if (-not $script:CONFIG_ERROR) {
        if (-not $script:CONFIG_RAW -or $script:CONFIG_RAW.Trim().Length -eq 0) {
            $script:CONFIG_ERROR = "empty file: $script:CONFIG_FILE"
        } else {
            try {
                . ([ScriptBlock]::Create($script:CONFIG_RAW))
            } catch {
                $script:CONFIG_ERROR = "evaluation error: $($_.Exception.Message)"
            }
            if (-not $script:CONFIG_ERROR) {
                $probe = Get-Variable -Name CONFIG_PORT -ValueOnly -ErrorAction SilentlyContinue
                if ($null -eq $probe) {
                    $script:CONFIG_ERROR = "text evaluated but defined no CONFIG_* variables"
                }
            }
        }
    }
}

# Guard against the empty-config failure mode: every path and port below would
# become $null, Start-Process would be called with an empty file name, and the
# main loop would spin without ever sleeping (observed: 1.2 MB log per minute).
function Test-ConfigComplete {
    $required = @(
        'CONFIG_NODE_EXE', 'CONFIG_DSH_CLI', 'CONFIG_DSH_HOME', 'CONFIG_DSH_PROFILE',
        'CONFIG_PORT', 'CONFIG_HOST',
        'CONFIG_PORT_CHECK_INTERVAL', 'CONFIG_PROC_CHECK_INTERVAL',
        'CONFIG_DAEMON_PORT_ONLY_INTERVAL', 'CONFIG_LAUNCH_GRACE_SEC',
        'CONFIG_STARTUP_TIMEOUT_SEC', 'CONFIG_RESTART_BACKOFF_MAX_SEC'
    )
    $bad = @()
    foreach ($name in $required) {
        $v = Get-Variable -Name $name -ValueOnly -ErrorAction SilentlyContinue
        if ($null -eq $v) { $bad += $name; continue }
        if ($v -is [string] -and $v.Trim().Length -eq 0) { $bad += $name }
        if ($v -is [int] -or $v -is [double] -or $v -is [long]) {
            if ($v -le 0) { $bad += "$name=$v" }
        }
    }
    return $bad
}

# Log Directory: log subfolder under script/exe location
$script:LOG_DIR = Join-Path $script:APP_DIR "log"
if (-not (Test-Path $script:LOG_DIR)) {
    New-Item -Path $script:LOG_DIR -ItemType Directory -Force | Out-Null
}

# Create log info file (once, never overwritten)
$infoFile = Join-Path $script:LOG_DIR "日志说明.txt"
if (-not (Test-Path $infoFile)) {
    $info = @"
DSH 日志文件说明
====================

守护进程日志（实时追加）:
  文件: dsh_daemon_YYYY-MM-DD-HH-mm_ss.log
  说明: 记录守护状态切换、重启事件、退避与错误信息

dsh web 输出日志（进程退出后落盘）:
  文件: web_YYYY-MM-DD-HH-mm_ss.log
  说明: 捕获 dsh web 的标准输出
        就绪时会打印 "dsh web: http://127.0.0.1:3080/?token=..."（含一次性进程令牌）

DSH 结构化日志（实时写入）:
  路径: %DSH_HOME%\logs\
  说明: DSH 自身的会话与启动诊断日志，实时可用
"@
    Set-Content -Path $infoFile -Value $info -Encoding UTF8
}

# Process Identity
$script:PROCESS_NAME = "DshLauncherDaemon"
try { $Host.UI.RawUI.WindowTitle = $script:PROCESS_NAME } catch { }

# Log Function
$script:LOG_FILE = Join-Path $script:LOG_DIR ("dsh_daemon_{0}.log" -f (Get-Date -Format "yyyy-MM-dd-HH-mm_ss"))

function Write-Log {
    param([string]$Message)
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] $Message"
    try {
        Add-Content -Path $script:LOG_FILE -Value $line -Encoding UTF8 -ErrorAction Stop
    } catch {
        # Never let logging failure kill the daemon
    }
}

# Config sanity gate. Never enter the daemon loop with an incomplete config:
# an empty config previously produced an unbounded launch loop.
$script:CONFIG_PROBLEMS = @(Test-ConfigComplete)

# These two checks read the values defensively: Test-ConfigComplete may already
# have flagged them, and referencing an undefined variable under Set-StrictMode or
# a future strict mode would raise instead of reporting.
$profileValue = Get-Variable -Name CONFIG_DSH_PROFILE -ValueOnly -ErrorAction SilentlyContinue
if ($profileValue -and $profileValue.ToString().ToLower() -eq "desktop") {
    $script:CONFIG_PROBLEMS += "CONFIG_DSH_PROFILE=desktop (reserved by the Electron app; dsh refuses to boot it)"
}

$extraArgs = Get-Variable -Name CONFIG_EXTRA_ARGS -ValueOnly -ErrorAction SilentlyContinue
if ($null -eq $extraArgs) {
    $CONFIG_EXTRA_ARGS = @()
} elseif ($extraArgs -isnot [array]) {
    # Keep it an array even when the config declares a bare string.
    $CONFIG_EXTRA_ARGS = @($extraArgs)
}

if ($script:CONFIG_PROBLEMS.Count -gt 0) {
    Write-Log "FATAL: configuration is incomplete, refusing to start"
    Write-Log "  Config file: $script:CONFIG_FILE"
    if ($script:CONFIG_ERROR) { Write-Log "  Load error : $script:CONFIG_ERROR" }
    Write-Log "  Missing/invalid: $($script:CONFIG_PROBLEMS -join ', ')"
    Write-Log "  Check that the file defines every CONFIG_* value (see DshLauncherDaemon_Config.ps1)."
    # Deliberately no dialog: this program must stay completely windowless.
    # The log line above is the durable record of the failure.
    exit 1
}

# Record the resolved configuration once, so a silent misconfiguration is
# always diagnosable from the log alone.
Write-Log ("Config loaded from {0} (error={1})" -f $script:CONFIG_FILE, [bool]$script:CONFIG_ERROR)

# Elevation is deliberately NOT requested.
#
# This daemon only probes a TCP port, starts node.exe, and writes its own log
# directory - none of which needs administrator rights. An earlier revision
# self-elevated with `Start-Process -Verb RunAs`, which had two bad effects:
#
#   1. A UAC prompt on every start, including at boot.
#   2. The running daemon became High-integrity, and Windows forbids a
#      Medium-integrity process from terminating a High-integrity one. The tray
#      companion therefore could never implement "stop daemon".
#
# The check below is now diagnostic only: it records whether we happen to be
# elevated, but never re-launches the process.
function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Log ("Elevated: {0} (not requested; this daemon does not need it)" -f (Test-Admin))

# Single-instance check: exit if another instance is already running
$currentExePath = if ($MyInvocation.MyCommand.Path) {
    $MyInvocation.MyCommand.Path
} else {
    try {
        [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    } catch {
        $null
    }
}
if ($currentExePath) {
    $currentExeName = [System.IO.Path]::GetFileNameWithoutExtension((Split-Path -Leaf $currentExePath))
    if ($currentExeName) {
        $existingProcs = Get-Process -Name $currentExeName -ErrorAction SilentlyContinue
        if ($existingProcs) {
            foreach ($p in $existingProcs) {
                if ($p.Id -ne $PID) {
                    Write-Log "Another instance already running (PID=$($p.Id)), exiting"
                    exit 0
                }
            }
        }
    }
}

$script:PROFILE_DIR = Join-Path (Join-Path $CONFIG_DSH_HOME "profiles") $CONFIG_DSH_PROFILE

Write-Log "=== DSH daemon started (PID=$PID, Name=$script:PROCESS_NAME) ==="
Write-Log "Host: AppDir=$script:APP_DIR, ExeHost=$($PSVersionTable.PSEdition), Compiled=$([string]::IsNullOrEmpty($PSScriptRoot))"
Write-Log "Config: Host=$CONFIG_HOST, Port=$CONFIG_PORT, Profile=$CONFIG_DSH_PROFILE"
Write-Log "Intervals: Short=$CONFIG_PORT_CHECK_INTERVAL/$CONFIG_PROC_CHECK_INTERVAL sec, Long=$CONFIG_DAEMON_PORT_ONLY_INTERVAL sec"
Write-Log "Grace: launch=$CONFIG_LAUNCH_GRACE_SEC sec, hardTimeout=$CONFIG_STARTUP_TIMEOUT_SEC sec, backoffMax=$CONFIG_RESTART_BACKOFF_MAX_SEC sec"
Write-Log "NodeExe: $CONFIG_NODE_EXE"
Write-Log "DshCli: $CONFIG_DSH_CLI"
Write-Log "DshHome: $CONFIG_DSH_HOME"
Write-Log "ProfileDir: $script:PROFILE_DIR"
Write-Log "WorkspaceDir: $CONFIG_WORKSPACE_DIR"
Write-Log "DshLogs: $(Join-Path $CONFIG_DSH_HOME 'logs')"

# Start dsh web
# Output is buffered; the file is written when the process exits (crash post-mortem).
# The readiness line "dsh web: http://.../?token=..." is captured here.
function Start-DshWeb {
    # Pre-flight validation: fail loudly instead of spinning silently
    $missing = @()
    if (-not (Test-Path $CONFIG_NODE_EXE)) { $missing += $CONFIG_NODE_EXE }
    if (-not (Test-Path $CONFIG_DSH_CLI))  { $missing += $CONFIG_DSH_CLI }
    if (-not (Test-Path $script:PROFILE_DIR)) { $missing += $script:PROFILE_DIR }

    if ($missing.Count -gt 0) {
        foreach ($m in $missing) { Write-Log "FATAL: required path missing: $m" }
        if (-not (Test-Path $script:PROFILE_DIR)) {
            Write-Log "HINT: profile '$CONFIG_DSH_PROFILE' is not initialized; run: dsh plugin --profile $CONFIG_DSH_PROFILE install"
        }
        return $false
    }

    # Pin the harness home explicitly (PS 5.1 has no Start-Process -Environment)
    $env:DSH_HOME = $CONFIG_DSH_HOME
    $env:DSH_PROFILE_DIR = $script:PROFILE_DIR

    $argList = @("`"$($CONFIG_DSH_CLI)`"", "web", "--host", $CONFIG_HOST, "--port", "$CONFIG_PORT")
    if ($CONFIG_NO_OPEN) { $argList += "--no-open" }
    if ($CONFIG_EXTRA_ARGS -and $CONFIG_EXTRA_ARGS.Count -gt 0) { $argList += $CONFIG_EXTRA_ARGS }
    $argString = $argList -join " "

    $workDir = if ($CONFIG_WORKSPACE_DIR -and (Test-Path $CONFIG_WORKSPACE_DIR)) { $CONFIG_WORKSPACE_DIR } else { $script:APP_DIR }

    if ($CONFIG_GATEWAY_LOG_ENABLED) {
        $ts = Get-Date -Format "yyyy-MM-dd-HH-mm_ss"
        $webLog = Join-Path $script:LOG_DIR ("web_{0}.log" -f $ts)
        Write-Log "Starting dsh web (output -> $webLog)"
        Write-Log "  node $argString  (cwd=$workDir)"
        $child = Start-Process -FilePath $CONFIG_NODE_EXE `
            -ArgumentList $argString `
            -WorkingDirectory $workDir `
            -WindowStyle Hidden `
            -RedirectStandardOutput $webLog `
            -PassThru
    } else {
        Write-Log "Starting dsh web (no log capture)"
        Write-Log "  node $argString  (cwd=$workDir)"
        $child = Start-Process -FilePath $CONFIG_NODE_EXE `
            -ArgumentList $argString `
            -WorkingDirectory $workDir `
            -WindowStyle Hidden `
            -PassThru
    }

    # Remember the child we spawned. PID liveness is reliable even where
    # Get-CimInstance Win32_Process is denied (verified: it returns nothing from an
    # elevated host), whereas command-line fingerprinting silently degrades to 0.
    if ($child) {
        $script:CHILD_PID = $child.Id
        Write-Log "  child PID = $($child.Id)"
    } else {
        $script:CHILD_PID = $null
    }
    return $true
}

# Port Detection
function Test-PortListening {
    try {
        $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    } catch {
        Write-Log "WARN: port probe failed: $($_.Exception.Message)"
        return $false
    }
    foreach ($ep in $listeners) {
        if ($ep.Port -eq $CONFIG_PORT) { return $true }
    }
    return $false
}

# Process Detection.
#
# Primary signal is the PID we spawned ourselves: it needs no WMI and keeps
# working where CIM is denied. The command-line fingerprint remains as a
# best-effort fallback for hosts where process enumeration is permitted.
function Get-DshWebProcessCount {
    $matched = 0

    if ($script:CHILD_PID) {
        $alive = Get-Process -Id $script:CHILD_PID -ErrorAction SilentlyContinue
        if ($alive) { $matched++ }
    }

    try {
        $procs = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop
    } catch {
        # Denied from an elevated host; the PID check above still applies.
        return $matched
    }

    foreach ($p in $procs) {
        if ($script:CHILD_PID -and $p.ProcessId -eq $script:CHILD_PID) { continue }
        $cmdLine = if ($p.CommandLine) { $p.CommandLine } else { "" }
        $exePath = if ($p.ExecutablePath) { $p.ExecutablePath } else { "" }
        if (($cmdLine -imatch 'deepseek-ai') -and
            ($cmdLine -imatch 'bin\.js') -and
            ($cmdLine -imatch '\bweb\b') -and
            ($exePath -ieq $CONFIG_NODE_EXE)) {
            $matched++
        }
    }
    return $matched
}

# Restart Guard: launch grace window + exponential backoff
$script:lastLaunchAt   = $null
$script:consecFailures = 0
# PID of the child we spawned; the primary liveness signal.
$script:CHILD_PID      = $null

function Test-CanRestart {
    $now = Get-Date
    if ($script:lastLaunchAt) {
        $elapsed = ($now - $script:lastLaunchAt).TotalSeconds
        if ($elapsed -lt $CONFIG_LAUNCH_GRACE_SEC) {
            Write-Log "[GRACE] launched ${elapsed}s ago, holding until grace window ($CONFIG_LAUNCH_GRACE_SEC s) elapses"
            Start-Sleep -Seconds $CONFIG_PORT_CHECK_INTERVAL
            return $false
        }
    }
    if ($script:consecFailures -gt 1) {
        $backoff = [Math]::Min([Math]::Pow(2, $script:consecFailures), $CONFIG_RESTART_BACKOFF_MAX_SEC)
        Write-Log "[BACKOFF] ${backoff}s after $($script:consecFailures) consecutive failed launches"
        Start-Sleep -Seconds $backoff
    }
    return $true
}

function Invoke-Launch {
    if (-not (Test-CanRestart)) { return }
    $ok = Start-DshWeb
    $script:lastLaunchAt = Get-Date
    if ($ok) {
        Start-Sleep -Seconds $CONFIG_PROC_CHECK_INTERVAL
    } else {
        $script:consecFailures++
    }
}

# Main Daemon Loop
$script:STATE_WAITING = 0
$script:STATE_ACTIVE = 1

$portListening = Test-PortListening
if ($portListening) {
    $currentState = $script:STATE_ACTIVE
    Write-Log "Initial state: ACTIVE (port $CONFIG_PORT already listening, no action needed)"
} else {
    $currentState = $script:STATE_WAITING
    Write-Log "Initial state: WAITING (port $CONFIG_PORT not listening, will start dsh web)"
    Invoke-Launch
}

while ($true) {
    $portListening = Test-PortListening

    if ($currentState -eq $script:STATE_WAITING) {
        if ($portListening) {
            $currentState = $script:STATE_ACTIVE
            $script:consecFailures = 0
            Write-Log "STATE: WAITING -> ACTIVE (port $CONFIG_PORT now listening)"
            Start-Sleep -Seconds $CONFIG_DAEMON_PORT_ONLY_INTERVAL
            continue
        }

        # Not listening: respect the grace window before declaring failure
        if ($script:lastLaunchAt) {
            $since = ((Get-Date) - $script:lastLaunchAt).TotalSeconds
            if ($since -lt [double]$CONFIG_LAUNCH_GRACE_SEC) {
                Start-Sleep -Seconds $CONFIG_PORT_CHECK_INTERVAL
                continue
            }
            if ($since -ge [double]$CONFIG_STARTUP_TIMEOUT_SEC) {
                Write-Log "[WAITING] startup hard timeout ($([Math]::Round($since,1))s), forcing a fresh launch"
                Invoke-Launch
                if (-not $portListening) { Start-Sleep -Seconds $CONFIG_PORT_CHECK_INTERVAL }
                continue
            }
        }

        Start-Sleep -Seconds $CONFIG_PORT_CHECK_INTERVAL
        $procCount = Get-DshWebProcessCount

        if ($procCount -eq 0) {
            Write-Log "[WAITING] No dsh web process, restarting..."
            Invoke-Launch
        }
    } else {
        if ($portListening) {
            Start-Sleep -Seconds $CONFIG_DAEMON_PORT_ONLY_INTERVAL
        } else {
            $currentState = $script:STATE_WAITING
            Write-Log "STATE: ACTIVE -> WAITING (port $CONFIG_PORT no longer listening)"
            Write-Log "[WAITING] dsh web crashed or exited, will restart..."
            Invoke-Launch
        }
    }
}
