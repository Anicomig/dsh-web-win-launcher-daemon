# ========= DSH Launcher Daemon 配置 ==========
#
# 此文件为 DSH（DeepSeek Harness）Web 守护进程的配置文件
# 修改后需重启守护进程才生效（配置仅在进程启动时读取一次）
#
# 调整配置有两条路：直接编辑本文件，或在 DshConfigHost 窗口里改。
# 改坏了可以用 Fixed.cmd 的「彻底重置配置」以默认模板覆盖本文件（会先备份）。
# 程序不申请管理员权限，不会弹 UAC。
# =================================================

# ---8<--- TEMPLATE-ONLY-BEGIN（这一段只属于模板，重置写入时会被删除）
# 本文件是默认模板，有两个下游用途：
#   1. Fixed.cmd 的「彻底重置配置」用它覆盖 DshLauncherDaemon_Config.ps1
#   2. ConfigHost\defaults.json 由它推导（DshConfigHost 的「恢复默认」用）
# 两处都从这里取字段，不再各自维护清单，避免和模板漂移。
#
# 下面 4 个 __DETECT_*__ 占位符在写入时替换成本机探测值：
#   __DETECT_NODE_EXE__    node.exe 完整路径
#   __DETECT_DSH_CLI__     dsh 的 bin.js 完整路径
#   __DETECT_DSH_HOME__    通常是 %USERPROFILE%\.dsh
#   __DETECT_WORKSPACE_DIR__   通常是 %USERPROFILE%\Documents\workspace
# 探测不到就保留占位符并明确报告 —— 而不是留下一个「看着对但错」的路径。
# ---8<--- TEMPLATE-ONLY-END

# --- 路径配置（与运行环境相关，换机器/换用户后需要重新设置） ---
$CONFIG_NODE_EXE    = "__DETECT_NODE_EXE__"
$CONFIG_DSH_CLI     = "__DETECT_DSH_CLI__"

# DSH 家目录：必须与手动运行 dsh 时一致，否则会指向另一个家目录（找不到 sessions / credentials）
$CONFIG_DSH_HOME    = "__DETECT_DSH_HOME__"

# 要启动的 dsh profile
$CONFIG_DSH_PROFILE = "web"

# dsh 的默认 workspace 根目录（子进程工作目录）
$CONFIG_WORKSPACE_DIR = "__DETECT_WORKSPACE_DIR__"

# --- 网络配置 ---
$CONFIG_PORT = 3080              # dsh web 默认端口
$CONFIG_HOST = "127.0.0.1"       # dsh web 不支持 0.0.0.0（会被硬拒）

# --- 启动参数 ---
$CONFIG_NO_OPEN    = $true       # 追加 --no-open：守护进程不应弹浏览器
$CONFIG_EXTRA_ARGS = @()         # 追加参数，例如 @("--trusted-host", "foo:3080")

# --- 守护配置 ---
$CONFIG_PORT_CHECK_INTERVAL       = 2     # WAITING 状态端口检测间隔（秒）
$CONFIG_PROC_CHECK_INTERVAL       = 3     # WAITING 状态进程检测间隔（秒）
$CONFIG_DAEMON_PORT_ONLY_INTERVAL = 15    # ACTIVE 状态空闲轮询间隔（秒）
$CONFIG_LAUNCH_GRACE_SEC          = 120   # 启动宽限期：期间端口未起也不重启（冷启动很慢）
$CONFIG_STARTUP_TIMEOUT_SEC       = 180   # 硬上限：超过此时间仍未监听则强制重启
$CONFIG_RESTART_BACKOFF_MAX_SEC   = 300   # 连续失败时的退避上限（秒）

# --- 日志捕获 ---
$CONFIG_GATEWAY_LOG_ENABLED = $true   # 是否将 dsh web 的 stdout 写入独立日志文件
                                      # 注：dsh web 就绪时会打印 "dsh web: http://127.0.0.1:3080/?token=..."
                                      #     该行会被此日志捕获（进程退出时落盘），可用于事后取证

# --- 日志配置 ---
# 守护进程自身日志：log 子文件夹（dsh_daemon_YYYY-MM-DD-HH-mm_ss.log）
# dsh web 输出日志：每次启动/重启创建独立时间戳文件（web_YYYY-MM-DD-HH-mm_ss.log）
# DSH 结构化日志：%DSH_HOME%\logs\
