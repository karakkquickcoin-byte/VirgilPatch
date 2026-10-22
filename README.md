# VirgilPatch

Epsilon (9.2.7) game patches, maintained as independently selectable patch folders.

The 1.5.1 interface adds a silent, embedded 12-second falling-leaves background. It is cropped from the
supplied scene to keep leaf motion near the right third and remove recording overlays. A soft blend joins the
repeat point. The original separate ambience stays at 40%; the video adds no sound. Playback pauses while
minimized/hidden and uses a matching still image if decoding is unavailable. No external media download is needed.

The 1.5.0 patch engine, recovery/selection controls, JSON validation, publisher fixes and signed-upgrade trust are
unchanged in this visual release. Patches still require Update, selection and confirmation.

## Install / update

1. Download [VirgilUpdater.zip](VirgilUpdater.zip?raw=1) from this repository and extract it.
2. Run `VirgilUpdater.exe`. In the Chronicles of Lordaeron launcher, click **Select folder** and choose your Epsilon `_retail_\Patches` folder if needed.
3. Close WoW, click **Update**, and select only the patch folders you want.
4. Review **What's new / repair preview**, then confirm **Update / repair selected**.

Launcher **1.5.1** repairs existing interrupted installations as well as installing updates. It reuses matching
assets and restores missing or corrupt `patch.json` after that patch is complete. A failure in one patch does
not prevent another completed patch from being registered. Progress includes percentage, transfer size and ETA;
Cancel keeps completed files for the next attempt. Invalid content stops promptly; transient transfers get at most
three attempts with bounded timeouts.

Choose Classic, Northrend, HD or other folders separately. Unselected patches and their tracking stay untouched.
The preview lists your required changes/repairs and the publisher's latest change summary. The first 1.5.0 check
re-verifies existing selected assets locally once to discard stale legacy caches. Later checks reuse verified
size/time/hash entries. **Deep repair** rechecks every selected asset if hidden corruption is suspected.

If an older updater is still stuck downloading, close it and reopen it so it resolves the repaired publication.
Existing self-updating versions receive 1.5.0 through the same signed channel. Versions 1.4.2/1.4.3 check that
channel when Update is clicked; 1.4.4 checks at launch. Patch files still change only after explicit selection
and confirmation. Versions before 1.4.0 need a one-time launcher download to obtain these new interface features.

Every update downloads only missing or changed assets as individual files, including large updates and first installs.
The updater never downloads the full repository ZIP. It checks existing files against the current index before updating;
if all assets already match, none are downloaded again. The index is still retrieved to check for changes.
Existing v1.2/v1.3.0 updaters remain compatible without replacing the EXE. On large updates they may still
display "Downloading the full archive", but that generated archive now contains only source/updater files;
game assets are excluded. They then fetch only the missing or changed assets through their existing fallback.
Start a fresh check to pick up this repository change. An already running update stays pinned to its earlier commit.
New downloads use v1.4.4, which skips the archive request entirely and supports automatic program upgrades. Replacing an old EXE once is still necessary to
receive the newer application security protections and automatic-upgrade support; v1.2/v1.3.x cannot update their own program.
The launcher uses a single Update button, an instruction panel and embedded Lordaeron artwork.
Existing v1.4.0 and newer launchers receive this design through their automatic program upgrade.
Classic and TexturePatch remain independently published alternatives; Epsilon controls which is enabled.

Version 1.4.3 adds resizing (drag any edge or the bottom-right grip; the artwork ratio is kept), a **Guides & Links**
window with the Chronicles guides, registers, wiki and the Watch2gether music room (opened in your browser; only
those fixed https addresses can be opened), and an **Epsilon phase ID 35602** plaque that copies the id when clicked.

The full ambience track plays automatically at 40% player volume and loops while the launcher is open.
Use **Sound on (40%) / Sound off** at the bottom right to mute or unmute it. Audio stops when the launcher closes;
patch updates still work if Windows cannot play the audio. Headless `--cli` updates never play sound; `--mute` starts
the GUI muted. The soundtrack is embedded in the EXE and cached locally with SHA-256 verification.

## Automatic updater upgrades

**Golden rule (v1.4.4+):** the launcher program updates itself on every launch when a newer signed release is published -
it downloads only the signed EXE, verifies it, restarts and keeps the previous EXE; no redownload is needed. Patch files
are never checked or changed at launch, only when **Update** is clicked. Launchers 1.4.2/1.4.3 check for program upgrades
only on Update, so they receive the latest release on their next Update click; newer launchers check at launch.

Versions 1.4.2 and later never scan or change patch files at launch. Selecting a folder
only remembers its path. **Update** explicitly starts both the signed launcher-upgrade check and patch updating.
Clicking Update synchronizes published files, so do this only when you intend to update your selected patches.
If a launcher upgrade restarts the app, click Update again to update patches; the restarted window stays idle.
Earlier v1.4.0/v1.4.1 programs check their own upgrades on startup and can receive this version once.
Their program upgrade does not install patch files.
A newer program release is verified against an RSA-4096 public key embedded in the app, including its version,
SHA-256 and exact size. It downloads only the updater EXE, restarts automatically and retains the previous EXE.
If the new program fails to confirm startup, the helper restores the previous version and avoids retrying that
same failed release. Your Patches folder and saved selection are retained.

Keep the updater in a normal folder you can write to, and close other copies while upgrading. A failed download,
invalid signature or unwritable folder leaves the current program in place and patch checks can continue.
No administrator elevation or antivirus exclusions are requested. Users with an older EXE need to replace it
once to enable this feature; future signed releases can then arrive automatically.

## Antivirus alerts

If your antivirus reports a threat, stop and keep the file blocked or quarantined.
Do not disable protection, restore a detected file, or add an exclusion to install this updater.
Report the download URL, the threat name, the affected file path and the ZIP's SHA-256 hash.
The developer can submit the exact detected file to [Microsoft for analysis](https://www.microsoft.com/en-us/wdsi/filesubmission).
A clean source review or matching checksum does not establish that an antivirus alert is a false positive.

The launcher is not Authenticode-signed. A Windows reputation warning and an antivirus malware detection are different checks;
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
Game-asset integrity checks trust this repository and GitHub's HTTPS delivery. Game assets are not individually
release-signed, and these checks do not guarantee how the game parses asset files. See [SECURITY.md](SECURITY.md).

## Layout

* `patches/<folder>/`: published game assets and `patch.json`
* `index.json`: version manifest
* `updater/`: reviewed source and Windows build
* `publisher/`: matching publisher source and snapshot-integrity validation

## Build from source

For a local development build, open PowerShell in `updater` and run `./build.ps1 -CompileOnly`.
This uses the .NET Framework compiler already installed with Windows; no downloaded build tools are required.
A private release key is required to publish automatic upgrades. See [the maintainer release instructions](updater/RELEASING.md).
Release-metadata signatures authenticate program upgrades after the initial installation; they are separate from
Windows Authenticode signatures and do not establish an antivirus clearance.

## Repository source archives

GitHub's **Code > Download ZIP** and generated source archives omit `patches/`, `index.json` and the compiled EXE/ZIP to keep older
updater downloads small. The files remain tracked in the repository and are available through raw URLs or a Git clone.
For patch installation use the updater. The separate `VirgilUpdater.zip` download still contains the latest updater.


### Online patch versions

Patch releases start at `5.0.0`, then advance independently to `5.0.0.1`, `5.0.0.2`, and so on when published content changes. No-op scans, no-op publishes, and retries of a completed commit do not consume another version. Publisher tables show the online version; the updater chooser shows Installed and Available versions.

The permanent `index.json` key remains the internal tracking identity. Updater 1.5.3 maps it to a fixed local `Name(5.0.0)` folder and migrates old aliases on a confirmed Update. `version`, `display_name`, and the strictly derived `repo_dir` describe the online release, for example key `!Chronicles-TexturePatch(3.2.0)` with repository path `patches/!Chronicles-TexturePatch(5.0.0)`. Only the Git mirror and published `patch.json` receive the new name/version. Publisher source folders and their metadata are never renamed or rewritten by release numbering. The updater continues to use stable internal keys for selection, hashes, repair, resume, and retirement; renaming online assets does not force unchanged assets to download.

Use updater 1.5.3 or later for folder migration; 1.5.2 supports the versioned catalog but retains legacy local folder names. Startup still checks the signed program channel only; patches still require explicit Update, selection and confirmation. Path, link, extension, ID, hash, byte-limit, signature and atomic-publication checks remain enforced.

Maintainers can optionally set `E:\M2FX_Work\virgil\repository.json` to `{"path":"E:\\path\\to\\mirror"}` to use a different Git mirror without changing `published.json` source paths. The mirror must track the same GitHub main branch; keep existing pending commits and staged work separate.

The configured publisher mirror may use Git sparse checkout: its working folders can contain only metadata and recently changed assets. Unchanged assets remain in verified Git objects and are staged under their new online paths without copying or reuploading their bytes. The final catalog is rebuilt from the complete staged Git snapshot, not from the sparse directory listing. Before a normal online publish, the publisher fetches GitHub main and accepts only a fast-forward update; diverged pending commits stop publication for reconciliation.


### Existing-folder migration (updater 1.5.3)

On an explicitly confirmed patch update, the updater consolidates recognized old folder versions into a fixed `Name(5.0.0)` local folder. It prefers existing files matching the current published checksum, including useful files from another duplicate, so it does not redownload matching payloads. Folder metadata versions rank the starting copy; an inaccurate folder label does not override verified content.

The chooser/preview reports the rename. Checking, cancelling and ordinary launcher startup do not move game folders. The winning directory is renamed on the same volume, with its hash cache carried over. Repair, selected-patch isolation and resumable installation retain stable internal keys; a validated folder map survives restarts and supports retirement of renamed packages. Future `5.0.0.1` updates change version metadata and assets inside the existing `Name(5.0.0)` directory.

After the current catalog's assets and patch metadata verify, duplicate folders move outside the active Patches directory into sibling `VirgilPatch-Migration-Backups` for rollback. This removes active duplicates quickly without deleting user data. Failed/cancelled updates retain recovery copies. Recognized package families are exact matches; unrelated packages stay untouched. Migration rejects reparse points/junctions before moving directories and never recursively deletes a directory tree.

### Updater 1.5.4 verification

Update previews and Details identify the exact selected Patches root and install destinations. A checked plan cannot be applied to another directory. Latest-release added/changed files and files newer than installed state bypass the hash cache. New downloads and files reused by copying from duplicates pass three checks: staged bytes, reopened installed bytes, and a final uncached read before metadata finalization. Additional content reads are limited to affected assets; ordinary unchanged-file checks and optional full Deep repair remain available. Opening the updater still updates only its software; game patch installation and migration require the existing Update confirmation. This does not establish which Epsilon installation someone launches or enable patches in Epsilon.

### Updater 1.5.5 diagnostics

Copy diagnostics generates a read-only support report with report-local path aliases, detected direct patch folders and metadata versions, nearby installation candidates, running WoW locations if accessible, selected/checked destinations, migration events and categorized error codes. It excludes raw logs, exception messages, drive/network identifiers and arbitrary personal path components. The report is copied only on request; it is not uploaded. No patch content is scanned for diagnostics and the chosen installation is never switched automatically. An Epsilon/Epsilon installation remains distinguishable from Epsilon because game path components are preserved.

### Updater 1.5.6 folder confirmation and changelog

Every fifth manual Update use checks the Epsilon.exe/Data main installation markers, _retail_/Wow.exe/EpsilonAddon.dll, the existing Patches directory and nearby installations. A themed dialog asks the user to confirm the exact path; missing required markers disable confirmation. No reopens folder selection, and Cancel stops patch work. Problem installing? Check folders in the patch chooser runs this flow immediately. A successful confirmation resets the saved five-use counter. These are layout checks, not executable authenticity checks. The gear at bottom left opens the history of updater releases. Software startup still never updates game patches automatically.

### Updater 1.5.7 additive security checks

Five protections extend the existing update flow: strict signed-release metadata parsing; strict saved-state parsing and unsafe tracking-entry rejection; immutable Git commit and relative download path checks; complete Windows reserved-device-name rejection, including superscript COM/LPT names; and an exclusive per-Patches update lock that prevents simultaneous writers and releases on failure/process exit. Existing repair, migration, privacy, folder confirmations and three-stage verification remain. These tests establish the covered updater behaviour, not an antivirus verdict.
