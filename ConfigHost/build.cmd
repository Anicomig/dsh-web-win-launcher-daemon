@echo off
setlocal
cd /d "%~dp0"

rem Build DshConfigHost as a self-contained single-file exe.
rem Body is ASCII-only on purpose: a .cmd with Chinese needs GBK+CRLF, which is
rem easy to break. This file lives INSIDE ConfigHost, so every path below is
rem relative to ConfigHost itself.

set PROJ=src\ConfigHost.csproj
set OUT=src\bin\Release\publish

echo [1/4] removing stale framework-dependent output ...
del /q "DshConfigHost.dll"                    2>nul
del /q "DshConfigHost.deps.json"              2>nul
del /q "DshConfigHost.runtimeconfig.json"     2>nul
del /q "Microsoft.Web.WebView2.Core.dll"      2>nul
del /q "Microsoft.Web.WebView2.WinForms.dll"  2>nul
del /q "Microsoft.Web.WebView2.Wpf.dll"       2>nul
del /q "*.xml"                                2>nul
rmdir /s /q "runtimes"                        2>nul

echo [2/4] dotnet publish (self-contained, single file, compressed) ...
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true ^
  /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true ^
  /p:DebugType=none /p:DebugSymbols=false ^
  -o "%OUT%"
if errorlevel 1 goto :fail

echo [3/4] staging into ConfigHost ...
copy /y "%OUT%\DshConfigHost.exe"           "DshConfigHost.exe"           >nul
copy /y "%OUT%\WebView2Loader.dll"          "WebView2Loader.dll"          >nul
copy /y "%OUT%\D3DCompiler_47_cor3.dll"     "D3DCompiler_47_cor3.dll"     >nul
copy /y "%OUT%\PenImc_cor3.dll"             "PenImc_cor3.dll"             >nul
copy /y "%OUT%\PresentationNative_cor3.dll" "PresentationNative_cor3.dll" >nul
copy /y "%OUT%\vcruntime140_cor3.dll"       "vcruntime140_cor3.dll"       >nul
copy /y "%OUT%\wpfgfx_cor3.dll"             "wpfgfx_cor3.dll"             >nul
rem wwwroot is NOT copied: page assets live in ConfigHost\wwwroot already, which is
rem the publish source. Copying it onto itself is a no-op at best.

echo [4/4] result
for %%F in ("DshConfigHost.exe") do echo       exe bytes: %%~zF
goto :eof

:fail
echo.
echo BUILD FAILED - see the messages above
exit /b 1
