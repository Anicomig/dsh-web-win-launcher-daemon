@echo off
rem ---------------------------------------------------------------------------
rem  uninstall.cmd
rem
rem  Asks three separate questions and defaults to "no" on all of them.
rem
rem    [1] Clean INSTALL DATA - things written OUTSIDE this folder:
rem          %LOCALAPPDATA%\DshConfigHost   (WebView2 cache, regenerable)
rem          the two autostart .lnk in the Startup folder (exact names only)
rem          legacy dshlauncher:// registry key (new versions never register it)
rem
rem    [2] Delete the INSTALL FOLDER contents (keeps _backup\, needs a typed
rem        confirmation word).
rem
rem    [3] Clean USER DATA - what you produced on this machine:
rem          log\  and  DshLauncherDaemon_Config.ps1
rem        Automatically SKIPPED when [2] already deleted the folder, because
rem        then it would be pointless.
rem
rem  No admin rights required. Never deletes anything without you saying yes.
rem
rem  Wrapper only; logic lives in scripts\Uninstall.ps1.
rem  Pure ASCII on purpose (batch files are parsed with the console code page).
rem ---------------------------------------------------------------------------

setlocal

set "PS1=%~dp0scripts\Uninstall.ps1"

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
echo [uninstall.cmd] Done (exit code %RC%).
echo.
rem Pause unless DSH_NO_PAUSE is set. Deliberately NOT a command-line flag:
rem forwarding %* to the .ps1 would pass the flag through and PowerShell would
rem reject it as an unknown parameter.
if defined DSH_NO_PAUSE goto :eof
pause
exit /b %RC%
