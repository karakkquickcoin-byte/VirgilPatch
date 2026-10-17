# Updater regression tests

From this directory, in PowerShell:

```powershell
$testRoot = Join-Path $env:TEMP ('VirgilTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path (Split-Path $compiler) 'WPF'
& $compiler /nologo /target:exe /optimize+ /main:SecurityTests "/out:$testRoot\SecurityTests.exe" /r:System.Web.Extensions.dll "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ../VirgilUpdater.cs ../SelfUpdate.cs ../LauncherTheme.cs ../LauncherAmbience.cs ../LauncherLinks.cs /resource:../assets/lordaeron-ambience.mp3,VirgilPatch.LauncherAmbience /resource:../assets/lordaeron-background.png,VirgilPatch.LauncherBackground SecurityTests.cs
& "$testRoot\SecurityTests.exe" $testRoot
```

The first argument is a new, empty scratch folder. These offline tests use synthetic manifests and game asset bytes.
They do not touch an existing installation or contact GitHub. An optional second argument points to a copied
production metadata snapshot (`index.json` and `patches/<name>/patch.json`). An optional third argument points
to a separate prepared fixture with a `jump` junction to a scratch folder, for junction rejection checks.

`NetworkSmoke.cs` is an optional network test. Compile with `/main:NetworkSmoke` instead and pass one nonexistent
output file path. It contacts only the configured public repository, retrieves its current index and verifies one
small game asset. It does not install patches. The test binary contains the updater code; use the explicit main selector.

The updater suite covers security safeguards and individual asset downloading, including more than 400 changed files,
an update exceeding the old 40-percent threshold, no-op updates, missing/corrupt assets and a lost state file.
The release also runs a first-install/second-run smoke test of the built EXE. Publisher code is unchanged in v1.3.1.


`SelfUpdateTests.cs` validates signatures, trusted payload scope, size/hash/version checks and Windows argument
quoting. Compile it alongside `VirgilUpdater.cs`, `SelfUpdate.cs`, `LauncherTheme.cs` and `LauncherAmbience.cs` (use both media resources and WPF references above) with `/main:SelfUpdateTests`. It takes a
test fixture EXE (assembly name `VirgilUpdater`, version `1.4.1.0`) and a test-only private RSA XML key path.
Never use a production signing key for these fixtures. The accompanying audit's process harness tests real
replacement/restart, startup rollback, tampering, invalid signatures, downgrade rejection, read-only EXEs and the
actual Program/MainForm startup confirmation using a separate temporary test key and invisible offline windows.

`LauncherTests.cs` exercises the actual window with synthetic offline assets: idle startup with zero patch requests,
explicit Update with disabled actions during download, no-op updates, changed-file-only downloads, folder
retention, diagnostic disclosure and recoverable manifest failure. Compile with `/main:LauncherTests`, the four
updater source files, WPF references, `System.Web.Extensions.dll` and both embedded media resources shown above. Pass a new
empty scratch folder, followed optionally by an output PNG path for an invisible-window updating preview.

`SelfUpdateGuiTests.cs` uses the same sources/resource and `/main:SelfUpdateGuiTests`. Pass a new scratch folder
and `network` or `signature` to verify that program-upgrade failures still allow normal patch checks.

`ManualUpdateTests.cs` verifies that launch and folder selection make zero source requests and preserve
unpublished development files and saved patch state, while explicit Update checks both channels. Compile with
the same four sources, resources and references and `/main:ManualUpdateTests`; pass a fresh scratch folder.

`AmbienceTests.cs` verifies the real full-track playback at volume 0.4, mute, end-of-track looping, shutdown,
cache integrity/read locks/reuse and safe patch checks after audio fails. Compile with `/main:AmbienceTests`
and the same sources/resources/references, then pass a fresh scratch folder. Playback tests start muted.
LauncherTests automatically answers only its own scratch-fixture WoW-running warning if the user's game is open.
It does not close or modify the game. Actual release EXE CLI smoke tests use synthetic files and never play audio.
