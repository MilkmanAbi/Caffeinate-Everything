# caffeinate for Windows

The macOS `caffeinate` command, for Windows, plus extras. It has the same flags and syntax as
macOS, is a single native `.exe` that needs no runtime, and works from PowerShell, cmd, Windows
Terminal and Git Bash.
```
usage: caffeinate [-dimsu] [-t timeout] [-w pid] [utility arguments...]
```

```powershell
caffeinate -dismu                 # keep everything awake until Ctrl+C
caffeinate -d -t 3600             # keep the display on for an hour
caffeinate -i make build          # stay awake while a command runs; exits with its exit code
caffeinate -w 1234                # stay awake until process 1234 exits
caffeinate -s -- python train.py  # stay awake on AC power while training
```

## Install

Download the installer for your PC from the
[latest release](https://github.com/MilkmanAbi/Caffeinate-Everything/releases/latest):

| File | For |
|------|-----|
| `caffeinate-<version>-windows-x64-setup.exe` | Most Windows PCs (Intel / AMD) |
| `caffeinate-<version>-windows-arm64-setup.exe` | Windows on ARM (Snapdragon, Surface Pro X...) |

- By default it installs just for you, with no admin rights: `%LOCALAPPDATA%\Programs\caffeinate`,
  added to your PATH. Choose "all users" on the first page to install to Program Files instead.
- Open a **new** terminal afterwards so it picks up the PATH change.
- The uninstaller removes your saved schedules and their tasks, restores anything caffeinate
  changed, and deletes `%LOCALAPPDATA%\Caffeinate`.
- Silent install: `setup.exe /VERYSILENT /CURRENTUSER`, or `/ALLUSERS` for all users.

There are also standalone `caffeinate-windows-<arch>.exe` files if you'd rather put the exe on your
PATH yourself. The binaries are not code-signed, so Windows may ask before running them the first
time.

## Updating

```powershell
caffeinate --check-update     # is a newer version out?
caffeinate --update           # download it and install it
```

- `--update` picks the build for your PC (arm64 or x64) and checks the download against the
  release's `SHA256SUMS.txt` before running anything.
- An installed copy updates through the installer, which upgrades in place and keeps your settings
  and schedules. A standalone exe replaces itself.
- Once a day caffeinate looks for a new release in the background and prints a one-line notice if
  there is one. It never blocks or slows anything down. Turn it off with
  `caffeinate --update-check off`, the installer checkbox, or `CAFFEINATE_NO_UPDATE_CHECK=1`. The
  check is a single request to the GitHub releases API that sends nothing but caffeinate's version.
## Flags

| Flag | macOS | Windows implementation |
|------|-------|------------------------|
| `-d` | Prevent display sleep | `PowerRequestDisplayRequired` |
| `-i` | Prevent idle system sleep | `PowerRequestSystemRequired` |
| `-m` | Prevent disk idle sleep | Same as `-i`. Windows has no per-disk idle assertion. |
| `-s` | Prevent system sleep (AC power only) | `PowerRequestSystemRequired`, held only while on AC. Power source is re-checked every 5 s. |
| `-u` | Declare user active; wake the display | Zero-distance mouse input wakes the display and resets idle timers. A display request is then held for `-t` seconds, or 5 s by default. |
| `-t n` | Timeout in seconds | Same. Ignored when a utility is given. |
| `-w pid` | Wait for a process to exit | Same. If `-t` is also given, caffeinate exits at whichever comes first. |

Options are parsed with BSD `getopt` rules, as on macOS:
- Flags combine: `-dismu`, `-dt 60`, `-t60`.
- Option parsing stops at the first non-option, so `caffeinate make -j4` passes `-j4` to make.
- `--` ends options.

With no assertion flag, `-i` is assumed. With no utility, `-t` or `-w`, caffeinate runs until Ctrl+C.

Utilities are looked up through `PATH` + `PATHEXT` the way a shell does. `.cmd`/`.bat` files such as
`npm` or `code` are run through `cmd.exe`. The utility's exit code is passed through, and a missing
command returns 127.

### Stopping it

| What you do | What happens |
|-------------|--------------|
| Ctrl+C (no utility) | Exits immediately with code 130. |
| Ctrl+C (utility running) | The utility gets the Ctrl+C too. caffeinate keeps holding while it shuts down, then exits with its code. |
| Ctrl+C a second time | caffeinate stops right away (130) even if the utility ignores Ctrl+C, such as a GUI app or a `.bat` waiting at "Terminate batch job?". The utility is left running. |
| Ctrl+Break | Exits immediately. |
| Close the terminal window or tab | Exits immediately, along with any console utility it started. |
| Process killed (Task Manager, `Stop-Process`) | Windows drops the power requests with the process. |

If you start it as a background job with cmd's `start /b caffeinate -d`, Ctrl+C is ignored, just as
`caffeinate &` on macOS isn't killed by Ctrl+C. Stop it with Ctrl+Break, by closing the window, or
with `taskkill /im caffeinate.exe`.

`caffeinate --help` prints the macOS usage line; the Windows extensions are listed further down.

## Extended features (Windows only)

All of these are long options, so they never clash with the macOS flags. Everything runs as you.
The only exception is Windows Update pausing, which shows one UAC prompt.

```powershell
caffeinate --for 2h30m                       # hold for a duration
caffeinate -d --until 18:30                  # hold until a time
caffeinate --screen-off                      # screen off now, PC keeps running (Wi-Fi, downloads...)
caffeinate --ignore-lid -i python train.py   # keep training with the lid closed
caffeinate --battery-floor 20 -i make        # stop holding if the battery drops to 20%
caffeinate --hold-updates -i ./long-job.cmd  # no Windows Update restart in the middle (UAC once)

caffeinate --sleep 23:00-07:00 --repeat weekdays   # asleep at night, woken in the morning
caffeinate -d --wake 09:00-17:30 --repeat weekdays # awake (display on) in work hours, then sleep
caffeinate --wake 3h                               # stay awake 3 hours here, then sleep
caffeinate --sleep 2h                              # sleep now, wake in 2 hours
caffeinate --at "23:30 sleep" --at "06:45 wake" --repeat daily
caffeinate --list ; caffeinate --remove all ; caffeinate --status
caffeinate --pause-updates 7d ; caffeinate --resume-updates
```

Time formats: `90s 45m 2h 1h30m 1d`, `18:30 7am 7:30pm noon midnight`, `tomorrow 07:00`,
`2026-12-01 09:00`, `+2h`, `now`. A window is `START-END`, `"START to END"`, `START+DURATION`, or
just `DURATION`, which starts now. An end earlier than its start crosses midnight. `--dry-run`
prints the plan without doing anything.

### How it works, and why

| Feature | Implementation | Why this way |
|---------|----------------|--------------|
| `--screen-off` | Sets the screen timeout to 3 s, waits for the screen to go dark, then restores the timeout. The screen stays off until there's input. | Microsoft: after the *screen* timeout "the system continues to run and all applications continue to operate normally". The usual monitor-off message (`SC_MONITORPOWER`) instead sends a Modern Standby PC to sleep, cutting Wi-Fi (verified on a Surface Laptop). |
| Holding on battery (Modern Standby) | While caffeinate keeps the system awake, the *battery* sleep timeout is set to "never", then put back. | `PowerSetRequest` docs: on Modern Standby on DC power, requests "are terminated 5 minutes after the system sleep timeout has expired". |
| `--ignore-lid` | Lid close action is set to "do nothing" while holding, then put back. | Closing the lid is a user-initiated sleep, which ends all power requests. |
| `--sleep`, `--at ... sleep` | Modern Standby: the monitor-off message (enters standby). Older PCs: `SetSuspendState`. | `SetSuspendState(sleep)` isn't supported on Modern Standby PCs. |
| `--sleep` followed by a wake | On Modern Standby PCs this **hibernates** instead. | A timer "wakes SoC but cannot turn on display" from Modern Standby (Microsoft). Resuming from hibernate on a timer is a full wake. |
| Saved schedules | Per-user Task Scheduler tasks in `\Caffeinate\` (no admin), run by a windowless copy of caffeinate. They catch up after missed runs and re-enter a wake window after restart, sign-in or unlock. | Survives closing the terminal, restarts and sleep. |
| Wake events | Holds the display and system on for 2 minutes after a timer wake. Turns on "Allow wake timers" while a wake schedule exists, and puts it back afterwards. | Windows sends a PC back to sleep ~2 minutes after an unattended timer wake. |
| Scheduled sleeps | Wait until there has been 2 minutes with no input (up to 15 minutes), and skip if another caffeinate is holding or the event is more than 10 minutes late. | A schedule shouldn't put the PC to sleep while you're using it. |
| Windows Update | The same registry values the Settings app writes, set by a short-lived elevated helper. Verified on Windows 11 build 28000: Settings then shows "Updates paused until...". `--hold-updates` restores the previous state the moment caffeinate exits, never shortens a pause you set, and expires on its own if everything is killed. | One UAC prompt; everything else stays unprivileged. |

### Nothing gets left behind

Every power-plan change is recorded in `%LOCALAPPDATA%\Caffeinate\journal.json` with the original
value and the process that made it. It goes back the moment caffeinate ends, however it ends:

| How caffeinate ends | What restores it | Measured |
|---|---|---|
| Normal exit, Ctrl+C, Ctrl+Break | caffeinate itself | instant |
| Terminal window or tab closed, logoff, shutdown | caffeinate's close handler, power settings first | instant |
| Killed (Task Manager, `Stop-Process -Force`, `taskkill /F`) | a watcher that holds a handle to every caffeinate that changed something | ~0.1 s |
| Whole process tree killed (`taskkill /T /F`, "End process tree") | the same watcher, which Task Scheduler starts so it isn't in caffeinate's tree | ~0.1 s |
| The watcher was killed as well | a repair task at logon and every 5 minutes, or `caffeinate --restore` | ≤ 5 min |

A value *you* changed in the meantime is never overwritten. When several caffeinates change the same
setting, the last one out restores it.

Windows Update (`--hold-updates`) works the same way. The elevated helper re-launches itself
detached, so it survives a process-tree kill, and it lifts the pause the moment caffeinate's process
ends: measured at 2–13 ms for a kill, a tree kill and a window close. If even the helper is killed,
the pause runs out by itself within 15 minutes, because the helper renews it in 15-minute leases.

`--status` shows everything that's currently changed.

## Windows caveats

- **Modern Standby (S0 Low Power Idle)**, which covers most modern laptops and all ARM64 ones:
  - A scheduled wake only works fully if the PC is awake, hibernated, or was put to sleep by a
    caffeinate `--sleep` schedule, since caffeinate hibernates for those.
  - Timer wakes from hibernate are reported to work reliably on AC power only.
- Without `--ignore-lid`, closing the lid sleeps the PC as usual, just like on macOS.
- Scheduled tasks run only while you're signed in. That's what lets them run without admin rights
  or a stored password.
- Power requests are tied to the process. If caffeinate is killed, Windows drops them immediately.

## Verify it's working

In an **elevated** terminal, while caffeinate is running:

```powershell
powercfg /requests
```

The entries show up under `DISPLAY:` / `SYSTEM:` as `[PROCESS] ...\caffeinate.exe` with the reason
`caffeinate command-line tool`.

## Development

```
src/          the app (C#, .NET 10, Native AOT)
tests/        end-to-end tests: run-tests.ps1 + harness.cs (compiled with the C# compiler built into Windows)
installer/    Inno Setup script
build.ps1     build one architecture, optionally with its installer
release.ps1   build both architectures + installers, run the tests, publish a GitHub release
```

Requirements:
- the .NET 10 SDK
- the Visual Studio "Desktop development with C++" workload (needed for Native AOT)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php), only for building installers

```powershell
.\build.ps1                          # publish\<rid>\caffeinate.exe for this PC
.\build.ps1 -Rid win-x64 -Installer  # x64 exe + publish\installers\caffeinate-<ver>-windows-x64-setup.exe
.\tests\run-tests.ps1                # end-to-end tests against publish\<rid>\caffeinate.exe
.\release.ps1                        # bump <Version> in caffeinate.csproj and add a CHANGELOG entry first
```

GitHub Actions builds both architectures on every push, smoke-tests them on native x64 and arm64
runners, and keeps the installers as build artifacts.
### Tests

```powershell
.\tests\run-tests.ps1                                       # tests .\publish\<rid>\caffeinate.exe
.\tests\run-tests.ps1 -Exe publish\win-x64\caffeinate.exe
```

These are end-to-end tests. A small harness (`tests/harness.cs`, compiled with the C# compiler built
into Windows) launches caffeinate in real hidden consoles and sends it real Ctrl+C, Ctrl+Break and
window-close signals. The suite covers:
- background jobs (`start /b`)
- no console at all (GUI launchers, scheduled tasks)
- pipes and redirection, and cmd.exe
- `.cmd` resolution
- Git Bash and WSL interop (skipped if not installed)
- the extended options:
  - validation and schedule maths with a pinned clock and time zone, including DST and midnight
    crossing
  - settings being restored after a normal exit, Ctrl+C, window close and a hard kill
  - schedules being registered and removed
  - a real Task Scheduler wake firing the windowless agent
- updates, against a local stand-in for the GitHub API:
  - newer and older releases
  - a tampered download being refused
  - the standalone exe replacing itself

None of the tests sleep the PC or turn the screen off.

Run it elevated to also check `powercfg /requests`, including that the requests are released when
the process is killed.

## License

MIT. See [LICENSE](LICENSE).

A Linux version lives in a separate repository.
