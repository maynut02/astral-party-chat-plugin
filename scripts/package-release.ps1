[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [string]$Tag = '',
    [string]$OutputRoot = '',
    [Parameter(Mandatory = $true, ParameterSetName = 'ExistingDll')]
    [ValidateNotNullOrEmpty()]
    [string]$DllPath,
    [Parameter(ParameterSetName = 'Build')]
    [string]$GameRoot = '',
    [Parameter(ParameterSetName = 'Build')]
    [string]$RefsRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'project-version.ps1')
if (-not $Tag) { $Tag = 'v' + (Get-AstralProjectVersion -Root $repoRoot) }
$version = ConvertTo-AstralVersion $Tag -Tag
$buildPlugin = $PSCmdlet.ParameterSetName -eq 'Build'
if ($buildPlugin -and $version -cne (Get-AstralProjectVersion -Root $repoRoot)) {
    throw 'Package tag must match VERSION when building. Prepare the VERSION file before packaging.'
}
if (-not $OutputRoot) { $OutputRoot = Join-Path (Join-Path $repoRoot 'dist\release') $Tag }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$zipName = "AstralPartyChatPlugin-$Tag.zip"
$allowed = @('AstralPartyChatPlugin.dll', $zipName, 'SHA256SUMS.txt')
if (Test-Path -LiteralPath $OutputRoot) {
    foreach ($entry in Get-ChildItem -LiteralPath $OutputRoot -Force) {
        if ($entry.PSIsContainer -or $entry.Name -cnotin $allowed) {
            throw 'Release output directory contains an unexpected file. Use a separate empty directory.'
        }
    }
}

if ($buildPlugin) {
    $buildArguments = @{}
    if ($GameRoot) { $buildArguments.GameRoot = $GameRoot }
    if ($RefsRoot) { $buildArguments.RefsRoot = $RefsRoot }
    & (Join-Path $PSScriptRoot 'build.ps1') @buildArguments
    $dll = Join-Path $repoRoot 'dist\AstralPartyChatPlugin.dll'
}
else { $dll = [IO.Path]::GetFullPath($DllPath) }
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Missing plugin DLL: $dll" }
if ([Reflection.AssemblyName]::GetAssemblyName($dll).Name -cne 'AstralPartyChatPlugin') { throw 'Unexpected plugin assembly.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -cne $version) {
    throw 'Package tag must match the built DLL version. Prepare the VERSION file and rebuild before packaging.'
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $OutputRoot 'AstralPartyChatPlugin.dll') -Force

Add-Type -AssemblyName System.IO.Compression
$zipPath = Join-Path $OutputRoot $zipName
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $entry = $archive.CreateEntry('BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll')
    $destination = $entry.Open()
    $source = [IO.File]::OpenRead($dll)
    try { $source.CopyTo($destination) }
    finally { $source.Dispose(); $destination.Dispose() }
}
finally { $archive.Dispose(); $stream.Dispose() }

$checksums = foreach ($name in @($zipName)) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutputRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Write-Output "Release package ready: $Tag (plugin DLL, ZIP, SHA256SUMS.txt)"
