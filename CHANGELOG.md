# Changelog

## 1.2.1

- `caffeinate --help` shows the macOS flag descriptions (`-d -i -m -s -u -t -w`) under the usage line
  again, with the Windows extended options further down.

## 1.2.0

- **Updates:**
  - `caffeinate --check-update` and `caffeinate --update`. They pick the arm64 or x64 build to match
    your PC and verify the download against the release's `SHA256SUMS.txt` before running it.
  - Installed copies update through the installer. Standalone exes replace themselves.
- **Update notice:** a once-a-day check that prints a one-line notice when a new version is out.
  It never blocks or slows caffeinate. Turn it off with `caffeinate --update-check off` or the
  installer checkbox.
- **Installer:**
  - Adds the update-check option.
  - Refreshes the windowless agent used by saved schedules on upgrade.
  - Ships the README, LICENSE and CHANGELOG.
- **Repository:** now Windows-only, building from the root, with a GitHub Actions build for both
  architectures and a `release.ps1`.
- `--version` shows the architecture.

1.1.0 has no updater, so updating from 1.1.0 to 1.2.0 is a one-time manual install. Run the 1.2.0
installer over it; it upgrades in place.

## 1.1.0

- **Installers** for x64 and arm64 (Inno Setup). They install per user with no admin rights by
  default, and add caffeinate to PATH.
- **New options:**
  - Holding: `--for`, `--until`, `--screen-off`, `--ignore-lid`, `--battery-floor`, `--hold-updates`.
  - Schedules: `--sleep`, `--wake`, `--at`, `--repeat`, `--name`, `--dry-run`.
  - Managing: `--status`, `--list`, `--remove`, `--pause-updates`, `--resume-updates`, `--restore`.
- **Help:** `--help` shows the macOS usage line, with the extended options below it.
- **Restoring settings:** every temporary setting change is journaled and restored however
  caffeinate ends: exit, Ctrl+C, closing the window, a kill, or a process-tree kill.

## 1.0.0

- First release: the macOS `caffeinate` CLI for Windows. Supports `-d -i -m -s -u -t -w` and the
  utility form, with BSD getopt parsing.
