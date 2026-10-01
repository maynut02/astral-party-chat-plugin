param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'reference-files.ps1')

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $GameRoot 'BepInEx'
$targetRoot = Join-Path $repoRoot '.work\refs'

$requiredCore = @(
    Get-AstralReferencePaths | Where-Object { $_.StartsWith('core/') } | ForEach-Object { $_.Substring(5) }
)

$requiredInterop = @(
    Get-AstralReferencePaths | Where-Object { $_.StartsWith('interop/') } | ForEach-Object { $_.Substring(8) }
)

$coreOut = Join-Path $targetRoot 'core'
$interopOut = Join-Path $targetRoot 'interop'
# Check the entire set before overwriting any references.
foreach ($group in @(@{ Folder = 'core'; Files = $requiredCore }, @{ Folder = 'interop'; Files = $requiredInterop })) {
    foreach ($file in $group.Files) {
        $source = Join-Path $sourceRoot (Join-Path $group.Folder $file)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing build reference: $source" }
    }
}
New-Item -ItemType Directory -Force -Path $coreOut, $interopOut | Out-Null

foreach ($file in $requiredCore) {
    $source = Join-Path $sourceRoot "core\$file"
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing BepInEx dependency: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $coreOut $file) -Force
}

foreach ($file in $requiredInterop) {
    $source = Join-Path $sourceRoot "interop\$file"
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing game interop dependency: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $interopOut $file) -Force
}

$versions = foreach ($group in @(@{ Folder = 'core'; Files = $requiredCore }, @{ Folder = 'interop'; Files = $requiredInterop })) {
    foreach ($file in $group.Files) {
        $copied = Join-Path $targetRoot (Join-Path $group.Folder $file)
        $assembly = [Reflection.AssemblyName]::GetAssemblyName($copied)
        [ordered]@{
            file = $group.Folder + '/' + $file
            assemblyVersion = $assembly.Version.ToString()
            fileVersion = (Get-Item -LiteralPath $copied).VersionInfo.FileVersion
            sha256 = (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash
        }
    }
}
[ordered]@{ copiedAt = [DateTime]::UtcNow.ToString('o'); references = @($versions) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $targetRoot 'versions.json') -Encoding UTF8
Write-Output "refs=$targetRoot"
