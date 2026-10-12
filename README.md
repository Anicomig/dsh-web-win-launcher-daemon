# DSH Web Launcher Daemon (Windows)

A small, no-admin, no-installer Windows daemon + tray app that keeps
[DeepSeek Harness](https://github.com/deepseek-ai/dsh)'s `dsh web` GUI running
and gives you a right-click tray menu to control it.

- **Daemon** (`DshLauncherDaemon.exe`, ~50 KB, PS2EXE-built) — silently supervises
  `node "<dsh>\lib\bin.js" web …`, restarts on crash, captures the boot-marker
  log line, and exits cleanly with no console window.
- **Tray** (`DshLauncher.exe`, ~100 KB, csc-built) — WinForms system-tray icon
  with a dark rounded menu: open the web UI (browser or installed Edge PWA),
  start/stop the daemon, start/stop/restart `dsh web`, open the log folder,
  launch the configuration window, exit.
- **Config host** (`ConfigHost\DshConfigHost.exe`, ~60 MB self-contained
  single-file) — a WebView2-based local window for editing the daemon's
  `.ps1` config, switching launch behaviour, and managing the two startup
  shortcuts.

> **Designed to be moved.** Every absolute path the runtime needs is rewritten
> by `Initialization.cmd` against the *current* folder, so you can copy the
> whole tree to a new location and just rerun that one script.

## Windows prerequisites

- **Windows 10 (1803 or later) or Windows 11.** Win11 already ships everything
  you need; on Win10 you may need to install two things.
- **Microsoft Edge WebView2 Runtime.** Preinstalled on Win11 and most Win10
  1803+ systems. If the config window complains, install it from
  <https://developer.microsoft.com/microsoft-edge/webview2/> (Evergreen
  Standalone Installer, ~110 MB, per-user).
- **[Node.js](https://nodejs.org/) ≥ 18.** Required because `dsh web` is a
  Node app. `node` must be on `PATH` **or** the config window's
  *Path → Node executable* field must point at it.
- **[`dsh` (DeepSeek Harness)](https://github.com/deepseek-ai/dsh) installed
  via npm.** This is the program being supervised:
  ```cmd
  npm install -g @deepseek-ai/dsh
  ```
  `Install.cmd` will auto-discover `<npm-global>\node_modules\@deepseek-ai\dsh\lib\bin.js`
  on first run.

> No administrator rights are requested at any point. No registry keys are
> written. No custom URL protocol is registered. The two startup-folder
> shortcuts live in the **current user's** Startup folder only.

## Install & first run

1. **Unzip** `dsh-web-launcher-daemon-v0.1.0.zip` anywhere you like
   (e.g. `D:\tools\dsh-web-launcher-daemon`). Avoid paths that need admin to
   write to.
2. **Double-click `Install.cmd`.** It will:
   - verify the daemon, tray and config-host binaries are present;
   - generate `ConfigHost\defaults.json` from the template, with `node.exe`,
     `dsh` CLI, `~/.dsh` and `~/Documents/workspace` auto-detected;
   - rebuild the three `.lnk` shortcuts (`DshLauncher.lnk` in the root,
     `LaunchDshLauncher*.lnk` in `autostart\`) so they point at the current
     folder;
   - run a full integrity report (33 self-checks).
3. **Double-click `DshLauncher.lnk`.** The tray icon appears in the
   notification area (it may be folded under the `^` overflow chevron).
4. **Right-click the tray → 启动后台 Daemon** (Start background daemon). The
   daemon starts silently; after a few seconds, **right-click → 打开网页 (3080)**
   opens the DSH web UI in your default browser.

> The first time you click **打开网页 (3080)** the page will ask you to log in
> — that's `dsh web`'s own auth, not this daemon. After that it remembers you.

## What the four entry scripts do

| Script | When to use it | Will it touch `DshLauncherDaemon_Config.ps1`? |
|---|---|---|
| **`Install.cmd`** | New machine, new user, or just to verify everything is in order | **No** |
| **`Initialization.cmd`** | You moved or renamed the installation folder | **No** |
| **`uninstall.cmd`** | Three-stage teardown, each stage asks (defaults to "no") | Stage 3 deletes it |
| **`Fixed.cmd`** | Repair corrupt data; the only script that can fully overwrite the config (after a backup) | **Yes** (and it backs up first) |

Changing the daemon config is **only** done by editing
`DshLauncherDaemon_Config.ps1` directly or through the `ConfigHost` window.
`Install.cmd` and `Initialization.cmd` will never silently overwrite it.

## Launching the DSH web UI

The tray menu item comes in two flavours, controlled by the configuration
window's *Run parameters → Launch mode* field:

| Tray menu item | Behaviour |
|---|---|
| `打开网页 (3080)` | Open `http://127.0.0.1:<port>/` in the system default browser |
| `打开网页应用 (3080)` | Open in an installed Edge PWA window via `msedge --app-id=<id>` (no address bar, looks like a desktop app) |

Switching is **immediate** — no save button — because the value lives in
`DshLauncher\DshLauncher.json`, not in the `.ps1` config. The tray re-reads
that file on every right-click, so the menu text updates without a restart.

When you pick the Edge PWA option, the config window reads Edge's session
store (read-only, shared file lock; never touches cookies, history, logins
or bookmarks) to find the matching `app-id`. If nothing is found, it walks
you through *open the URL → ⋮ → Apps → Install this site as an app → open
it once → retry*.

## Configuration

`ConfigHost\DshConfigHost.exe` opens a dark, single-window editor for the
`DshLauncherDaemon_Config.ps1` file. Five groups:

- **Run parameters** — port, host, profile, `dsh` extra args, launch mode
- **Paths** — `node.exe`, `dsh` CLI, `~/.dsh`, workspace root
- **Polling & timeouts** — five intervals, grace period, hard timeout
- **Startup** — toggle the two startup shortcuts (state is read from the real
  Startup folder, not from a cached flag)
- **Environment** — read-only diagnostic info

Path fields are validated on the fly (green check / red ×). The "Save & restart"
button writes the file **and** restarts the daemon in one step, because
configuration is read only at startup.

The full manual is in [`docs\config-host.md`](docs/config-host.md).

## Startup on boot

The configuration window has two independent switches under *Startup*:

- **Background daemon** — copies `autostart\LaunchDshLauncherDaemon.lnk`
  into `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`. That
  shortcut points at `autostart\LaunchDshLauncherDaemon.vbs`, which silently
  launches `DshLauncherDaemon.exe` with no window, no console, no error
  dialog.
- **Tray program** — copies `autostart\LaunchDshLauncher.lnk` (points
  directly at `DshLauncher.exe`) into the same Startup folder.

The status shown next to each switch is the **actual** state of the
Startup folder, not a cached flag. Toggling deletes the file; the
folder state is then re-read.

`Initialization.cmd` only overwrites the Startup-folder copies **if they
already exist** — it never adds a startup item you didn't already have.

## Uninstall

Double-click `uninstall.cmd`. Three stages, each asks, all default to "no":

1. **Install data** (outside the folder): the WebView2 cache in
   `%LOCALAPPDATA%\DshConfigHost\`, the two Startup-folder `.lnk` files, and
   any leftover `HKCU\Software\Classes\dshlauncher` key from a much older
   version.
2. **Installation folder** contents: clears this folder but **keeps
   `_backup\`**; requires you to type a confirmation word.
3. **User data**: the `log\` directory and the
   `DshLauncherDaemon_Config.ps1` file (auto-skipped if stage 2 already
   deleted the folder).

## Source layout

```
DshLauncherDaemon.exe          daemon, ~50 KB
DshLauncherDaemon_Config.ps1   runtime config (you edit this or use ConfigHost)
DshLauncherDaemon.ps1          daemon source (used to rebuild the EXE)
DshLauncher\                   tray program + config
ConfigHost\                    config window + wwwroot + default-config.ps1
autostart\                     two startup .lnk templates + silent-launch VBS
assets\                        tray icons
scripts\                       Install / Initialization / uninstall / Fixed
docs\                          user documentation
tools\                         headless page-logic tests (Node, no deps)
src\                           full source tree, kept in sync for rebuilds
```

`src\Tools\test-page-logic.mjs` runs the config window's pure page logic
(16 Node assertions, no DOM, no build step):

```cmd
node src\tools\test-page-logic.mjs
```

## Rebuilding from source

| What | Command | Needs |
|---|---|---|
| Tray (`DshLauncher.exe`) | `cd DshLauncher && build.cmd` | nothing extra — uses the in-box `csc.exe` |
| Config host (`DshConfigHost.exe`) | `ConfigHost\build.cmd` | .NET SDK (tested with 10.0.401) |
| Daemon (`DshLauncherDaemon.exe`) | `scripts\Sync-Release.ps1` then PS2EXE (see `README_DSH.md`) | [PS2EXE](https://github.com/MScholtes/PS2EXE) module, then `icacls … /setintegritylevel Medium` |

## Known Windows pitfalls (and why the project is shaped this way)

- **WebView2 Runtime** is the one external dependency for the config window.
  `DshConfigHost.exe` itself is a 60 MB self-contained single-file .NET app,
  so no .NET Runtime install is required.
- **`.ps1` files must be UTF-8 with BOM.** Windows PowerShell 5.1 decodes
  a BOM-less file with the system ANSI codepage, which turns any Chinese
  comment into mojibake and breaks here-string boundaries with a misleading
  "missing terminator" error pointing at the wrong line. The two header
  notes in `scripts\README.md` and `docs\config-host.md` describe the
  four pitfalls the project has actually been bitten by.
- **Tray EXE must run at Medium integrity.** If the build process is Low
  integrity the resulting EXE inherits that label and `Shell_NotifyIcon`
  silently fails — the process runs, the log is clean, and the icon
  *never* appears. `DshLauncher\build.cmd` runs
  `icacls dist\DshLauncher.exe /setintegritylevel Medium` automatically.
- **Tray icons must use the classic DIB format** (16/32/48 DIB + 256 PNG).
  `csc.exe` cannot read a 256-only PNG `.ico`. See
  `DshLauncher\README.md` §"三个必须知道的实现约束" for the full story.

## Diagnostics

`ConfigHost\DshConfigHost.exe` has four non-interactive switches for
reproducible testing. None of them change persistent state; `--page-test-write`
backs up and restores the JSON it touches.

```cmd
ConfigHost\DshConfigHost.exe --selftest          :: 33 bridge-method checks
ConfigHost\DshConfigHost.exe --page-test         :: 76 in-page assertions (no writes)
ConfigHost\DshConfigHost.exe --page-test-write   :: 76 + 2 real-save + restore
ConfigHost\DshConfigHost.exe --print-env         :: dump resolved install root, version, paths
```

## License

This project is a thin supervisor for `dsh web` and contains no code from
DeepSeek Harness itself. The two external runtimes it depends on — Node.js
and WebView2 — are governed by their own licences.
