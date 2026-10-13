@echo off
rem Only the overlay: no browser, console hidden (log in the session folder), tray icon / gear button for settings and quit.
rem Builds first. A radar that is still closing gets up to 5 s, so the build is not blocked by its files.
setlocal
set "EXE=%~dp0src\CubeRadar.Cli\bin\Release\net10.0-windows\cuberadar.exe"
set tries=0
:wait
tasklist /FI "IMAGENAME eq cuberadar.exe" 2>nul | find /I "cuberadar.exe" >nul || goto build
set /a tries+=1
if %tries% geq 5 (
  echo Cube Radar laeuft noch. Bitte zuerst beenden: Rechtsklick auf das Radar-Symbol unten rechts in der Taskleiste - Beenden.
  exit /b 1
)
timeout /t 1 /nobreak >nul
goto wait
:build
dotnet build "%~dp0src\CubeRadar.Cli\CubeRadar.Cli.csproj" -c Release -v q -nologo >nul 2>&1 || (
  echo Build fehlgeschlagen. Details: dotnet build src\CubeRadar.Cli -c Release
  exit /b 1
)
start "Cube Radar" /min "%EXE%" radar --overlay-only
