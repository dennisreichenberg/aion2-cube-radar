# Builds "Cube Radar.exe": one self-contained file (.NET, web UI and default config inside), a Windows app without a
# console window; a double-click starts the overlay (see AppPaths / Program). Output:
#   dist\Cube Radar.exe                          the exe on its own, ready to share
#   dist\cuberadar-<version>-win-x64.zip         the exe + LIESMICH.txt + LICENSE + source.zip (GPL-3.0)
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
$publish = Join-Path ([System.IO.Path]::GetTempPath()) "cuberadar-publish-$version"

foreach ($p in $stage, $publish) { if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
New-Item -ItemType Directory -Force $stage | Out-Null

# WinExe: no console window; everything the radar prints goes to radar.log, problems show as message boxes.
dotnet publish $cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:CubeRadarWindowsApp=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $stage "Cube Radar.exe"
Copy-Item (Join-Path $publish "cuberadar.exe") $exe
# A running radar locks dist\Cube Radar.exe, but Windows lets it be renamed: it is moved aside (and removed on a
# later run, once it has quit), so the next double-click starts the new version.
$shared = Join-Path $dist "Cube Radar.exe"
Get-ChildItem $dist -Filter "Cube Radar.*.old" | Remove-Item -Force -ErrorAction SilentlyContinue
if (Test-Path $shared)
{
    try { Remove-Item $shared -Force -ErrorAction Stop }
    catch { Rename-Item $shared ("Cube Radar.{0:yyyyMMdd_HHmmss}.old" -f (Get-Date)); "dist\Cube Radar.exe was running: old file moved aside" }
}
Copy-Item $exe $shared
Copy-Item (Join-Path $root "LICENSE") $stage
Copy-Item (Join-Path $root "docs\LIESMICH_TESTER.txt") (Join-Path $stage "LIESMICH.txt")
Remove-Item $publish -Recurse -Force

# Corresponding source (GPL-3.0): the committed code, without personal data.
git -C $root archive --format=zip -o (Join-Path $stage "source.zip") HEAD -- `
    src tools config assets LICENSE README.md CubeRadar.slnx cuberadar.cmd overlay.cmd .gitignore
if ($LASTEXITCODE -ne 0) { throw "git archive failed" }

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
"{0}  ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
