@echo off
rem ---------------------------------------------------------------------------
rem  Fixed.cmd - repair data the user may have broken by editing it
rem
rem  Offers five repairs and backs up every file it is about to touch into
rem  _backup\<timestamp>-fixed\ first:
rem
rem    1) Fully reset DshLauncherDaemon_Config.ps1 from
rem       ConfigHost\default-config.ps1, re-detecting the four
rem       machine-specific paths. This is the ONLY entry point that overwrites
rem       the configuration file - Install.cmd and Initialization.cmd never do.
rem    2) Rebuild ConfigHost\defaults.json from the same template
rem    3) Rebuild both autostart shortcut sources in autostart\
rem    4) Fix DshLauncher\DshLauncher.json (daemonExe / webPort)
rem    5) Normalise the config file's encoding and line endings
rem       (UTF-8 with BOM + pure LF) without changing any value
rem
rem  Afterwards it runs the full data check again and reports what is left.
rem  No admin rights required.
rem
rem  Wrapper only; logic lives in scripts\Fixed.ps1.
rem  Pure ASCII on purpose (batch files are parsed with the console code page).
rem ---------------------------------------------------------------------------

setlocal

set "PS1=%~dp0scripts\Fixed.ps1"

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
echo [Fixed.cmd] Done (exit code %RC%).
echo.
rem Pause unless DSH_NO_PAUSE is set. Deliberately NOT a command-line flag:
rem forwarding %* to the .ps1 would pass the flag through and PowerShell would
rem reject it as an unknown parameter.
if defined DSH_NO_PAUSE goto :eof
pause
exit /b %RC%
