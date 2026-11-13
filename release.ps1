# Publish a new TaskBoard version. Every installed copy shows "Update to vX" within the hour (or on next launch).
#   .\release.ps1            -> bumps the patch number (1.1.0 -> 1.1.1)
#   .\release.ps1 1.2.0      -> uses that version
# Needs: dotnet SDK, gh CLI signed in to Cheesysgames, a clean commit of your changes.
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$proj = Join-Path $PSScriptRoot 'TaskBoard.csproj'
$xml = Get-Content $proj -Raw
$current = [regex]::Match($xml, '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $Version) {
    $v = [version]$current
    $Version = "$($v.Major).$($v.Minor).$($v.Build + 1)"
}
$Version = $Version.TrimStart('v')
Write-Host "TaskBoard $current -> $Version"

(Get-Content $proj -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" |
    Set-Content $proj -NoNewline

dotnet publish -c Release -o dist
if ($LASTEXITCODE) { throw 'build failed' }

& .\dist\TaskBoard.exe --selftest | Out-Null
Start-Sleep -Milliseconds 500
$result = Get-Content .\dist\selftest.log -Raw
if (-not $result.StartsWith('PASS')) { throw "selftest: $result" }

git add TaskBoard.csproj
git diff --cached --quiet
if ($LASTEXITCODE) { git commit -m "Release v$Version" | Out-Null }
git tag "v$Version"
git push origin HEAD "v$Version"
if ($LASTEXITCODE) { throw 'push failed' }

gh release create "v$Version" .\dist\TaskBoard.exe --repo Cheesysgames/TaskBoard --title "v$Version" --generate-notes
if ($LASTEXITCODE) { throw 'release failed' }
Write-Host "Released v$Version"
