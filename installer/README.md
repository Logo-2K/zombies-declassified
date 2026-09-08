# Zombies Declassified installer

Source of `ZombiesDeclassified-Updater.exe`, the installer and updater shipped with each release.

## What it does

- Reads the latest release's `manifest.json`: every file of the pack with its path, size and hashes, and a download URL.
- Finds the Plutonium storage folder and the Steam Black Ops II folder (or asks for them).
- Downloads each file, resuming partial downloads, verifies its hash and places it.
- Backs up anything it replaces under its `backups` folder, and removes leftovers of earlier releases listed in the manifest.
- `verify` reports files that no longer match; `update` fetches only what changed; `uninstall` removes the pack and restores what it displaced.
- Files under the Steam folder are placed by a short elevated step; everything else runs unelevated.

## Build

Requires the .NET 8 SDK.

```powershell
.\build.ps1 -Tag beta2
```

`-Tag` names the pack release the build belongs to. The installer's own version is the line in `updater.version` (major.minor.patch); bump it for every build that ships. The published executable, `updater-version.json` (`{"version": ..., "tag": ...}`) and `SHA256SUMS.txt` are in the releases section. The hash in `SHA256SUMS.txt` on a release matches the executable attached to that release. A running installer compares the release's `updater-version.json` with its own version and says when a newer one exists.

## Command line

```
ZombiesDeclassified-Updater.exe <install|update|verify|uninstall|paths> [--dry-run] [--bo2-path <dir>] [--pluto-path <dir>] [--manifest <path|url>] [--force]
```

A file already sitting where one of the pack's files goes is replaced when its hash is one the manifest lists as an earlier release's (`priorSha256`). Any other content is foreign: the plan names it by path, the wizard asks before replacing it, and the command line keeps it unless `--force` is given. Kept and failed rows are recorded as not placed (never as installed), the run exits 4 when rows were kept, and `verify` lists them as `NOTPLACED`. Replaced foreign files are backed up under `backups\foreign` and restored by `uninstall`.
