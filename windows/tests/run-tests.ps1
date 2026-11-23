# End-to-end tests for caffeinate.exe: real console signals (Ctrl+C, Ctrl+Break, closing the window)
# and the environments it gets launched from (no console, pipes, cmd, Git Bash, WSL).
#
#   .\tests\run-tests.ps1                         # test .\publish\caffeinate.exe
#   .\tests\run-tests.ps1 -Exe path\to\caffeinate.exe
#
# Run it from an elevated shell to also check, via `powercfg /requests`, that the power requests
# really are registered and are released when the process dies.
param(
    [string]$Exe = (Join-Path $PSScriptRoot ('..\publish\' + $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }) + '\caffeinate.exe')),
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

# ==================================================================== extended features
# Non-disruptive: nothing here sleeps the PC or turns the screen off. Power-plan settings are changed
# only for seconds and every test checks they are put back.

$GUIDS = @{
    lid   = @('4f971e89-eebd-4455-a8de-9e59040e7347', '5ca83367-6e45-459f-a27b-476b1d01c936')
    sleep = @('238c9fa8-0aad-41ed-83f4-97be242c8f20', '29f6c1db-86da-48c5-9fdb-f2b67b1f44da')
    wake  = @('238c9fa8-0aad-41ed-83f4-97be242c8f20', 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d')
    video = @('7516b95f-f776-4464-8c53-06167f40cc99', '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e')
}
function Setting([string]$k) {
    $g = $GUIDS[$k]
    (powercfg /qh SCHEME_CURRENT $g[0] $g[1] | Select-String 'Current (AC|DC) Power Setting Index: (0x[0-9a-f]+)' |
        ForEach-Object { [Convert]::ToInt32($_.Matches[0].Groups[2].Value, 16) }) -join '/'
}
function TaskExists([string]$n) { schtasks /query /tn "\Caffeinate\$n" 2>$null | Out-Null; $LASTEXITCODE -eq 0 }
$log = Join-Path $env:LOCALAPPDATA 'Caffeinate\caffeinate.log'
$modern = ((& $Exe --status) -join ' ') -match 'Modern Standby'
& $Exe --restore | Out-Null   # start from a clean journal
$orig = @{ lid = Setting lid; sleep = Setting sleep; wake = Setting wake; video = Setting video }
function SettingsBack { ($orig.Keys | Where-Object { (Setting $_) -ne $orig[$_] }).Count -eq 0 }

Write-Host "`n-- Extended: help and validation --"
$hl = @(& $Exe --help)
Check '--help: macOS usage line first' ($hl[0] -like 'usage: caffeinate `[-dimsu`]*') $hl[0]
Check '--help: extended options below a gap' ((@($hl[1..10] | Where-Object { $_.Trim() }).Count -eq 0) -and (($hl -join "`n") -match 'Extended options'))
$cases = @(
    @{ a = @('--screen-off', '-d'); m = 'contradicts' },
    @{ a = @('--for', '2h', '-t', '5'); m = 'only one of' },
    @{ a = @('--sleep', '25:00-07:00'); m = 'invalid time' },
    @{ a = @('--wake', '09:00', '--repeat', 'funday'); m = 'invalid day' },
    @{ a = @('--repeat', 'daily'); m = 'needs --sleep' },
    @{ a = @('--sleep', 'now-07:00', '--repeat', 'daily'); m = 'clock times' },
    @{ a = @('--battery-floor', '150'); m = 'battery floor' },
    @{ a = @('--sleep', '23:00-07:00', '-d'); m = 'only apply to --wake' },
    @{ a = @('--bogus'); m = 'unrecognized option' },
    @{ a = @('--wake', '09:00-17:30', '--repeat', 'daily', '--hold-updates'); m = "can't be part of a saved schedule" }
)
foreach ($case in $cases) {
    $out = (& $Exe @($case.a) 2>&1) -join ' '
    Check "rejects: caffeinate $($case.a -join ' ')" (($LASTEXITCODE -eq 1) -and $out -match [regex]::Escape($case.m)) $out
}

Write-Host "`n-- Extended: schedule maths (pinned clock and time zone) --"
function DryRun([string]$now, [string[]]$a) {
    $env:CAFFEINATE_NOW = $now; $env:CAFFEINATE_TZ = 'Pacific Standard Time'
    try { (& $Exe @a --dry-run 2>&1) -join "`n" } finally { Remove-Item Env:CAFFEINATE_NOW, Env:CAFFEINATE_TZ -ErrorAction SilentlyContinue }
}
$o = DryRun '2026-11-23T12:00:00' @('--wake', '09:00-17:30')
Check 'a wake window already in progress holds now until its end' ($o -match 'Hold now until today 17:30') $o
$o = DryRun '2026-11-23T12:00:00' @('--sleep', '23:00-07:00')
Check 'sleep window: sleeps tonight, wakes tomorrow' (($o -match '(sleep|hibernate)\s+today 23:00') -and ($o -match 'wake\s+tomorrow 07:00')) $o
$o = DryRun '2026-11-24T01:00:00' @('--sleep', '23:00-07:00')
Check 'sleep window in progress: sleeps now, wakes at its end' (($o -match 'now: (sleep|hibernate)') -and ($o -match 'wake\s+today 07:00')) $o
$o = DryRun '2026-11-23T12:00:00' @('--sleep', '23:00-07:00', '--repeat', 'fri')
Check 'repeating window across midnight: Friday night -> Saturday morning' ($o -match 'wake\s+Sat 2026-11-28 07:00') $o
$o = DryRun '2026-11-27T12:00:00' @('--at', '23:30 sleep', '--at', '06:45 wake', '--repeat', 'weekdays')
Check '--at weekdays from a Friday: next wake is Monday' (($o -match 'sleep\s+today 23:30') -and ($o -match 'wake\s+Mon 2026-11-30 06:45')) $o
$o = DryRun '2027-03-13T12:00:00' @('--at', '02:30 wake')
Check 'DST: 02:30 on the spring-forward night becomes 03:00' ($o -match 'wake\s+tomorrow 03:00') $o
$o = DryRun '2026-11-23T12:00:00' @('--sleep', '18:00+90m')
Check 'START+DURATION window' (($o -match 'today 18:00') -and ($o -match 'wake\s+today 19:30')) $o
if ($modern) {
    $o = DryRun '2026-11-23T12:00:00' @('--sleep', '23:00-07:00')
    Check 'Modern Standby: a sleep followed by a wake hibernates instead' ($o -match 'hibernate\s+today 23:00') $o
}

Write-Host "`n-- Extended: changed settings are always put back --"
$p = Start-Process -PassThru -WindowStyle Hidden $Exe -ArgumentList '--ignore-lid', '-t', '5'
Start-Sleep 2
Check '--ignore-lid: lid close is "do nothing" while holding' ((Setting lid) -eq '0/0') (Setting lid)
if ($modern) { Check 'Modern Standby: battery sleep timeout is Never while holding' ((Setting sleep) -like '*/0') (Setting sleep) }

Check '--status lists the session and the temporary changes' (((& $Exe --status) -join ' ') -match "pid $($p.Id).*Temporarily changed")
$p.WaitForExit()
Check '...restored after a normal exit' (SettingsBack) "lid=$(Setting lid) sleep=$(Setting sleep)"
$p = Start-Process -PassThru -WindowStyle Hidden $Exe -ArgumentList '-dismu', '-t', '3'
Start-Sleep 1.5
Check 'plain -dismu changes no power-plan setting, like on macOS' ((SettingsBack) -and -not (TaskExists 'repair')) "sleep=$(Setting sleep)"
$p.WaitForExit()
$r = Harness console ctrlc 2500 6000 $Exe --ignore-lid
Check '...restored after Ctrl+C' (((Code $r) -eq 130) -and (SettingsBack)) "$r lid=$(Setting lid)"
$r = Harness console close 2500 8000 $Exe --ignore-lid
Start-Sleep -Milliseconds 500
Check '...restored after closing the terminal window' (($null -ne (Code $r)) -and (SettingsBack)) "$r lid=$(Setting lid)"
function TimeToRestore([int]$maxMs = 5000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $maxMs) { if (SettingsBack) { return $sw.ElapsedMilliseconds }; Start-Sleep -Milliseconds 100 }
    return -1
}
$p = Start-Process -PassThru -WindowStyle Hidden $Exe -ArgumentList '--ignore-lid'
Start-Sleep 3
Check 'a watcher process is guarding the change' ([bool](Get-CimInstance Win32_Process -Filter "Name='caffeinate-agent.exe'" | Where-Object CommandLine -match '--watch'))
Stop-Process -Id $p.Id -Force
$ms = TimeToRestore
Check "...restored right after a hard kill (Stop-Process -Force), in ${ms} ms" ($ms -ge 0 -and $ms -lt 3000) "lid=$(Setting lid) sleep=$(Setting sleep)"
$p = Start-Process -PassThru -WindowStyle Hidden $Exe -ArgumentList '--ignore-lid'
Start-Sleep 3
taskkill /T /F /PID $p.Id 2>&1 | Out-Null
$ms = TimeToRestore
Check "...restored right after killing the whole process tree (taskkill /T /F), in ${ms} ms" ($ms -ge 0 -and $ms -lt 3000) "lid=$(Setting lid) sleep=$(Setting sleep)"
Start-Sleep 2
Check '...and the watcher and repair task go away once nothing is changed' ((-not (TaskExists 'repair')) -and -not (Get-CimInstance Win32_Process -Filter "Name='caffeinate-agent.exe'" | Where-Object CommandLine -match '--watch'))

Write-Host "`n-- Extended: saved schedules --"
foreach ($n in 'caff-test-a', 'caff-test-b') { & $Exe --remove $n 2>$null | Out-Null }
$o = (& $Exe --wake '2030-01-01 09:00-2030-01-01 10:00' --name caff-test-a -d 2>&1) -join ' '
Check 'a future wake window is saved' (($LASTEXITCODE -eq 0) -and $o -match "Schedule 'caff-test-a'") $o
Check '...as two scheduled tasks (normal + wake-to-run)' ((TaskExists 'caff-test-a') -and (TaskExists 'caff-test-a.wake'))
$agent = Join-Path $env:LOCALAPPDATA 'Caffeinate\bin\caffeinate-agent.exe'
$sub = if (Test-Path $agent) { $b = [IO.File]::ReadAllBytes($agent); $pe = [BitConverter]::ToInt32($b, 0x3C); [BitConverter]::ToUInt16($b, $pe + 24 + 68) } else { -1 }
Check '...run by a windowless (GUI-subsystem) agent' ($sub -eq 2) "subsystem=$sub"
Check '...with "Allow wake timers" turned on' ((Setting wake) -eq '1/1') (Setting wake)
Check '--list shows it' (((& $Exe --list) -join ' ') -match 'caff-test-a')
$o = (& $Exe --remove caff-test-a) -join ' '
Check '--remove deletes both tasks' ((-not (TaskExists 'caff-test-a')) -and (-not (TaskExists 'caff-test-a.wake'))) $o
Check '...and puts "Allow wake timers" back' ((Setting wake) -eq $orig.wake) (Setting wake)

$mark = (Get-Date)
$o = (& $Exe --at '+1m wake' --name caff-test-b 2>&1) -join ' '
Check 'one-off wake in 1 minute is scheduled' ($LASTEXITCODE -eq 0) $o
$deadline = (Get-Date).AddSeconds(150); $fired = $false
do { Start-Sleep 5; $fired = [bool](Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match 'schedule caff-test-b: wake' -and [datetime]::ParseExact($_.Substring(0, 19), 'yyyy-MM-dd HH:mm:ss', $null) -ge $mark.AddSeconds(-2) }) } until ($fired -or (Get-Date) -gt $deadline)
Check 'Task Scheduler fired it and the windowless agent ran the wake' $fired
$deadline = (Get-Date).AddSeconds(180)
do { Start-Sleep 5 } until (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'Caffeinate\schedules\caff-test-b.json')) -or (Get-Date) -gt $deadline)
Check '...and the finished one-off removed itself, tasks and all' ((-not (Test-Path (Join-Path $env:LOCALAPPDATA 'Caffeinate\schedules\caff-test-b.json'))) -and (-not (TaskExists 'caff-test-b.wake')))
Check '...and put "Allow wake timers" back' ((Setting wake) -eq $orig.wake) (Setting wake)

foreach ($n in 'caff-test-a', 'caff-test-b') { & $Exe --remove $n 2>$null | Out-Null }
& $Exe --restore | Out-Null
Check 'all power settings are exactly as they were before the tests' (SettingsBack) (($orig.Keys | ForEach-Object { "$_=$(Setting $_) (was $($orig[$_]))" }) -join ', ')

Cleanup
Write-Host "`n$script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
exit $script:fail
