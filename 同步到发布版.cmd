@echo off
rem ---------------------------------------------------------------------------
rem  Sync-Release  (Chinese label: "Tong Bu Dao Fa Bu Ban")
rem
rem  Copies the project's release artifacts into the in-project release copy
rem  dsh-web-launcher-daemon\, so the two stay identical.
rem
rem  Deliberately NOT copied:
rem    * DshLauncherDaemon_Config.ps1
rem    * DshLauncher\DshLauncher.json
rem  Both hold machine-specific absolute paths and are produced / fixed by
rem  Install and Initialization. Copying them would break a working install.
rem
rem  Does not touch the registry, the Startup folder, or request admin.
rem
rem  This .cmd is only a wrapper; the real logic lives in scripts\Sync-Release.ps1.
rem  Kept pure ASCII on purpose: a batch file is parsed using the console code
rem  page, so non-ASCII here would depend on the machine's locale.
rem ---------------------------------------------------------------------------

setlocal

set "PS1=%~dp0scripts\Sync-Release.ps1"

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
rem Pause only when double-clicked; stay silent when called from a console.
echo %cmdcmdline% | find /i "%~nx0" >nul
if not errorlevel 1 pause
exit /b %RC%