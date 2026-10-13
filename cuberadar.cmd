@echo off
rem Runs the built CLI (rebuild after code changes: dotnet build CubeRadar.slnx -c Release)
set "EXE=%~dp0src\CubeRadar.Cli\bin\Release\net10.0-windows\cuberadar.exe"
if not exist "%EXE%" dotnet build "%~dp0src\CubeRadar.Cli\CubeRadar.Cli.csproj" -c Release -v q || exit /b 1
"%EXE%" %*
