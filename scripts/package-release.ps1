param(
    [string]$Tag = '',
    [string]$OutputRoot = '',
    [string]$DllPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'project-version.ps1')
if (-not $Tag) { $Tag = 'v' + (Get-AstralProjectVersion -Root $repoRoot) }
$version = ConvertTo-AstralVersion $Tag -Tag

$dll = if ($DllPath) { [IO.Path]::GetFullPath($DllPath) } else { Join-Path $repoRoot 'dist\AstralPartyChatPlugin.dll' }
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Run scripts/build.ps1 before packaging.' }
if ([Reflection.AssemblyName]::GetAssemblyName($dll).Name -cne 'AstralPartyChatPlugin') { throw 'Unexpected plugin assembly.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -cne $version) {
    throw 'Package tag must match the built DLL version. Prepare the VERSION file and rebuild before packaging.'
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

$checksums = foreach ($name in @('AstralPartyChatPlugin.dll', $zipName)) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutputRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Write-Output "Release package ready: $Tag (plugin DLL, ZIP, SHA256SUMS.txt)"
