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

The published executable, `updater-version.json` and `SHA256SUMS.txt` are in the releases section. The hash in `SHA256SUMS.txt` on a release matches the executable attached to that release.

## Command line

```
ZombiesDeclassified-Updater.exe <install|update|verify|uninstall|paths> [--dry-run] [--bo2-path <dir>] [--pluto-path <dir>] [--manifest <path|url>] [--force]
```
