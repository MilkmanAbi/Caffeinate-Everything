# End-to-end tests for caffeinate.exe: real console signals (Ctrl+C, Ctrl+Break, closing the window)
# and the environments it gets launched from (no console, pipes, cmd, Git Bash, WSL).
#
#   .\tests\run-tests.ps1                         # test .\publish\caffeinate.exe
#   .\tests\run-tests.ps1 -Exe path\to\caffeinate.exe
#
# Run it from an elevated shell to also check, via `powercfg /requests`, that the power requests
# really are registered and are released when the process dies.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\caffeinate.exe'),
    [switch]$SkipGitBash,
    [switch]$SkipWsl
)
$ErrorActionPreference = 'Continue'
$Exe = (Resolve-Path $Exe).Path
if ($Exe -match '\s') { throw "Put caffeinate.exe on a path without spaces for these tests: $Exe" }

$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$h = Join-Path $bin 'harness.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo "/out:$h" (Join-Path $PSScriptRoot 'harness.cs')
if ($LASTEXITCODE -ne 0) { throw 'failed to compile tests\harness.cs' }

$script:pass = 0; $script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "PASS  $name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "FAIL  $name   -> $detail" -ForegroundColor Red }
}
function Harness { $o = & $h @args; ($o | Where-Object { $_ -notlike 'pid=*' }) -join ' | ' }
function Code([string]$r) { if ($r -match 'EXITED code=(-?\d+)') { [int]$Matches[1] } else { $null } }
function Running([string]$pattern) {
    @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -match $pattern }).Count
}
function Ours { @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Exe }).Count }
function Cleanup {
    Get-CimInstance Win32_Process |
        Where-Object { $_.ExecutablePath -eq $Exe -or ($_.Name -eq 'harness.exe' -and $_.CommandLine -match ' ignore ') } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
Cleanup

Write-Host "`n== Testing $Exe ==" -ForegroundColor Cyan

Write-Host "`n-- Terminal signals --"
$r = Harness console ctrlc 1500 5000 $Exe -dismu
Check 'Ctrl+C stops `caffeinate -dismu` (exit 130)' ((Code $r) -eq 130) $r
$r = Harness group break 1500 5000 $Exe -dismu
Check 'Ctrl+Break stops caffeinate' ($null -ne (Code $r)) $r
$r = Harness console close 1500 8000 $Exe -dismu
Check 'Closing the terminal window stops caffeinate' ($null -ne (Code $r)) $r
$r = Harness console close 1500 8000 $Exe
Check 'Double-clicked (no args), then window closed' ($null -ne (Code $r)) $r
Check 'No caffeinate left running after signals' ((Ours) -eq 0)
# cmd's `start /b` marks a background job by disabling Ctrl+C for it; like `caffeinate &` on macOS,
# a background caffeinate must survive a Ctrl+C aimed at the foreground.
$r = Harness console ctrlc 1500 2500 cmd /d /c start /b /wait $Exe -dismu
Check 'Background job (start /b): Ctrl+C is ignored, like `caffeinate &`' ((Ours) -eq 1) "$r ours=$(Ours)"
Cleanup

Write-Host "`n-- Signals while running a utility --"
$r = Harness console ctrlc 1500 8000 $Exe -i ping -n 41 127.0.0.1
Check 'Ctrl+C reaches the utility; caffeinate exits with it' (($null -ne (Code $r)) -and (Running 'ping.*-n 41') -eq 0) $r
$r = Harness console close 1500 8000 $Exe -i ping -n 42 127.0.0.1
Check 'Closing the window ends caffeinate and the utility' (($null -ne (Code $r)) -and (Running 'ping.*-n 42') -eq 0) $r
$r = Harness group break 1500 5000 $Exe -i ping -n 43 127.0.0.1
Check 'Ctrl+Break ends caffeinate with a utility running' (($null -ne (Code $r)) -and (Ours) -eq 0) $r
$r = Harness console ctrlc 1500 2500 $Exe -i $h ignore 20000
Check 'One Ctrl+C: keeps holding while a stubborn utility runs' ($r -like 'STILL RUNNING*') $r
Cleanup
$r = Harness console ctrlc2 1500 5000 $Exe -i $h ignore 20000
Check 'Second Ctrl+C: gives up on a stubborn utility (exit 130)' ((Code $r) -eq 130) $r
Check '...and leaves the utility itself running' ((Running 'harness\.exe.* ignore ') -ge 1)
Cleanup

Write-Host "`n-- No console at all (GUI launchers, scheduled tasks) --"
$r = Harness detached none 0 6000 $Exe -t 2
Check 'Detached: -t 2 exits 0 after ~2s' (((Code $r) -eq 0) -and $r -match 'ms=(19|2\d)\d\d') $r
$r = Harness detached none 0 2500 $Exe -dismu
Check 'Detached: -dismu keeps running' ($r -like 'STILL RUNNING*') $r
$r = Harness detached none 0 6000 $Exe -u -t 1
Check 'Detached: -u does not crash without a desktop console' ((Code $r) -eq 0) $r
Cleanup

Write-Host "`n-- Pipes and redirection --"
$o = 'hello' | & $Exe -i findstr hello
Check 'stdin is piped through to the utility' (($o -eq 'hello') -and $LASTEXITCODE -eq 0) "out=$o code=$LASTEXITCODE"
$o = & $Exe -i cmd /c echo captured
Check 'utility stdout can be captured' ($o -eq 'captured') "out=$o"
$null = & $Exe -i cmd /c exit 7 | Out-String
Check 'exit code survives a pipeline' ($LASTEXITCODE -eq 7) "code=$LASTEXITCODE"
$o = cmd /d /c "$Exe -t 1 <NUL >NUL && echo ok"
Check 'cmd.exe: runs with stdin/stdout redirected to NUL' ($o -eq 'ok') "out=$o"
$o = cmd /d /c "$Exe -x 2>&1"
Check 'usage errors go to stderr' (($o -join ' ') -match 'illegal option -- x') "out=$o"

Write-Host "`n-- Command resolution --"
$tmp = Join-Path $env:TEMP ("caff-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $tmp | Out-Null
Set-Content -Encoding ascii (Join-Path $tmp 'cafftool') '#!/bin/sh'   # extensionless POSIX shim, like npm's
Set-Content -Encoding ascii (Join-Path $tmp 'cafftool.cmd') '@echo cmd=%1& exit /b 3'
$oldPath = $env:PATH; $env:PATH = "$tmp;$env:PATH"
$o = & $Exe -i cafftool ran
$env:PATH = $oldPath
Check '.cmd found via PATH/PATHEXT (not the extensionless shim)' (($o -eq 'cmd=ran') -and $LASTEXITCODE -eq 3) "out=$o code=$LASTEXITCODE"
Remove-Item -Recurse -Force $tmp
& $Exe -i no-such-command-xyz 2>$null
Check 'missing command exits 127' ($LASTEXITCODE -eq 127) "code=$LASTEXITCODE"

$bash = Join-Path $env:ProgramFiles 'Git\bin\bash.exe'
if (-not $SkipGitBash -and (Test-Path $bash)) {
    Write-Host "`n-- Git Bash --"
    $exeFwd = $Exe -replace '\\', '/'
    & $bash -c "$exeFwd -t 1"
    Check 'Git Bash: -t 1 exits 0' ($LASTEXITCODE -eq 0) "code=$LASTEXITCODE"
    & $bash -c "$exeFwd -i sh -c 'exit 5'"
    Check 'Git Bash: utility exit code passes through' ($LASTEXITCODE -eq 5) "code=$LASTEXITCODE"
    $script = Join-Path $bin 'bash-dismu.sh'
    [IO.File]::WriteAllText($script, "$exeFwd -dismu`n")
    $r = Harness console ctrlc 2500 8000 $bash ($script -replace '\\', '/')
    Check 'Git Bash: Ctrl+C stops caffeinate' (($null -ne (Code $r)) -and (Ours) -eq 0) "$r ours=$(Ours)"
    Cleanup
}

$distros = @(wsl.exe --list --quiet 2>$null | ForEach-Object { ($_ -replace "`0", '').Trim() } | Where-Object { $_ })
if (-not $SkipWsl -and $distros.Count -gt 0) {
    Write-Host "`n-- WSL interop --"
    $lin = '/mnt/' + $Exe.Substring(0, 1).ToLower() + ($Exe.Substring(2) -replace '\\', '/')
    wsl.exe -e $lin -t 1
    Check 'WSL: -t 1 exits 0' ($LASTEXITCODE -eq 0) "code=$LASTEXITCODE"
    wsl.exe -e $lin -i cmd.exe /c exit 6
    Check 'WSL: utility exit code passes through' ($LASTEXITCODE -eq 6) "code=$LASTEXITCODE"
    $r = Harness console ctrlc 4000 8000 wsl.exe -e $lin -dismu
    Start-Sleep -Milliseconds 500
    Check 'WSL: Ctrl+C stops caffeinate' (($null -ne (Code $r)) -and (Ours) -eq 0) "$r ours=$(Ours)"
    Cleanup
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($admin) {
    Write-Host "`n-- Power requests (powercfg /requests) --"
    function Sections {
        $cur = ''; $hits = @{}
        foreach ($line in (powercfg /requests)) {
            if ($line -match '^([A-Z]+):$') { $cur = $Matches[1] }
            elseif ($line -match 'caffeinate\.exe') { $hits[$cur] = $true }
        }
        $hits
    }
    $p = Start-Process -PassThru -WindowStyle Hidden $Exe -ArgumentList '-dismu'
    Start-Sleep 1
    $s = Sections
    Check 'powercfg lists caffeinate under DISPLAY' ([bool]$s['DISPLAY'])
    Check 'powercfg lists caffeinate under SYSTEM' ([bool]$s['SYSTEM'])
    Stop-Process -Id $p.Id -Force
    Start-Sleep 1
    $s = Sections
    Check 'requests are released when caffeinate is killed' ($s.Count -eq 0) ($s.Keys -join ',')
} else {
    Write-Host "`n(skipping powercfg checks: run elevated to include them)" -ForegroundColor DarkGray
}

Cleanup
Write-Host "`n$script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
exit $script:fail
