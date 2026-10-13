@echo off
rem Builds incrementally (a few seconds) and runs the CLI. If the build cannot run (e.g. a radar is still open and
rem holds the files), the existing build is used.
set "EXE=%~dp0src\CubeRadar.Cli\bin\Release\net10.0-windows\cuberadar.exe"
dotnet build "%~dp0src\CubeRadar.Cli\CubeRadar.Cli.csproj" -c Release -v q -nologo >nul 2>&1 || echo (Build uebersprungen - Dateien in Benutzung oder Build-Fehler. Starte vorhandene Version.)
"%EXE%" %*
