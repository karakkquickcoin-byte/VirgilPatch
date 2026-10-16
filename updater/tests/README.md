# Updater regression tests

From this directory, in PowerShell:

```powershell
$testRoot = Join-Path $env:TEMP ('VirgilTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /optimize+ /main:SecurityTests "/out:$testRoot\SecurityTests.exe" /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ../VirgilUpdater.cs SecurityTests.cs
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
