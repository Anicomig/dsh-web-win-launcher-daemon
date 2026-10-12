# DSH Web Launcher Daemon (Windows)

[中文](README.md) | [English](README.en.md)

A small Windows daemon + tray app that keeps
[DeepSeek Harness](https://github.com/deepseek-ai/dsh)'s `dsh web` GUI running.
**No installer, no administrator rights** — everything lives in the right-click
tray menu.

- **Daemon** (`DshLauncherDaemon.exe`, ~50 KB, PS2EXE-built) — silently
  supervises `node "<dsh>\lib\bin.js" web …`: restarts it on crash, captures
  the boot-marker log line, and runs with no console window at all.
- **Tray** (`DshLauncher.exe`, ~100 KB, csc-built) — WinForms system-tray icon
  with a dark rounded menu: open the web UI (default browser or installed Edge
  PWA), start/stop the daemon, start/restart/stop `dsh web`, open the log
  folder, launch the configuration window, exit.
- **Config host** (`ConfigHost\DshConfigHost.exe`, self-contained single file,
  ~60 MB) — a WebView2-based local window for editing the daemon's `.ps1`
  config, switching the launch mode, and managing the two startup switches.

> **The whole folder is movable.** Every absolute path the runtime needs is
> rewritten by `Initialization.cmd` against the *current* location — move or
> rename the folder, run that one script, done.

## Windows prerequisites

- **Windows 10 (1803 or later) or Windows 11.** Win11 ships everything you
  need; on Win10 you may have to install the two items below.
- **Microsoft Edge WebView2 Runtime.** Preinstalled on Win11 and most Win10
  1803+ systems. If the config window complains, install it from
  <https://developer.microsoft.com/microsoft-edge/webview2/> (Evergreen
  Standalone Installer, ~110 MB, per-user).
- **[Node.js](https://nodejs.org/) ≥ 18.** `dsh web` is a Node app. `node`
  must be on `PATH`, **or** point the config window's *Paths → Node
  executable* field at it.
- **[`dsh`](https://github.com/deepseek-ai/dsh) installed via npm** — this is
  the program being supervised:
  ```cmd
  npm install -g @deepseek-ai/dsh
  ```
  `Install.cmd` auto-discovers
  `<npm-global-root>\node_modules\@deepseek-ai\dsh\lib\bin.js` on first run.

> No administrator rights are requested at any point. No registry keys are
> written. No custom URL protocol is registered. The two startup shortcuts
> live in the **current user's** Startup folder only.

## Install & first run

1. **Unzip** `dsh-web-launcher-daemon-v0.1.0.zip` anywhere you like
   (e.g. `D:\tools\dsh-web-launcher-daemon`). Avoid paths that require admin
   rights to write to.
2. **Double-click `Install.cmd`.** It will:
   - verify the daemon, tray and config-host binaries are present;
   - generate `ConfigHost\defaults.json` and `DshLauncherDaemon_Config.ps1`
     from the default template, auto-detecting this machine's `node.exe`,
     `dsh` CLI, `~\.dsh` and `Documents\workspace`;
   - rebuild the three `.lnk` shortcuts (root `DshLauncher.lnk` plus the two
     autostart sources under `autostart\`) so they point at the current folder;
   - run a full integrity report.
3. **Double-click `DshLauncher.lnk`** to start the tray. The icon appears in
   the notification area (it may be folded under the `^` overflow chevron);
   right-click it for the menu.
4. **Right-click the tray → "启动后台 Daemon"** (Start background daemon). The
   daemon starts silently; after a few seconds, **right-click → "打开网页
   (3080)"** opens the DSH web UI in your default browser.

> The first time you open the page it asks you to log in — that is `dsh web`'s
> own authentication, unrelated to this daemon. After that it remembers you.

*(The tray and config-window UI text is currently Chinese-only; the labels
quoted above are what you will see on screen.)*

## What the four entry scripts do

| Script | When to use it | Will it touch `DshLauncherDaemon_Config.ps1`? |
|---|---|---|
| **`Install.cmd`** | New machine, new user, or just to verify everything is in order | **No** (it only creates the file from the template if it is missing) |
| **`Initialization.cmd`** | You moved or renamed the installation folder | **No** |
| **`uninstall.cmd`** | Three-stage teardown, each stage asks (defaults to "no") | Stage 3 deletes it |
| **`Fixed.cmd`** | Repair corrupt data; the only script that can fully overwrite the config | **Yes** (and it backs up first) |

Changing the daemon config happens **only** by editing
`DshLauncherDaemon_Config.ps1` directly or through the ConfigHost window.
`Install.cmd` and `Initialization.cmd` never silently overwrite it.

## Launch mode: browser vs Edge PWA

The tray menu item comes in two flavours, controlled by the config window's
*Run parameters → Launch mode*:

| Tray menu item | Behaviour |
|---|---|
| `打开网页 (3080)` | Open `http://127.0.0.1:<port>/` in the system default browser |
| `打开网页应用 (3080)` | Open the installed Edge PWA in its own window (`msedge --app-id=<id>` — no address bar, looks like a desktop app) |

Switching is **immediate — there is no save button** — because the value lives
in `DshLauncher\DshLauncher.json`, not in the `.ps1` config. The tray re-reads
that file on every right-click, so the menu text updates without restarting
the tray.

When you pick the Edge PWA option, the config window reads Edge's session
store (**read-only**, shared-lock access; it never touches cookies, history,
logins or bookmarks) to find the matching `app-id`. Nothing found usually
means the app has never been *opened* — the window then walks you through:
open the URL → ⋮ menu → "Install this site as an app" → open it once → click
"re-detect".

## Configuration window

`ConfigHost\DshConfigHost.exe` is a dark single-window editor over
`DshLauncherDaemon_Config.ps1`, grouped into five sections:

- **Run parameters** — port, host, profile, extra `dsh` args, launch mode
- **Paths** — `node.exe`, `dsh` CLI, DSH home directory, workspace root
- **Polling & timeouts** — five interval/timeout values
- **Startup** — two switches for daemon / tray autostart (badges show the
  **actual** state of the Startup folder, not a cached flag)
- **Environment** — read-only diagnostic info

Path fields validate on the fly (green check / red ×). "Save & restart"
writes the file **and** restarts the daemon in one step, because the config
is read only at process startup.

Full manual: [`docs/config-host.md`](docs/config-host.md) (Chinese).

## Start on boot

Two independent switches in the config window:

- **Background daemon** — copies `autostart\LaunchDshLauncherDaemon.lnk` into
  `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`. That shortcut
  points at `autostart\LaunchDshLauncherDaemon.vbs`, which launches
  `DshLauncherDaemon.exe` silently: no window, no console, no dialog on
  success *or* failure.
- **Tray program** — copies `autostart\LaunchDshLauncher.lnk` (pointing
  directly at `DshLauncher.exe`) into the same Startup folder.

Turning a switch off deletes the file, then re-reads the folder state.
`Initialization.cmd` only overwrites Startup-folder copies **if they already
exist** — it never adds a startup item you didn't already have.

## Uninstall

Double-click `uninstall.cmd`. Three stages, each asks, all default to "no":

1. **Install data** (written outside the folder): the WebView2 cache in
   `%LOCALAPPDATA%\DshConfigHost\` (regenerable, contains no configuration),
   the two Startup-folder `.lnk` files, and any leftover
   `HKCU\Software\Classes\dshlauncher` key from a much older version.
2. **Installation folder** contents: clears this folder but **keeps
   `_backup\`**; requires typing a confirmation word.
3. **User data**: the `log\` directory and `DshLauncherDaemon_Config.ps1`
   (auto-skipped if stage 2 already deleted the folder).

## Source layout

```
DshLauncherDaemon.ps1          daemon source (EXE built from it via PS2EXE)
DshLauncher\                   tray program source + build.cmd
ConfigHost\                    config window source + wwwroot + default-config.ps1 template
autostart\                     silent-launch VBS (.lnk files generated by Install, not committed)
assets\                        tray icons
scripts\                       implementations of Install / Initialization / uninstall / Fixed
docs\                          user documentation
tools\                         headless page-logic tests (Node, no deps)
```

Run the page-logic test suite (16 assertions, no build step):

```cmd
node tools\test-page-logic.mjs
```

## Rebuilding from source

| Artifact | Command | Needs |
|---|---|---|
| Tray (`DshLauncher.exe`) | `cd DshLauncher && build.cmd` | nothing extra — uses the in-box `csc.exe` |
| Config host (`DshConfigHost.exe`) | `ConfigHost\build.cmd` | .NET SDK (tested with 10.0.401) |
| Daemon (`DshLauncherDaemon.exe`) | PS2EXE-build `DshLauncherDaemon.ps1` (see `README_DSH.md`) | [PS2EXE](https://github.com/MScholtes/PS2EXE), then `icacls … /setintegritylevel Medium` |

All three prebuilt executables ship in the
[Releases](https://github.com/Anicomig/dsh-web-win-launcher-daemon/releases)
zip; the git repository contains source only.

## Known Windows pitfalls (and why this project is shaped the way it is)

- **WebView2 Runtime** is the config window's only external dependency;
  `DshConfigHost.exe` itself is a 60 MB self-contained single-file .NET app,
  so no .NET Runtime install is required.
- **`.ps1` files must be UTF-8 with BOM.** Windows PowerShell 5.1 decodes a
  BOM-less file with the system ANSI codepage, turning Chinese comments into
  mojibake and breaking here-string boundaries — with a misleading "missing
  terminator" error pointing at the wrong line. `scripts\README.md` and
  `docs\config-host.md` record the four pitfalls this project actually got
  bitten by.
- **The tray EXE must run at Medium integrity.** If the build process is Low
  integrity, the produced EXE inherits that label and `Shell_NotifyIcon`
  fails **silently** — the process lives, the log is clean, and the icon
  *never* appears. `DshLauncher\build.cmd` automatically runs
  `icacls dist\DshLauncher.exe /setintegritylevel Medium`.
- **Tray icons must use the classic DIB format** (16/32/48 DIB + 256 PNG).
  `csc.exe` cannot read a PNG-only small-size icon entry. See
  `DshLauncher\README.md`, section "三个必须知道的实现约束" (Chinese).

## Diagnostic switches

`ConfigHost\DshConfigHost.exe` accepts non-interactive switches for
reproducible self-checks. None change persistent state
(`--page-test-write` restores every byte it touched before exiting):

```cmd
ConfigHost\DshConfigHost.exe --selftest          :: 33 bridge-method checks
ConfigHost\DshConfigHost.exe --page-test         :: 76 in-page assertions (no writes)
ConfigHost\DshConfigHost.exe --page-test-write   :: 76 + 2 real-save round-trips, restored
ConfigHost\DshConfigHost.exe --print-env         :: dump resolved install root, version, paths
```

## License

This project is a thin supervisor for `dsh web` and contains no code from
DeepSeek Harness itself. The two external runtimes it depends on — Node.js
and WebView2 — are governed by their own licences.
