# Updater regression tests

From this directory, in PowerShell:

```powershell
$testRoot = Join-Path $env:TEMP ('VirgilTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /optimize+ /main:SecurityTests "/out:$testRoot\SecurityTests.exe" /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ../VirgilUpdater.cs ../SelfUpdate.cs ../LauncherTheme.cs /resource:../assets/lordaeron-background.png,VirgilPatch.LauncherBackground SecurityTests.cs
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
quoting. Compile it alongside `VirgilUpdater.cs`, `SelfUpdate.cs` and `LauncherTheme.cs` (embed the background as above) with `/main:SelfUpdateTests`. It takes a
test fixture EXE (assembly name `VirgilUpdater`, version `1.4.1.0`) and a test-only private RSA XML key path.
Never use a production signing key for these fixtures. The accompanying audit's process harness tests real
replacement/restart, startup rollback, tampering, invalid signatures, downgrade rejection, read-only EXEs and the
actual Program/MainForm startup confirmation using a separate temporary test key and invisible offline windows.

`LauncherTests.cs` exercises the actual window with synthetic offline assets: startup checks without installing,
explicit Update with disabled actions during download, no-op updates, changed-file-only downloads, folder
retention, diagnostic disclosure and recoverable manifest failure. Compile with `/main:LauncherTests`, the three
updater source files, `System.Web.Extensions.dll` and the embedded background resource shown above. Pass a new
empty scratch folder, followed optionally by an output PNG path for an invisible-window updating preview.

`SelfUpdateGuiTests.cs` uses the same sources/resource and `/main:SelfUpdateGuiTests`. Pass a new scratch folder
and `network` or `signature` to verify that program-upgrade failures still allow normal patch checks.
