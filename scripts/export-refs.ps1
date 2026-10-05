param(
    [string]$RefsRoot = '',
    [string]$OutputPath = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'reference-files.ps1')
Add-Type -AssemblyName System.IO.Compression

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($RefsRoot)) { $RefsRoot = Join-Path $repoRoot '.work/refs' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repoRoot '.work/astral-build-refs.zip' }
$RefsRoot = [IO.Path]::GetFullPath($RefsRoot)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$referencePaths = @(Get-AstralReferencePaths)
$temporaryPath = $null
$temporaryOwned = $false
$archive = $null
$outputStream = $null

try {
    if (Test-Path -LiteralPath $OutputPath) {
        $existing = Get-Item -LiteralPath $OutputPath -Force
        if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'OutputPath must be a regular file path.'
        }
        if (-not $Force) { throw 'Output ZIP already exists. Use -Force to replace it.' }
    }

    # Check the full set before creating an output file. Read handles below also
    # exclude writers while each assembly is validated and archived.
    foreach ($relativePath in $referencePaths) {
        if (-not (Test-Path -LiteralPath (Join-Path $RefsRoot $relativePath) -PathType Leaf)) {
            throw "Missing build reference: $relativePath"
        }
    }

    $outputDirectory = [IO.Path]::GetDirectoryName($OutputPath)
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $temporaryPath = Join-Path $outputDirectory ([IO.Path]::GetRandomFileName())
    $outputStream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $temporaryOwned = $true
    $archive = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    [long]$totalLength = 0
    foreach ($relativePath in $referencePaths) {
        $sourceStream = $null
        $entryStream = $null
        try {
            $sourcePath = Join-Path $RefsRoot $relativePath
            $sourceStream = [IO.File]::Open($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            if ($sourceStream.Length -gt 24MB) { throw "Reference exceeds 24 MiB: $relativePath" }
            $totalLength += $sourceStream.Length
            if ($totalLength -gt 64MB) { throw 'References exceed 64 MiB in total.' }
            try { [Reflection.AssemblyName]::GetAssemblyName($sourcePath) | Out-Null }
            catch { throw "Invalid managed DLL: $relativePath" }
            $entry = $archive.CreateEntry($relativePath, [IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $sourceStream.CopyTo($entryStream)
        }
        finally {
            if ($null -ne $entryStream) { $entryStream.Dispose() }
            if ($null -ne $sourceStream) { $sourceStream.Dispose() }
        }
    }
    $archive.Dispose()
    $archive = $null
    $outputStream.Dispose()
    $outputStream = $null
    if ((Get-Item -LiteralPath $temporaryPath -Force).Length -gt 64MB) {
        throw 'Completed reference ZIP exceeds 64 MiB.'
    }
    $sha256 = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()

    # Never delete the old ZIP first: a failed build/replacement preserves it.
    if ($Force -and [IO.File]::Exists($OutputPath)) {
        [IO.File]::Replace($temporaryPath, $OutputPath, [System.Management.Automation.Language.NullString]::Value)
    }
    else { [IO.File]::Move($temporaryPath, $OutputPath) }
    $temporaryOwned = $false
    Write-Output "archive=$OutputPath"
    Write-Output "sha256=$sha256"
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $outputStream) { $outputStream.Dispose() }
    if ($temporaryOwned) { [IO.File]::Delete($temporaryPath) }
}
