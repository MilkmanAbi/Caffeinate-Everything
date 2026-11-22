# Build a standalone native caffeinate.exe and (optionally) install it to ~/.local/bin.
#   .\build.ps1              # build for this machine's architecture
#   .\build.ps1 -Install     # build + copy to $HOME\.local\bin
#   .\build.ps1 -Rid win-x64 # cross-build for another architecture
param(
    [string]$Rid = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }),
    [switch]$Install,
    [string]$InstallDir = (Join-Path $HOME '.local\bin')
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# The Native AOT linker locates MSVC via vswhere.exe, which the VS installer doesn't put on PATH.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $installer 'vswhere.exe')) { $env:PATH = "$installer;$env:PATH" }

dotnet publish -c Release -r $Rid -o publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if ($Install) {
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item publish\caffeinate.exe (Join-Path $InstallDir 'caffeinate.exe') -Force
    Write-Host "Installed to $InstallDir\caffeinate.exe"
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User') -split ';'
    if ($userPath -notcontains $InstallDir) {
        Write-Warning "$InstallDir is not on your user PATH. Add it to run 'caffeinate' from anywhere."
    }
}
