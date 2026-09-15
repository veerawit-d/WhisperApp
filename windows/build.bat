@echo off
rem Build WhisperApp for Windows - no SDK needed, uses the Roslyn compiler
rem bundled with Visual Studio Build Tools (falls back to the .NET Framework compiler).
setlocal

set "CSC=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist "%CSC%" set "CSC=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist "%CSC%" set "CSC=C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist "%CSC%" set "CSC=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

set "FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

"%CSC%" /nologo /noconfig /nostdlib+ /target:winexe /platform:anycpu /optimize+ /langversion:Default /codepage:65001 /out:"%~dp0WhisperApp.exe" /r:"%FW%\mscorlib.dll" /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Net.Http.dll" /r:"%FW%\System.Web.Extensions.dll" "%~dp0src\*.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: %~dp0WhisperApp.exe
