#requires -Version 7.0
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$package = Join-Path $repoRoot 'scripts/package-release.ps1'
$testRoot = Join-Path $repoRoot ('.work/package-tests/' + [Guid]::NewGuid().ToString('N'))
$assemblyRoot = Join-Path $testRoot 'assembly'
[IO.Directory]::CreateDirectory($assemblyRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
. (Join-Path $repoRoot 'scripts/project-version.ps1')
$version = Get-AstralProjectVersion -Root $repoRoot
$tag = "v$version"
$zipName = "AstralPartyChatPlugin-$tag.zip"
$localDotnet = Join-Path $repoRoot '.work/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source }
[IO.File]::WriteAllText((Join-Path $assemblyRoot 'Fixture.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net6.0</TargetFramework><AssemblyName>AstralPartyChatPlugin</AssemblyName><Version>$version</Version><IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>", $utf8)
[IO.File]::WriteAllText((Join-Path $assemblyRoot 'Fixture.cs'), 'public static class Fixture { }', $utf8)
[IO.File]::WriteAllText((Join-Path $assemblyRoot 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>', $utf8)
$dllRoot = Join-Path $testRoot 'dll'
$output = @(& $dotnet build (Join-Path $assemblyRoot 'Fixture.csproj') --configuration Release --output $dllRoot --nologo 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Package fixture build failed: $($output -join "`n")" }
$dll = Join-Path $dllRoot 'AstralPartyChatPlugin.dll'
$assets = Join-Path $testRoot 'assets'
& $package -DllPath $dll -OutputRoot $assets | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $assets $zipName))
try {
    if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne 'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll') {
        throw 'Distribution ZIP must contain only the plugin DLL at the installation path.'
    }
    $extracted = Join-Path $testRoot 'extracted'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.Entries[0], $extracted)
    if ((Get-FileHash -LiteralPath $dll).Hash -cne (Get-FileHash -LiteralPath $extracted).Hash) {
        throw 'Packaged DLL differs from the built DLL.'
    }
} finally { $archive.Dispose() }
if (@(Get-ChildItem -LiteralPath $assets).Count -ne 3) { throw 'Unexpected release assets.' }
if ((Get-FileHash -LiteralPath $dll).Hash -cne (Get-FileHash -LiteralPath (Join-Path $assets 'AstralPartyChatPlugin.dll')).Hash) { throw 'Standalone DLL differs.' }
$checksums = [IO.File]::ReadAllLines((Join-Path $assets 'SHA256SUMS.txt'))
if ($checksums.Count -ne 2) { throw 'Release manifest must include the ZIP and standalone DLL.' }
foreach ($line in $checksums) {
    $parts = $line -split '  ', 2
    if ($parts[1] -cnotin @('AstralPartyChatPlugin.dll', $zipName)) { throw 'Unexpected release checksum entry.' }
    if ((Get-FileHash -LiteralPath (Join-Path $assets $parts[1])).Hash.ToLowerInvariant() -cne $parts[0]) { throw 'Release checksum mismatch.' }
}
# Repackaging replaces the previous ZIP rather than retaining obsolete entries.
& $package -Tag $tag -DllPath $dll -OutputRoot $assets | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $assets $zipName))
try { if ($archive.Entries.Count -ne 1) { throw 'Repeated packaging added files.' } } finally { $archive.Dispose() }
function Assert-Rejected([hashtable]$Arguments, [string]$Message, [string]$Script = $package) {
    $failed = $false
    try { & $Script @Arguments | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'Invalid release package was accepted.' }
}
$mismatch = Join-Path $testRoot 'mismatch'
$otherTag = 'v' + (Get-AstralNextVersion -Version $version -Bump patch)
Assert-Rejected @{ Tag = $otherTag; DllPath = $dll; OutputRoot = $mismatch } 'must match the built DLL version'
if (Test-Path -LiteralPath $mismatch) { throw 'Mismatched packaging created output.' }
Assert-Rejected @{ Tag = 'v01.0.0'; DllPath = $dll; OutputRoot = $mismatch } 'canonical vX.Y.Z'
Assert-Rejected @{ DllPath = (Join-Path $testRoot 'missing.dll'); OutputRoot = $mismatch } 'Missing plugin DLL'
[IO.File]::WriteAllText((Join-Path $assets 'unrelated.txt'), 'preserve', $utf8)
$before = (Get-FileHash -LiteralPath (Join-Path $assets $zipName)).Hash
Assert-Rejected @{ DllPath = $dll; OutputRoot = $assets } 'unexpected file'
if ((Get-FileHash -LiteralPath (Join-Path $assets $zipName)).Hash -cne $before) { throw 'Rejected packaging changed the existing ZIP.' }

# Isolate the package command from game references. The build collaborator records
# calls and supplies the managed fixture DLL; a real plugin build is checked locally.
$automaticRoot = Join-Path $testRoot 'automatic'
$automaticScripts = Join-Path $automaticRoot 'scripts'
[IO.Directory]::CreateDirectory($automaticScripts) | Out-Null
Copy-Item -LiteralPath $package, (Join-Path $repoRoot 'scripts/project-version.ps1') -Destination $automaticScripts
[IO.File]::WriteAllText((Join-Path $automaticRoot 'VERSION'), $version, $utf8)
Copy-Item -LiteralPath $dll -Destination (Join-Path $automaticRoot 'build-input.dll')
$buildStub = @'
param([string]$GameRoot = '', [string]$RefsRoot = '')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$call = @{ GameRoot = $GameRoot; RefsRoot = $RefsRoot } | ConvertTo-Json -Compress
[IO.File]::AppendAllText((Join-Path $root 'build-calls.jsonl'), $call + [Environment]::NewLine)
if (Test-Path -LiteralPath (Join-Path $root 'fail-build')) { throw 'Fixture build failed.' }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'build-input.dll') -Destination (Join-Path $dist 'AstralPartyChatPlugin.dll') -Force
'@
[IO.File]::WriteAllText((Join-Path $automaticScripts 'build.ps1'), $buildStub, $utf8)
$automaticPackage = Join-Path $automaticScripts 'package-release.ps1'
$automaticAssets = Join-Path $automaticRoot "dist/release/$tag"
$automaticDll = Join-Path $automaticRoot 'dist/AstralPartyChatPlugin.dll'
$callsPath = Join-Path $automaticRoot 'build-calls.jsonl'
$expectedHash = (Get-FileHash -LiteralPath $dll).Hash
function Assert-AutomaticPackage {
    if ((Get-FileHash -LiteralPath (Join-Path $automaticAssets 'AstralPartyChatPlugin.dll')).Hash -cne $expectedHash) {
        throw 'Automatic packaging used an outdated DLL.'
    }
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $automaticAssets $zipName))
    try {
        $file = Join-Path $automaticRoot 'extracted.dll'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.Entries[0], $file, $true)
        if ((Get-FileHash -LiteralPath $file).Hash -cne $expectedHash) { throw 'Automatic ZIP used an outdated DLL.' }
    } finally { $zip.Dispose() }
}
& $automaticPackage | Out-Null
Assert-AutomaticPackage
if ([IO.File]::ReadAllLines($callsPath).Count -ne 1) { throw 'Packaging did not build exactly once.' }
[IO.File]::WriteAllText($automaticDll, 'stale DLL', $utf8)
& $automaticPackage | Out-Null
Assert-AutomaticPackage
if ([IO.File]::ReadAllLines($callsPath).Count -ne 2) { throw 'Packaging reused an existing DLL without rebuilding.' }
& $automaticPackage -DllPath $dll -OutputRoot (Join-Path $automaticRoot 'explicit') | Out-Null
if ([IO.File]::ReadAllLines($callsPath).Count -ne 2) { throw 'Explicit DLL packaging unexpectedly built the plugin.' }
$gameRoot = 'D:\Steam Library\Astral Party\8vJXnINT'
$refsRoot = 'D:\Build References\BepInEx'
& $automaticPackage -GameRoot $gameRoot -RefsRoot $refsRoot | Out-Null
$calls = @([IO.File]::ReadAllLines($callsPath) | ForEach-Object { $_ | ConvertFrom-Json })
if ($calls.Count -ne 3 -or $calls[-1].GameRoot -cne $gameRoot -or $calls[-1].RefsRoot -cne $refsRoot) {
    throw 'Packaging did not forward game and reference paths to the build.'
}
Assert-Rejected @{ Tag = $otherTag } 'must match VERSION when building' $automaticPackage
Assert-Rejected @{ OutputRoot = $assets } 'unexpected file' $automaticPackage
Assert-Rejected @{ DllPath = '' } 'DllPath' $automaticPackage
Assert-Rejected @{ DllPath = $dll; RefsRoot = $refsRoot } 'Parameter set' $automaticPackage
if ([IO.File]::ReadAllLines($callsPath).Count -ne 3) { throw 'Invalid package settings triggered a build.' }
$assetHashes = @(Get-ChildItem -LiteralPath $automaticAssets | Sort-Object Name | Get-FileHash | Select-Object -ExpandProperty Hash)
[IO.File]::WriteAllText((Join-Path $automaticRoot 'fail-build'), '', $utf8)
Assert-Rejected @{} 'Fixture build failed' $automaticPackage
$afterHashes = @(Get-ChildItem -LiteralPath $automaticAssets | Sort-Object Name | Get-FileHash | Select-Object -ExpandProperty Hash)
if (($assetHashes -join ',') -cne ($afterHashes -join ',')) { throw 'A failed build changed existing release assets.' }
$failedAssets = Join-Path $automaticRoot 'failed-assets'
Assert-Rejected @{ OutputRoot = $failedAssets } 'Fixture build failed' $automaticPackage
if (Test-Path -LiteralPath $failedAssets) { throw 'A failed build packaged an old DLL.' }
Write-Output 'Release package tests passed: automatic fresh builds, explicit DLL reuse, build path forwarding, failure handling, ZIP contents, checksums and rejection cases.'
$global:LASTEXITCODE = 0
