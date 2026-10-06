#requires -Version 7.0

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

function New-AstralPluginPackage([Collections.IDictionary]$Files, [string]$OutputRoot, [string]$ZipName) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $zipPath = Join-Path $OutputRoot $ZipName
    $temporary = Join-Path $OutputRoot ('.' + $ZipName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        # Build a complete replacement before overwriting a previous ZIP.
        $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $Files.GetEnumerator()) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.Value, $file.Key, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $zipPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    foreach ($file in $Files.GetEnumerator()) {
        $target = Join-Path $OutputRoot ([IO.Path]::GetFileName($file.Key))
        if ($file.Value -ine $target) { Copy-Item -LiteralPath $file.Value -Destination $target -Force }
    }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'SHA256SUMS.txt'), "$hash  $ZipName`n", [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ Path = $zipPath; Sha256 = $hash; Size = (Get-Item -LiteralPath $zipPath).Length }
}
