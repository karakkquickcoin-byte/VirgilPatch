$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler was not found.' }
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /optimize+ /out:VirgilUpdater.exe /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /win32icon:virgil.ico /win32manifest:app.manifest VirgilUpdater.cs
    if ($LASTEXITCODE -ne 0) { throw 'Updater build failed.' }
} finally { Pop-Location }
