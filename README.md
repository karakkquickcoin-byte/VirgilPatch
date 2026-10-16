# VirgilPatch

Epsilon (9.2.7) game patches, maintained as independently selectable patch folders.

## Install / update

1. Download [VirgilUpdater.zip](VirgilUpdater.zip?raw=1) from this repository and extract it.
2. Run `VirgilUpdater.exe`. Browse to your Epsilon `_retail_\Patches` folder if needed.
3. Close WoW, then click **Update**.

Every update downloads only missing or changed assets as individual files, including large updates and first installs.
The updater never downloads the full repository ZIP. It checks existing files against the current index before updating;
if all assets already match, none are downloaded again. The index is still retrieved to check for changes.
Existing v1.2/v1.3.0 updaters remain compatible without replacing the EXE. On large updates they may still
display "Downloading the full archive", but that generated archive now contains only source/updater files;
game assets are excluded. They then fetch only the missing or changed assets through their existing fallback.
Start a fresh check to pick up this repository change. An already running update stays pinned to its earlier commit.
New downloads use v1.3.1, which skips the archive request entirely. Replacing an old EXE is still necessary to
receive the newer application security protections; the old updater cannot update its own program.
Classic and TexturePatch remain independently published alternatives; Epsilon controls which is enabled.

## Antivirus alerts

If your antivirus reports a threat, stop and keep the file blocked or quarantined.
Do not disable protection, restore a detected file, or add an exclusion to install this updater.
Report the download URL, the threat name, the affected file path and the ZIP's SHA-256 hash.
The developer can submit the exact detected file to [Microsoft for analysis](https://www.microsoft.com/en-us/wdsi/filesubmission).
A clean source review or matching checksum does not establish that an antivirus alert is a false positive.

Version 1.3.1 is unsigned. A Windows reputation warning and an antivirus malware detection are different checks;
neither should be dismissed solely because a program is open source or unsigned.

## What the updater does

* Resolves one commit in this GitHub repository over HTTPS, then uses that commit for every file in the update.
* Validates manifest paths and limits installation to supported game assets inside the selected Patches folder.
* Rejects symbolic links and junctions in source, destination and state paths; choose the real folder path.
* Checks declared sizes and git blob SHA-1 hashes before replacing downloaded files.
* Writes each verified `patch.json` after its assets and checks that its file list matches the manifest.
* Rechecks old files before deleting them and retains files you have edited yourself.
* Remembers your selected folder in `%APPDATA%\VirgilPatch\state.json`.

The updater requests normal user permissions. It does not run downloaded assets or install another program.
These integrity checks trust this repository and GitHub's HTTPS delivery. They are not a signed release system
or a guarantee about how the game parses asset files. See [SECURITY.md](SECURITY.md).

## Layout

* `patches/<folder>/`: published game assets and `patch.json`
* `index.json`: version manifest
* `updater/`: reviewed source and Windows build

## Build from source

Open PowerShell in `updater` and run `./build.ps1`. This uses the .NET Framework compiler already installed
with Windows; no downloaded build tools are required. The resulting EXE remains unsigned.

## Repository source archives

GitHub's **Code > Download ZIP** and generated source archives omit `patches/` and `index.json` to keep older
updater downloads small. The files remain tracked in the repository and are available through raw URLs or a Git clone.
For patch installation use the updater. The separate `VirgilUpdater.zip` download still contains the latest updater.
