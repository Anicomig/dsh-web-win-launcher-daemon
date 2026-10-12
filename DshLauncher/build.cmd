@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
rem %~dp0 ends with backslash, so strip it for cleaner joins
set ROOT=%~dp0
if "%ROOT:~-1%"=="\" set ROOT=%ROOT:~0,-1%
rem one level up is the project root
set ICO=%ROOT%\..\assets\DshLauncher.ico
set ICO_LIGHT=%ROOT%\..\assets\DshLauncher_light.ico
set PROJDIR=%ROOT%\DshLauncher

rem --- pre-flight ---
if not exist "%ICO%" (
  echo ERROR: tray icon not found at "%ICO%" 1>&2
  exit /b 2
)
if not exist "%ICO_LIGHT%" (
  echo ERROR: light icon not found at "%ICO_LIGHT%" 1>&2
  exit /b 2
)
if not exist "%CSC%" (
  echo FAIL %CSC% not found 1>&2
  exit /b 3
)

if not exist "%ROOT%\dist" mkdir "%ROOT%\dist"

rem --- compile ---
rem Two icon resources are embedded: the dark-taskbar variant (white glyph) and
rem the light-taskbar variant (black glyph). The app picks one at startup from
rem HKCU\...\Themes\Personalize\SystemUsesLightTheme, so the tray icon stays
rem visible whichever theme the user runs.
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Management.dll ^
  /define:TRACE ^
  /out:"%ROOT%\dist\DshLauncher.exe" ^
  "%PROJDIR%\Program.cs" ^
  "%PROJDIR%\Tray\NotifyIconHost.cs" ^
  "%PROJDIR%\Tray\ContextMenuBuilder.cs" ^
  "%PROJDIR%\Tray\ConfigPageLauncher.cs" ^
  "%PROJDIR%\Tray\DaemonController.cs" ^
  "%PROJDIR%\Tray\DarkMenuRenderer.cs" ^
  "%PROJDIR%\Tray\DshWebLauncher.cs" ^
  "%PROJDIR%\Tray\TrayConfig.cs" ^
  "%PROJDIR%\Tray\DshWebScope.cs" ^
  "%PROJDIR%\Tray\TcpListenerProbe.cs" ^
  "%PROJDIR%\Properties\Resources.Designer.cs" ^
  /win32icon:"%ICO%" ^
  /resource:"%ICO%",DshLauncher.Properties.Resources.dshLauncherIcon ^
  /resource:"%ICO_LIGHT%",DshLauncher.Properties.Resources.dshLauncherIconLight

if errorlevel 1 (
  echo BUILD FAILED - see csc errors above 1>&2
  exit /b 1
)

rem --- mandatory integrity label ---
rem Files created by a low-integrity process inherit a LOW mandatory label, and
rem Windows then runs the resulting EXE at low integrity. A low-integrity process
rem cannot register a tray icon: Shell_NotifyIcon returns FALSE with
rem ERROR_ACCESS_DENIED (5), because UIPI blocks the cross-integrity message to
rem Explorer's tray window. .NET's NotifyIcon.Visible=true swallows that failure,
rem so the app appears healthy while never showing an icon.
rem Setting the label to Medium restores normal behaviour. Without this step the
rem tray icon silently disappears whenever the build ran from a sandboxed shell.
icacls "%ROOT%\dist\DshLauncher.exe" /setintegritylevel Medium >nul 2>&1

echo Built %ROOT%\dist\DshLauncher.exe
endlocal
