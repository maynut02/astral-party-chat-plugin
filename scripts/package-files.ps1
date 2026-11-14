#requires -Version 7.2

function Assert-AstralPackageDirectory([string]$Root, [string[]]$AllowedFiles) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw 'Release output must be a directory.' }
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force) {
        if ($entry.PSIsContainer -or $entry.Name -cnotin $AllowedFiles) {
            throw 'Release output directory contains an unexpected file. Use a separate empty directory.'
        }
    }
}

function Assert-AstralPluginAssembly([string]$Path, [string]$Name, [string]$Version) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing plugin DLL: $Path" }
    if ([Reflection.AssemblyName]::GetAssemblyName($Path).Name -cne $Name) { throw "Unexpected plugin assembly: $Path" }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion -cne $Version) {
        throw 'Package tag must match the built DLL version. Prepare the VERSION file and rebuild before packaging.'
    }
}

function Get-AstralPluginEntry { 'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll' }

# [0] is the DLL-only package installed plugins download; [1] is the installer package players download.
# Installed plugins accept only these exact names, so they must not change between releases.
function Get-AstralReleaseAssetNames([string]$Tag) {
    @("update-$Tag.zip", "AstralPartyChatPlugin-$Tag.zip", 'update.json', 'update.json.sig', 'SHA256SUMS.txt')
}

function Get-AstralBepInExArchive {
    param([string]$WorkRoot, [string]$ExpectedSha256 = 'f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a', [long]$ExpectedSize = 34336405)

    $name = 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip'
    $path = Join-Path $WorkRoot "bepinex/$name"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        $temporary = "$path.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            Invoke-WebRequest -Uri 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip' -OutFile $temporary
            if ((Get-Item -LiteralPath $temporary).Length -ne $ExpectedSize -or
                (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedSha256) {
                throw 'Pinned BepInEx download size or SHA-256 mismatch.'
            }
            Move-Item -LiteralPath $temporary -Destination $path
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        }
    }
    if ((Get-Item -LiteralPath $path).Length -ne $ExpectedSize -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedSha256) {
        throw 'Pinned BepInEx cache size or SHA-256 mismatch. Remove the corrupt cache file and retry.'
    }
    return $path
}

# Files maps a ZIP path to a source file path or to literal bytes. BaseArchive entries are copied first.
function New-AstralZipPackage {
    param([Collections.IDictionary]$Files, [string]$OutputRoot, [string]$ZipName, [string]$BaseArchive = '')

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $zipPath = Join-Path $OutputRoot $ZipName
    $temporary = Join-Path $OutputRoot ('.' + $ZipName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        # Build a complete replacement before overwriting a previous ZIP.
        $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            if ($BaseArchive) {
                $source = [IO.Compression.ZipFile]::OpenRead($BaseArchive)
                try {
                    foreach ($entry in $source.Entries) {
                        if (-not $names.Add($entry.FullName)) { throw 'Duplicate BepInEx ZIP entry.' }
                        $copy = $archive.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                        $copy.LastWriteTime = $entry.LastWriteTime
                        $inputStream = $entry.Open()
                        $outputStream = $copy.Open()
                        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
                    }
                } finally { $source.Dispose() }
            }
            foreach ($file in $Files.GetEnumerator()) {
                if (-not $names.Add($file.Key)) { throw "Package path collides with BepInEx: $($file.Key)" }
                if ($file.Value -is [byte[]]) {
                    $stream = $archive.CreateEntry($file.Key, [IO.Compression.CompressionLevel]::Optimal).Open()
                    try { $stream.Write($file.Value, 0, $file.Value.Length) } finally { $stream.Dispose() }
                } else {
                    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.Value, $file.Key, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
                }
            }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $zipPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    return [pscustomobject]@{ Path = $zipPath; Sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant(); Size = (Get-Item -LiteralPath $zipPath).Length }
}

function Get-AstralSigningKeyPath([string]$Path = '') {
    if ($Path) { return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path) }
    if ($env:ASTRAL_UPDATE_SIGNING_KEY) { return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($env:ASTRAL_UPDATE_SIGNING_KEY) }
    return Join-Path ([Environment]::GetFolderPath('UserProfile')) '.astral-party/plugin-update-signing.pem'
}

# The trust anchor lives in the plugin source only: a release is signed for the key that plugin build verifies.
function Get-AstralUpdatePublicKey {
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/UpdateManifest.cs'))
    $match = [regex]::Match($source, 'const string PublicKey = "(?<key>[A-Za-z0-9+/=]+)";')
    if (-not $match.Success) { throw 'Could not read the update public key from src/UpdateManifest.cs.' }
    return $match.Groups['key'].Value
}

function Get-AstralSigningKey {
    param([string]$Path, [string]$PublicKey = (Get-AstralUpdatePublicKey))

    $Path = Get-AstralSigningKeyPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing update signing key: $Path" }
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        # ImportFromPem rejects encrypted PEM; the public-key match below rejects every other key.
        $key.ImportFromPem([IO.File]::ReadAllText($Path))
        if ($key.KeySize -ne 256 -or [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) -cne $PublicKey) {
            throw 'Update signing key does not match the trusted P-256 public key. Rotate the embedded trust key deliberately before release.'
        }
        return $key
    } catch { $key.Dispose(); throw }
}

function Assert-AstralUpdateSignature {
    param([byte[]]$Bytes, [string]$Signature, [string]$PublicKey = (Get-AstralUpdatePublicKey))

    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $read = 0
        $der = [Convert]::FromBase64String($PublicKey)
        $key.ImportSubjectPublicKeyInfo($der, [ref]$read)
        $signatureBytes = [Convert]::FromBase64String($Signature)
        if ($read -ne $der.Length -or $key.KeySize -ne 256 -or $signatureBytes.Length -ne 64 -or
            -not $key.VerifyData($Bytes, $signatureBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
            throw 'Update manifest signature does not match the trusted public key.'
        }
    } finally { $key.Dispose() }
}

# Writes update.json, update.json.sig and SHA256SUMS.txt beside the two ZIPs already in OutputRoot.
function New-AstralUpdateManifest {
    param([string]$Version, [string]$Repository, [string]$OutputRoot, [string]$DllPath, [string]$SigningKeyPath = '', [string]$PublicKey = (Get-AstralUpdatePublicKey))

    $names = @(Get-AstralReleaseAssetNames "v$Version")
    $name = $names[0]
    $package = Join-Path $OutputRoot $name
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        package = [ordered]@{
            name = $name
            url = "https://github.com/$Repository/releases/download/v$Version/$name"
            size = (Get-Item -LiteralPath $package).Length
            sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        files = @([ordered]@{
            path = Get-AstralPluginEntry
            size = (Get-Item -LiteralPath $DllPath).Length
            sha256 = (Get-FileHash -LiteralPath $DllPath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
        minimumBepInEx = '6.0.0-be.788'
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 5) + "`n")
    $key = Get-AstralSigningKey -Path $SigningKeyPath -PublicKey $PublicKey
    try { $signature = [Convert]::ToBase64String($key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) }
    finally { $key.Dispose() }
    Assert-AstralUpdateSignature -Bytes $bytes -Signature $signature -PublicKey $PublicKey
    [IO.File]::WriteAllBytes((Join-Path $OutputRoot 'update.json'), $bytes)
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'update.json.sig'), $signature, [Text.Encoding]::ASCII)
    $checksums = foreach ($asset in @($names[0], $names[1], 'update.json')) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $OutputRoot $asset) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $asset"
    }
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
}

# Everything an installed plugin will check, plus the installer package, verified before any upload.
function Assert-AstralReleaseAssets {
    param([string]$Tag, [string]$Repository, [string]$Root, [string]$PublicKey = (Get-AstralUpdatePublicKey))

    $names = @(Get-AstralReleaseAssetNames $Tag)
    $hashes = @{}
    foreach ($name in $names) {
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release asset: $name. Run scripts/package-release.ps1 first." }
        $hashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $expectedChecksums = @($names[0], $names[1], 'update.json') | ForEach-Object { "$($hashes[$_])  $_" }
    $checksums = [IO.File]::ReadAllLines((Join-Path $Root 'SHA256SUMS.txt'))
    if (($checksums -join "`n") -cne ($expectedChecksums -join "`n")) { throw 'Release checksums must match both ZIPs and update.json, with no extra entries.' }
    $bytes = [IO.File]::ReadAllBytes((Join-Path $Root 'update.json'))
    Assert-AstralUpdateSignature -Bytes $bytes -Signature ([IO.File]::ReadAllText((Join-Path $Root 'update.json.sig'))) -PublicKey $PublicKey
    $manifest = [Text.UTF8Encoding]::new($false, $true).GetString($bytes) | ConvertFrom-Json
    $zip = Join-Path $Root $names[0]
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $Tag.Substring(1) -or $manifest.minimumBepInEx -cne '6.0.0-be.788' -or
        $manifest.package.name -cne $names[0] -or $manifest.package.url -cne "https://github.com/$Repository/releases/download/$Tag/$($names[0])" -or
        $manifest.package.size -ne (Get-Item -LiteralPath $zip).Length -or $manifest.package.sha256 -cne $hashes[$names[0]]) {
        throw 'Update manifest does not describe this release package.'
    }
    $entryName = Get-AstralPluginEntry
    $files = @($manifest.files)
    if ($files.Count -ne 1 -or $files[0].path -cne $entryName) { throw 'Update manifest must list only the plugin DLL at its installation path.' }
    # Installed plugins refuse anything larger (UpdateManifest.PackageLimit), so such a release would update nobody.
    if ($manifest.package.size -gt 64MB -or $files[0].size -gt 64MB) { throw 'Update ZIP and plugin DLL must not exceed 64 MiB.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $readHash = {
        param($entry)
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne $entryName) {
            throw 'Update ZIP must contain only the plugin DLL at its installation path.'
        }
        if ($files[0].size -ne $archive.Entries[0].Length -or $files[0].sha256 -cne (& $readHash $archive.Entries[0])) {
            throw 'Update manifest file size or SHA-256 mismatch.'
        }
    } finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Root $names[1]))
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) { $entries[$entry.FullName] = $entry }
        foreach ($required in @($entryName, 'install.bat', 'install.ps1', 'BepInEx/config/BepInEx.cfg', 'BepInEx/core/BepInEx.Core.dll', 'winhttp.dll', 'LICENSE-BepInEx.txt')) {
            if (-not $entries.ContainsKey($required)) { throw "Install ZIP is missing $required." }
        }
        # BepInEx loads every DLL below plugins, so a second copy could be the one that runs.
        $plugins = @($archive.Entries | Where-Object { $_.Name -and $_.FullName.Replace('\', '/') -like 'BepInEx/plugins/*' })
        if ($plugins.Count -ne 1 -or $plugins[0].FullName -cne $entryName) { throw 'Install ZIP must contain nothing but this plugin below BepInEx/plugins.' }
        # Players who install by hand and players who auto-update must end up with the same DLL.
        if ($files[0].sha256 -cne (& $readHash $entries[$entryName])) { throw 'Install ZIP and update ZIP contain different plugin DLLs.' }
    } finally { $archive.Dispose() }
    return $hashes
}
