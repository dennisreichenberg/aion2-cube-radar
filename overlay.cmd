@echo off
rem Only the overlay: no browser, console hidden (log in the session folder), tray icon to open the web view or quit.
rem Builds first, like cuberadar.cmd.
set "EXE=%~dp0src\CubeRadar.Cli\bin\Release\net10.0-windows\cuberadar.exe"
dotnet build "%~dp0src\CubeRadar.Cli\CubeRadar.Cli.csproj" -c Release -v q -nologo >nul 2>&1 || echo (Build uebersprungen - Dateien in Benutzung oder Build-Fehler. Starte vorhandene Version.)
start "Cube Radar" /min "%EXE%" radar --overlay-only
