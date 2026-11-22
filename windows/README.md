# caffeinate for Windows

The macOS `caffeinate` command, for Windows. Same flags and syntax, single native `.exe`
(no runtime needed). Works from PowerShell, cmd and Git Bash.

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
command returns 127. Ctrl+C while a utility runs goes to the utility, and caffeinate keeps holding
until the utility actually exits.

Additions not in macOS: `-h` / `--help`.

## Windows caveats

- **Modern Standby (S0 Low Power Idle) laptops**, which includes most ARM64 machines: turning the
  display off moves the machine toward standby. To truly keep the machine up, include `-d`.
  `-dismu` already does.
- As on macOS, none of these stop sleep when you **close the lid** or **press the power button**.
  That is controlled by your power plan's lid/button actions.
- Assertions are tied to the process. If caffeinate is killed, Windows drops them immediately, so
  nothing gets stuck on.

## Verify it's working

In an **elevated** terminal, while caffeinate is running:

```powershell
powercfg /requests
```

The entries show up under `DISPLAY:` / `SYSTEM:` as `[PROCESS] ...\caffeinate.exe` with the reason
`caffeinate command-line tool`.

## Build / install

Requirements: the .NET 10 SDK and the Visual Studio "Desktop development with C++" workload (needed for Native AOT).

```powershell
.\build.ps1 -Install          # builds and copies caffeinate.exe to ~\.local\bin
.\build.ps1 -Rid win-x64      # cross-build for x64
```
