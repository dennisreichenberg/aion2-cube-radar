# Builds a self-contained test package (no .NET install needed on the target PC):
#   dist\cuberadar-<version>-win-x64.zip  =  cuberadar.exe + wwwroot\ + config\ + LIESMICH.txt + LICENSE + source.zip
# Leaves out everything personal or third-party: captures\, data\ (radar.db, th.gl spot list), fixtures\, docs\,
# tests\ and START_PROMPT.md (they contain the own character, IPs and traffic).
param([string]$Out = "dist")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $root "src\CubeRadar.Cli\CubeRadar.Cli.csproj"
$version = ([xml](Get-Content $cli)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "cuberadar-$version-win-x64"
$dist = Join-Path $root $Out
$stage = Join-Path $dist $name

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet publish $cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force (Join-Path $stage "config") | Out-Null
Copy-Item (Join-Path $root "config\*.json") (Join-Path $stage "config")
Copy-Item (Join-Path $root "LICENSE") $stage
Copy-Item (Join-Path $root "docs\LIESMICH_TESTER.txt") (Join-Path $stage "LIESMICH.txt")
Set-Content -Path (Join-Path $stage "Radar starten.cmd") -Encoding ascii -Value @(
    "@echo off",
    "cd /d `"%~dp0`"",
    "cuberadar.exe radar",
    "pause")
Set-Content -Path (Join-Path $stage "Nur Overlay starten.cmd") -Encoding ascii -Value @(
    "@echo off",
    "cd /d `"%~dp0`"",
    "start `"Cube Radar`" /min cuberadar.exe radar --overlay-only")

# Corresponding source (GPL-3.0): the committed code, without personal data.
git -C $root archive --format=zip -o (Join-Path $stage "source.zip") HEAD -- `
    src tools config LICENSE README.md CubeRadar.slnx cuberadar.cmd .gitignore
if ($LASTEXITCODE -ne 0) { throw "git archive failed" }

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
