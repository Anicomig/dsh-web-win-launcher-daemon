@echo off
rem ---------------------------------------------------------------------------
rem  Initialization.cmd
rem
rem  Run this ONCE after you have MOVED or RENAMED the whole installation
rem  folder, so that everything pointing at the old absolute path is fixed and
rem  the install works again.
rem
rem  It does:
rem    * DshLauncher\DshLauncher.json   -> daemonExe points at the new location
rem    * rebuild both autostart sources
rem        - autostart\LaunchDshLauncherDaemon.lnk
rem        - autostart\LaunchDshLauncher.lnk
rem    * overwrite a Startup-folder .lnk ONLY if one is already there.
rem      If the Startup folder has no such entry, nothing is added: whether to
rem      autostart is decided in the DshConfigHost window, not here.
rem
rem  It does NOT touch DshLauncherDaemon_Config.ps1 - the paths inside it are
rem  machine/user specific, not folder-location specific. Use Install.cmd when
rem  you move to a different machine or Windows user.
rem
rem  It does NOT generate defaults.json (that file holds no install-folder
rem  paths, so it stays valid after a move).
rem
rem  No protocol registration, no registry writes, no admin rights.
rem
rem  Wrapper only; logic lives in scripts\Initialization.ps1.
rem  Pure ASCII on purpose (batch files are parsed with the console code page).
rem ---------------------------------------------------------------------------

setlocal

set "PS1=%~dp0scripts\Initialization.ps1"

if not exist "%PS1%" (
    echo.
    echo [ERROR] Not found: "%PS1%"
    echo         Make sure the scripts folder sits next to this .cmd file.
    echo.
    set "RC=1"
    goto :done
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

:done
echo.
if "%RC%"=="0" (echo [OK] Initialization finished.) else (echo [FAIL] Initialization failed - see output above.)
echo.
rem Pause unless DSH_NO_PAUSE is set. Deliberately NOT a command-line flag:
rem forwarding %* to the .ps1 would pass the flag through and PowerShell would
rem reject it as an unknown parameter.
if defined DSH_NO_PAUSE goto :eof
pause
exit /b %RC%
