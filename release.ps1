# Build both architectures with installers, run the end-to-end tests, and publish a GitHub release.
#
#   1. bump <Version> in caffeinate.csproj and add a "## <version>" section to CHANGELOG.md
#   2. commit
#   3. .\release.ps1            (or -Draft to review on GitHub first)
#
# Release assets: both installers, both standalone exes, and SHA256SUMS.txt (which `caffeinate --update`
# requires: it refuses to install anything it can't verify).
param(
    [switch]$Draft,
    [switch]$SkipTests,
    [string]$Repo = 'MilkmanAbi/Caffeinate-Everything'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content caffeinate.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
Write-Host "Releasing caffeinate $version ($tag)" -ForegroundColor Cyan

if (git status --porcelain) { throw 'The working tree has uncommitted changes; commit them first.' }
if (git tag --list $tag) { throw "Tag $tag already exists; bump <Version> in caffeinate.csproj." }
gh release view $tag --repo $Repo *> $null
if ($LASTEXITCODE -eq 0) { throw "GitHub release $tag already exists." }

$changelog = [IO.File]::ReadAllText((Resolve-Path CHANGELOG.md)) -replace "`r`n", "`n"
$section = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($version))[^\n]*\n(.*?)(?=^## |\z)")
if (-not $section.Success) { throw "CHANGELOG.md has no '## $version' section." }

foreach ($rid in 'win-x64', 'win-arm64') { & .\build.ps1 -Rid $rid -Installer }

if (-not $SkipTests) {
    # Native architecture first; on ARM64 the x64 build also runs (emulated), so test both there.
    $rids = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64', 'win-x64' } else { , 'win-x64' }
    foreach ($rid in $rids) {
        & .\tests\run-tests.ps1 -Exe "publish\$rid\caffeinate.exe"
        if ($LASTEXITCODE -ne 0) { throw "tests failed for $rid; not releasing" }
    }
}

$out = Join-Path $PSScriptRoot 'publish\release'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null
foreach ($arch in 'x64', 'arm64') {
    Copy-Item "publish\installers\caffeinate-$version-windows-$arch-setup.exe" $out
    Copy-Item "publish\win-$arch\caffeinate.exe" (Join-Path $out "caffeinate-windows-$arch.exe")
}
$assets = Get-ChildItem $out -Filter *.exe | Sort-Object Name
$assets | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
    Set-Content -Encoding ascii (Join-Path $out 'SHA256SUMS.txt')

$notes = Join-Path $out 'notes.md'
@"
$($section.Groups[1].Value.Trim())

## Download

| File | For |
|------|-----|
| ``caffeinate-$version-windows-x64-setup.exe`` | Installer: most Windows PCs (Intel / AMD) |
| ``caffeinate-$version-windows-arm64-setup.exe`` | Installer: Windows on ARM (Snapdragon, Surface Pro X...) |
| ``caffeinate-windows-x64.exe`` / ``caffeinate-windows-arm64.exe`` | Standalone exe, no install |

Already have 1.2.0 or later? Run ``caffeinate --update``. The installer upgrades in place and keeps your
settings and schedules. The binaries aren't code-signed, so Windows may ask before running them the
first time. Checksums are in ``SHA256SUMS.txt``.
"@ | Set-Content -Encoding utf8 $notes

git push origin HEAD
$ghArgs = @('release', 'create', $tag, '--repo', $Repo, '--target', (git rev-parse HEAD), '--title', "caffeinate $version",
          '--notes-file', $notes) + $assets.FullName + (Join-Path $out 'SHA256SUMS.txt')
if ($Draft) { $ghArgs += '--draft' }
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
git fetch --tags --quiet
Write-Host "Released $tag" -ForegroundColor Green
