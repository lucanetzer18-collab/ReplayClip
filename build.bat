@echo off
rem Baut ReplayClip.exe mit dem C#-Compiler, der in Windows (.NET Framework 4.8) schon eingebaut ist.
rem Keine Installation von Visual Studio o.ae. noetig.
setlocal
rem "build.bat ci" = Build-Server (GitHub Actions): nichts beenden, nichts starten, nicht warten
set CI=0
if /i "%~1"=="ci" set CI=1
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe nicht gefunden: %CSC%
  if "%CI%"=="0" pause
  exit /b 1
)
cd /d "%~dp0"
rem Laufende ReplayClip-Versionen (egal aus welchem Ordner) samt FFmpeg beenden, sonst ist die EXE gesperrt
if "%CI%"=="0" (
  taskkill /F /T /IM ReplayClip.exe >nul 2>&1
  timeout /t 1 /nobreak >nul
)
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /unsafe /optimize+ /win32icon:src\app.ico /out:ReplayClip.exe ^
  /resource:src\fonts\Inter-Regular.ttf,Inter-Regular.ttf /resource:src\fonts\Inter-Medium.ttf,Inter-Medium.ttf ^
  /resource:src\fonts\Inter-SemiBold.ttf,Inter-SemiBold.ttf /resource:src\fonts\Inter-Bold.ttf,Inter-Bold.ttf ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll /r:System.dll /r:System.Core.dll src\*.cs
if errorlevel 1 (
  echo.
  echo Build fehlgeschlagen.
  if "%CI%"=="0" pause
  exit /b 1
)
echo.
if "%CI%"=="1" (
  echo Fertig: ReplayClip.exe
  exit /b 0
)
echo Fertig: ReplayClip.exe - wird gestartet ...
start "" "%~dp0ReplayClip.exe" --replace
