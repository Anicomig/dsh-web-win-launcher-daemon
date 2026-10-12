@echo off
rem ---------------------------------------------------------------------------
rem  Install.cmd
rem
rem  Run this on a new machine, under a new Windows user, or whenever you want
rem  to (re)install this folder. Works for both the release copy and the
rem  development copy.
rem
rem  It does:
rem    * fix everything that stores an absolute install path
rem        - DshLauncher\DshLauncher.json  (daemonExe)
rem        - autostart\LaunchDshLauncherDaemon.lnk   (daemon shortcut)
rem        - autostart\LaunchDshLauncher.lnk         (tray shortcut)
rem    * overwrite a Startup-folder .lnk ONLY if one is already there
rem      (it never adds a new autostart entry on its own)
rem    * create the log folder
rem    * regenerate ConfigHost\defaults.json from ConfigHost\default-config.ps1
rem    * run a full data check and report it
rem
rem  It does NOT touch DshLauncherDaemon_Config.ps1. Changing configuration
rem  happens either by editing that file yourself or in the DshConfigHost
rem  window; a full reset is what Fixed.cmd is for.
rem
rem  It does NOT register any protocol, does NOT write the registry and does
rem  NOT request admin rights.
rem
rem  This .cmd is only a wrapper; the real logic lives in scripts\Install.ps1.
rem  Kept pure ASCII on purpose: a batch file is parsed using the console code
rem  page, so non-ASCII here would depend on the machine's locale.
rem ---------------------------------------------------------------------------

setlocal

set "PS1=%~dp0scripts\Install.ps1"

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
if "%RC%"=="0" (echo [OK] Install finished.) else (echo [FAIL] Install reported problems - see output above.)
echo.
rem Pause unless DSH_NO_PAUSE is set. Deliberately NOT a command-line flag:
rem forwarding %* to the .ps1 would pass the flag through and PowerShell would
rem reject it as an unknown parameter.
if defined DSH_NO_PAUSE goto :eof
pause
exit /b %RC%
