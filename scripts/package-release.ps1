param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$OutputRoot = '',
    [string]$DllPath = '',
    [string]$InstallerScriptPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolved = & (Join-Path $PSScriptRoot 'release-version.ps1') -InitialVersion '0.0.0' -ExplicitTag $Tag
$version = $resolved.Version

$dll = if ($DllPath) { [IO.Path]::GetFullPath($DllPath) } else { Join-Path $repoRoot 'dist\AstralParty.Chat.dll' }
if (-not $InstallerScriptPath) { $InstallerScriptPath = Join-Path $PSScriptRoot 'install.ps1' }
$InstallerScriptPath = [IO.Path]::GetFullPath($InstallerScriptPath)
. (Join-Path ([IO.Path]::GetDirectoryName($InstallerScriptPath)) 'distribution-installer.ps1')
$installerText = Get-AstralDistributionInstaller -InstallerScriptPath $InstallerScriptPath
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Run scripts/build.ps1 before packaging.' }
if ([Reflection.AssemblyName]::GetAssemblyName($dll).Name -cne 'AstralParty.Chat') { throw 'Unexpected plugin assembly.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -cne $version) {
    throw 'Package tag must match the built DLL version. Update PluginVersion and rebuild before packaging.'
}
if (-not $OutputRoot) { $OutputRoot = Join-Path (Join-Path $repoRoot 'dist\release') $Tag }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$zipName = "AstralParty.Chat-$Tag.zip"
$allowed = @('AstralParty.Chat.dll', $zipName, 'SHA256SUMS.txt')
if (Test-Path -LiteralPath $OutputRoot) {
    foreach ($entry in Get-ChildItem -LiteralPath $OutputRoot -Force) {
        if ($entry.PSIsContainer -or $entry.Name -cnotin $allowed) {
            throw 'Release output directory contains an unexpected file. Use a separate empty directory.'
        }
    }
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $OutputRoot 'AstralParty.Chat.dll') -Force

Add-Type -AssemblyName System.IO.Compression
$zipPath = Join-Path $OutputRoot $zipName
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $entry = $archive.CreateEntry('BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll')
    $destination = $entry.Open()
    $source = [IO.File]::OpenRead($dll)
    try { $source.CopyTo($destination) }
    finally { $source.Dispose(); $destination.Dispose() }
    $entry = $archive.CreateEntry('Install.cmd')
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($installerText) }
    finally { $writer.Dispose() }
}
finally { $archive.Dispose(); $stream.Dispose() }

$checksums = foreach ($name in @('AstralParty.Chat.dll', $zipName)) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutputRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Write-Output "Release package ready: $Tag (plugin DLL, install ZIP, SHA256SUMS.txt)"
