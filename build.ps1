# Build a standalone native caffeinate.exe, and optionally an installer or a local copy.
#   .\build.ps1                       # build for this machine's architecture -> publish\<rid>\caffeinate.exe
#   .\build.ps1 -Rid win-x64          # cross-build for another architecture
#   .\build.ps1 -Installer            # also build the Inno Setup installer -> publish\installers\
#   .\build.ps1 -Install              # also copy caffeinate.exe to $HOME\.local\bin
param(
    [string]$Rid = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }),
    [switch]$Installer,
    [switch]$Install,
    [string]$InstallDir = (Join-Path $HOME '.local\bin')
)
# Native tools (git, gh, dotnet) write progress to stderr, which Windows PowerShell 5.1 turns into terminating
# errors under 'Stop' when output is redirected. So: 'Continue', and every native call checks $LASTEXITCODE.
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

# The Native AOT linker locates MSVC via vswhere.exe, which the VS installer doesn't put on PATH.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $vsInstaller 'vswhere.exe')) { $env:PATH = "$vsInstaller;$env:PATH" }

dotnet publish -c Release -r $Rid -o "publish\$Rid" --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
              "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
    $version = ([xml](Get-Content caffeinate.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $arch = $Rid -replace '^win-', ''
    & $iscc /Qp "/DArch=$arch" "/DAppVersion=$version" installer\caffeinate.iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
    Write-Host "Installer: publish\installers\caffeinate-$version-windows-$arch-setup.exe"
}

if ($Install) {
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item "publish\$Rid\caffeinate.exe" (Join-Path $InstallDir 'caffeinate.exe') -Force
    Write-Host "Installed to $InstallDir\caffeinate.exe"
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User') -split ';'
    if ($userPath -notcontains $InstallDir) {
        Write-Warning "$InstallDir is not on your user PATH. Add it to run 'caffeinate' from anywhere."
    }
}
