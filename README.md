# Caffeinate-Everything

macOS's `caffeinate` on every platform, with the same flags and syntax everywhere, so
`caffeinate -dismu` works wherever you are.

```
usage: caffeinate [-dimsu] [-t timeout] [-w pid] [utility arguments...]
```

| Flag | Meaning |
|------|---------|
| `-d` | Prevent the display from sleeping |
| `-i` | Prevent idle system sleep (default) |
| `-m` | Prevent disk idle sleep |
| `-s` | Prevent system sleep (AC power only) |
| `-u` | Declare the user active / wake the display |
| `-t n` | Hold for `n` seconds |
| `-w pid` | Hold until process `pid` exits |
| `utility ...` | Hold while the command runs, then exit with its exit code |

## Platforms

| Platform | Status | Directory |
|----------|--------|-----------|
| Windows  | ✅ Available, with installers. Extras: sleep/wake schedules, screen-off, lid, Windows Update pause | [`windows/`](windows/) |
| Linux    | Planned | |
| macOS    | Built in (`/usr/bin/caffeinate`) | |

Each platform's directory has its own build instructions and notes on how each flag maps to
that OS.

**Windows:** grab the installer from the
[latest release](https://github.com/MilkmanAbi/Caffeinate-Everything/releases/latest). `caffeinate --help`
shows the macOS usage line, with the Windows-only extended options below it.
