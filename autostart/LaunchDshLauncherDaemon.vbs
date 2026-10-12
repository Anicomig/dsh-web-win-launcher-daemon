' LaunchDshLauncherDaemon.vbs - silent autostart for the DSH daemon.
' Resides in <project>\autostart\ and targets ..\DshLauncherDaemon.exe.
' Silent on success AND failure - no console, no dialogs.
Option Explicit
Dim shell, fso, scriptDir, exePath

Set shell = CreateObject("WScript.Shell")
Set fso   = CreateObject("Scripting.FileSystemObject")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
exePath   = fso.BuildPath(scriptDir, "..\DshLauncherDaemon.exe")

If Not fso.FileExists(exePath) Then
    ' Silent failure - no popup.
    WScript.Quit 1
End If

' 0 = hidden window, False = do not block this script
shell.Run """" & exePath & """", 0, False

Set fso = Nothing
Set shell = Nothing
WScript.Quit 0