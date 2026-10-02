# VirgilPatch

Epsilon (9.2.7) patches - starting with the Chronicles texture patch, with LODs and texture optimisation - kept up
to date from here.

## Install / update

1. Download **[VirgilUpdater.zip](VirgilUpdater.zip?raw=1)** and unzip `VirgilUpdater.exe` anywhere, e.g. into your
   Epsilon `_retail_` folder.
2. Run it. If it doesn't find your `_retail_\Patches` folder itself, click **Browse...** and pick it.
3. Click **Update**. Close WoW first.

The first run downloads everything once. After that, each update only downloads the files that changed. Files you
already have are checked and kept.

**"Windows protected your PC" / "unrecognised app"**: click **More info** → **Run anyway**. Windows and browsers warn
about every program that isn't signed with a paid code-signing certificate. If your antivirus quarantines it, restore
it and add an exception - it's a false positive. The updater is a small .NET program (version info, manifest and icon
included); its full source is [updater/VirgilUpdater.cs](updater/VirgilUpdater.cs), and it only talks to this
GitHub repository.

## What it does

* reads `index.json` (every patch folder, every file, its FileDataID, size and git SHA-1) from the latest commit
* installs each published patch as its own folder in `Patches`, downloading only missing or different files and
  verifying each one
* writes each `patch.json` last, so the game never sees a file list whose files aren't there yet
* removes files that an earlier update installed and the new version dropped - including a whole patch folder that is
  no longer published (files you changed yourself are left alone)
* remembers your folder in `%APPDATA%\VirgilPatch\state.json`

## Layout

* `patches/<folder>/` - each patch folder exactly as it is installed (`patch.json` + files)
* `index.json` - manifest the updater reads
* `updater/` - updater source and build
