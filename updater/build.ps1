param([string]$SigningKeyPath=$env:VIRGIL_UPDATER_SIGNING_KEY, [switch]$CompileOnly)
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler was not found.' }
if (-not $CompileOnly -and (-not $SigningKeyPath -or -not (Test-Path -LiteralPath $SigningKeyPath))) {
    throw 'Release builds require -SigningKeyPath pointing to the private DPAPI-protected release key. Use -CompileOnly for local development.'
}
if (-not $CompileOnly -and (Test-Path -LiteralPath (Join-Path (Split-Path $PSScriptRoot) '.git'))) {
    $repoRoot=Split-Path $PSScriptRoot
    $tracked=@(& git -C $repoRoot ls-tree --name-only HEAD -- updater-release.json)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the previous committed updater release.' }
    if ($tracked.Count -gt 0) {
        $previous=(& git -C $repoRoot show HEAD:updater-release.json | Out-String | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read the previous committed updater release.' }
        $source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'VirgilUpdater.cs'))
        $versionMatch=[regex]::Match($source,'AssemblyVersion\("([^"]+)"\)')
        if (-not $versionMatch.Success -or [version]$versionMatch.Groups[1].Value -le [version]$previous.version) {
            throw 'Release version must increase before publishing another automatic updater upgrade.'
        }
    }
}
$wpf=Join-Path (Split-Path $compiler) 'WPF'
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /optimize+ /out:VirgilUpdater.exe /r:System.Web.Extensions.dll "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /win32icon:virgil.ico /win32manifest:app.manifest /resource:assets/lordaeron-background.png,VirgilPatch.LauncherBackground /resource:assets/lordaeron-ambience.mp3,VirgilPatch.LauncherAmbience VirgilUpdater.cs SelfUpdate.cs LauncherTheme.cs LauncherAmbience.cs LauncherLinks.cs
    if ($LASTEXITCODE -ne 0) { throw 'Updater build failed.' }
    if ((Get-Item -LiteralPath 'VirgilUpdater.exe').Length -gt 16MB) { throw 'Updater exceeds the 16 MiB size limit trusted by existing self-updating clients.' }
    if ($CompileOnly) { return }
    $entropy=[Text.Encoding]::UTF8.GetBytes('VirgilPatchUpdaterReleaseKey/v1')
    $clear=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($SigningKeyPath),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa=[Security.Cryptography.RSACryptoServiceProvider]::new([Security.Cryptography.CspParameters]::new(24))
    $rsa.PersistKeyInCsp=$false
    try {
        try { $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($clear)) } finally { [Array]::Clear($clear,0,$clear.Length) }
        $public=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release-public-key.xml')).Trim()
        if ($rsa.ToXmlString($false) -ne $public -or -not ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SelfUpdate.cs')).Contains($public))) {
            throw 'Release key does not match the public key embedded in the updater.'
        }
        $exe=Get-Item -LiteralPath (Join-Path $PSScriptRoot 'VirgilUpdater.exe')
        $version=$exe.VersionInfo.FileVersion
        $hash=(Get-FileHash -Algorithm SHA256 -LiteralPath $exe.FullName).Hash.ToLowerInvariant()
        $sizeText=$exe.Length.ToString([Globalization.CultureInfo]::InvariantCulture)
        $message="VirgilPatchUpdaterRelease/v1`n$version`nupdater/VirgilUpdater.exe`n$sizeText`n$hash`n"
        $signature=[Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($message),[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')))
        $manifest=[ordered]@{ format=1; version=$version; file='updater/VirgilUpdater.exe'; size=$exe.Length; sha256=$hash; signature=$signature }
        $repoRoot=Split-Path $PSScriptRoot
        [IO.File]::WriteAllText((Join-Path $repoRoot 'updater-release.json'),($manifest | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zipPath=Join-Path $repoRoot 'VirgilUpdater.zip'
        $newZip=$zipPath+'.new'
        if (Test-Path -LiteralPath $newZip) { throw 'Stale build ZIP exists; inspect it first.' }
        $zip=[IO.Compression.ZipFile]::Open($newZip,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in @('VirgilUpdater.exe','How to use.txt')) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $PSScriptRoot $name),$name,[IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $zip.Dispose() }
        if (Test-Path -LiteralPath $zipPath) { [IO.File]::Replace($newZip,$zipPath,[NullString]::Value) } else { [IO.File]::Move($newZip,$zipPath) }
        $zipHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($zipPath+'.sha256',"$zipHash  VirgilUpdater.zip`n",[Text.UTF8Encoding]::new($false))
        Write-Output "Built and signed updater $version. Publish the EXE, ZIP, checksum and updater-release.json together."
    } finally { $rsa.Dispose() }
} finally { Pop-Location }
