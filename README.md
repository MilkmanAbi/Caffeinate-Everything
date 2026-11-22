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
| Windows  | ✅ Available | [`windows/`](windows/) |
| Linux    | Planned | |
| macOS    | Built in (`/usr/bin/caffeinate`) | |

Each platform's directory has its own build instructions and notes on how each flag maps to
that OS.
