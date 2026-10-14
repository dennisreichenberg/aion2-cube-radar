# Writes the release notes for a tag from .github\release-notes.md: version, SHA-256 of the exe and what changed (the
# message of an annotated tag, else the commit subjects since the previous tag). Used by .github\workflows\release.yml.
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Exe,
    [string]$Out = "dist\release-notes.md",
    [string]$Repo = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { "dennisreichenberg/aion2-cube-radar" })
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8 # git prints UTF-8 (umlauts in tag messages)
$root = Split-Path $PSScriptRoot -Parent

git -C $root rev-parse --verify --quiet "$Tag^{commit}" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "unknown tag $Tag (git fetch --tags?)" }
if ((git -C $root cat-file -t $Tag) -eq "tag")
{
    $changes = (git -C $root tag -l --format="%(contents)" $Tag) -join "`n"
}
else
{
    $ErrorActionPreference = "Continue" # Windows PowerShell turns git's "no names found" on stderr into an error
    $previous = git -C $root describe --tags --abbrev=0 "$Tag^" 2>$null
    $ErrorActionPreference = "Stop"
    $range = if ($LASTEXITCODE -eq 0 -and $previous) { "$previous..$Tag" } else { $Tag }
    $changes = (git -C $root log --no-merges --format="- %s" $range) -join "`n"
}

$notes = (Get-Content (Join-Path $root ".github\release-notes.md") -Raw -Encoding UTF8).
    Replace("{{VERSION}}", $Tag.TrimStart("v")).
    Replace("{{TAG}}", $Tag).
    Replace("{{REPO}}", $Repo).
    Replace("{{SHA256}}", (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()).
    Replace("{{CHANGES}}", $changes.Trim())
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out) # .NET does not know the PS location
[System.IO.File]::WriteAllText($Out, $notes, [System.Text.UTF8Encoding]::new($false))
"release notes: $Out"
