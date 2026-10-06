#requires -Version 7.0
param([string]$DotNetPath = '')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$package = Join-Path $repoRoot 'scripts/package-release.ps1'
$testParent = [IO.Path]::GetFullPath((Join-Path $repoRoot '.work/package-tests'))
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
try {
$assemblyRoot = Join-Path $testRoot 'assembly'
[IO.Directory]::CreateDirectory($assemblyRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$passed = 0
function Write-PackagePass([string]$Name) {
    $script:passed++
    Write-Output "PASS package: $Name"
}
. (Join-Path $repoRoot 'scripts/project-version.ps1')
$version = Get-AstralProjectVersion -Root $repoRoot
$tag = "v$version"
$zipName = "AstralPartyChatPlugin-$tag.zip"
. (Join-Path $repoRoot 'scripts/dotnet-sdk.ps1')
$dotnet = Get-AstralDotnet -Root $repoRoot -WorkRoot (Join-Path $repoRoot '.work') -DotNetPath $DotNetPath
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
if ($checksums.Count -ne 1) { throw 'Release manifest must include only the uploaded ZIP.' }
foreach ($line in $checksums) {
    $parts = $line -split '  ', 2
    if ($parts[1] -cne $zipName) { throw 'Unexpected release checksum entry.' }
    if ((Get-FileHash -LiteralPath (Join-Path $assets $parts[1])).Hash.ToLowerInvariant() -cne $parts[0]) { throw 'Release checksum mismatch.' }
}
Write-PackagePass 'explicit DLL defaults to VERSION with exact ZIP, standalone DLL and checksum assets'
# Repackaging replaces the previous ZIP rather than retaining obsolete entries.
$archive = [IO.Compression.ZipFile]::Open((Join-Path $assets $zipName), [IO.Compression.ZipArchiveMode]::Update)
try { $archive.CreateEntry('obsolete.txt') | Out-Null } finally { $archive.Dispose() }
& $package -Tag $tag -DllPath $dll -OutputRoot $assets | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $assets $zipName))
try { if ($archive.Entries.Count -ne 1) { throw 'Repeated packaging added files.' } } finally { $archive.Dispose() }
Write-PackagePass 'repeated explicit packaging replaces obsolete ZIP entries'
& $package -DllPath (Join-Path $assets 'AstralPartyChatPlugin.dll') -OutputRoot $assets | Out-Null
if ((Get-FileHash -LiteralPath (Join-Path $assets 'AstralPartyChatPlugin.dll')).Hash -cne (Get-FileHash -LiteralPath $dll).Hash) {
    throw 'Packaging a DLL already in the output directory changed its bytes.'
}
Write-PackagePass 'explicit DLL already in the output directory packages successfully'
$renamedDll = Join-Path $testRoot 'renamed-input.dll'
Copy-Item -LiteralPath $dll -Destination $renamedDll
$renamedAssets = Join-Path $testRoot 'renamed-assets'
& $package -DllPath $renamedDll -OutputRoot $renamedAssets | Out-Null
$renamedNames = @(Get-ChildItem -LiteralPath $renamedAssets -Force | Sort-Object Name | Select-Object -ExpandProperty Name)
$canonicalNames = @('AstralPartyChatPlugin.dll', $zipName, 'SHA256SUMS.txt') | Sort-Object
if (($renamedNames -join ',') -cne ($canonicalNames -join ',') -or
    (Get-FileHash -LiteralPath (Join-Path $renamedAssets 'AstralPartyChatPlugin.dll')).Hash -cne (Get-FileHash -LiteralPath $dll).Hash) {
    throw 'Renamed input DLL did not produce the canonical standalone plugin DLL and exact release assets.'
}
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $renamedAssets $zipName))
try {
    if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne 'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll') {
        throw 'Renamed input DLL changed the canonical ZIP entry.'
    }
    $renamedExtracted = Join-Path $testRoot 'renamed-extracted.dll'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.Entries[0], $renamedExtracted)
    if ((Get-FileHash -LiteralPath $renamedExtracted).Hash -cne (Get-FileHash -LiteralPath $dll).Hash) { throw 'Renamed input DLL changed packaged bytes.' }
} finally { $archive.Dispose() }
$renamedChecksums = [IO.File]::ReadAllLines((Join-Path $renamedAssets 'SHA256SUMS.txt'))
$renamedZipHash = (Get-FileHash -LiteralPath (Join-Path $renamedAssets $zipName)).Hash.ToLowerInvariant()
if ($renamedChecksums.Count -ne 1 -or $renamedChecksums[0] -cne "$renamedZipHash  $zipName") { throw 'Renamed input ZIP checksum differs.' }
Write-PackagePass 'renamed valid input DLL produces only canonical filenames, ZIP entries and checksums'
function Assert-Rejected([hashtable]$Arguments, [string]$Message, [string]$Script = $package, [string]$ErrorId = '') {
    $failed = $false
    try { & $Script @Arguments | Out-Null } catch {
        if ($ErrorId) {
            if ($_.FullyQualifiedErrorId -notlike $ErrorId) { throw }
        } elseif ($_.Exception.Message -notlike "*$Message*") { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'Invalid release package was accepted.' }
    Write-PackagePass ("rejected: " + $(if ($ErrorId) { $ErrorId } else { $Message }))
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

# Managed fixtures exercise assembly identity/version rejection without game DLLs.
function New-RejectedAssemblyFixture([string]$Name, [string]$FixtureVersion, [string]$Directory) {
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $Directory 'Fixture.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net6.0</TargetFramework><AssemblyName>$Name</AssemblyName><Version>$FixtureVersion</Version><IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>", $utf8)
    [IO.File]::WriteAllText((Join-Path $Directory 'Fixture.cs'), 'public static class Fixture { }', $utf8)
    [IO.File]::WriteAllText((Join-Path $Directory 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>', $utf8)
    $outputRoot = Join-Path $Directory 'bin'
    $result = @(& $dotnet build (Join-Path $Directory 'Fixture.csproj') --configuration Release --output $outputRoot --nologo 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Rejected assembly fixture build failed: $($result -join "`n")" }
    return Join-Path $outputRoot "$Name.dll"
}
$wrongIdentity = New-RejectedAssemblyFixture 'UnrelatedPlugin' $version (Join-Path $testRoot 'wrong-identity')
$wrongVersion = New-RejectedAssemblyFixture 'AstralPartyChatPlugin' ($otherTag.Substring(1)) (Join-Path $testRoot 'wrong-version')
Assert-Rejected @{ DllPath = $wrongIdentity; OutputRoot = $mismatch } 'Unexpected plugin assembly'
Assert-Rejected @{ DllPath = $wrongVersion; OutputRoot = $mismatch } 'must match the built DLL version'
if (Test-Path -LiteralPath $mismatch) { throw 'Rejected DLL created release output.' }

# Isolate the package command from game references. The build collaborator records
# calls and supplies the managed fixture DLL; a real plugin build is checked locally.
$automaticRoot = Join-Path $testRoot 'automatic'
$automaticScripts = Join-Path $automaticRoot 'scripts'
[IO.Directory]::CreateDirectory($automaticScripts) | Out-Null
Copy-Item -LiteralPath $package, (Join-Path $repoRoot 'scripts/project-version.ps1'), (Join-Path $repoRoot 'scripts/package-files.ps1') -Destination $automaticScripts
[IO.File]::WriteAllText((Join-Path $automaticRoot 'VERSION'), $version, $utf8)
Copy-Item -LiteralPath $dll -Destination (Join-Path $automaticRoot 'build-input.dll')
$buildStub = @'
param([string]$GameRoot = '', [string]$RefsRoot = '', [string]$WorkRoot = '', [string]$DotNetPath = '')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$call = @{ GameRoot = $GameRoot; RefsRoot = $RefsRoot; WorkRoot = $WorkRoot; DotNetPath = $DotNetPath } | ConvertTo-Json -Compress
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
    $names = @(Get-ChildItem -LiteralPath $automaticAssets -Force | Sort-Object Name | Select-Object -ExpandProperty Name)
    $expected = @('AstralPartyChatPlugin.dll', $zipName, 'SHA256SUMS.txt') | Sort-Object
    if (($names -join ',') -cne ($expected -join ',')) { throw 'Automatic packaging produced unexpected release assets.' }
    if ((Get-FileHash -LiteralPath (Join-Path $automaticAssets 'AstralPartyChatPlugin.dll')).Hash -cne $expectedHash) {
        throw 'Automatic packaging used an outdated DLL.'
    }
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $automaticAssets $zipName))
    try {
        if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -cne 'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll') {
            throw 'Automatic ZIP has unexpected contents.'
        }
        $file = Join-Path $automaticRoot 'extracted.dll'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.Entries[0], $file, $true)
        if ((Get-FileHash -LiteralPath $file).Hash -cne $expectedHash) { throw 'Automatic ZIP used an outdated DLL.' }
    } finally { $zip.Dispose() }
    $lines = [IO.File]::ReadAllLines((Join-Path $automaticAssets 'SHA256SUMS.txt'))
    $zipHash = (Get-FileHash -LiteralPath (Join-Path $automaticAssets $zipName)).Hash.ToLowerInvariant()
    if ($lines.Count -ne 1 -or $lines[0] -cne "$zipHash  $zipName") { throw 'Automatic ZIP checksum mismatch.' }
}
& $automaticPackage | Out-Null
Assert-AutomaticPackage
if ([IO.File]::ReadAllLines($callsPath).Count -ne 1) { throw 'Packaging did not build exactly once.' }
Write-PackagePass 'default packaging builds exactly once and produces fresh assets'
[IO.File]::WriteAllText($automaticDll, 'stale DLL', $utf8)
& $automaticPackage | Out-Null
Assert-AutomaticPackage
if ([IO.File]::ReadAllLines($callsPath).Count -ne 2) { throw 'Packaging reused an existing DLL without rebuilding.' }
Write-PackagePass 'repeated default packaging rebuilds instead of reusing a stale DLL'
& $automaticPackage -DllPath $dll -OutputRoot (Join-Path $automaticRoot 'explicit') | Out-Null
if ([IO.File]::ReadAllLines($callsPath).Count -ne 2) { throw 'Explicit DLL packaging unexpectedly built the plugin.' }
Write-PackagePass 'explicit DLL packaging bypasses the build'
$gameRoot = 'D:\Steam Library\Astral Party\8vJXnINT'
$refsRoot = 'D:\Build References\BepInEx'
$workRoot = 'D:\Package Work\SDK and References'
$buildDotnet = 'D:\Dotnet SDK\dotnet.exe'
& $automaticPackage -GameRoot $gameRoot -RefsRoot $refsRoot -WorkRoot $workRoot -DotNetPath $buildDotnet | Out-Null
$calls = @([IO.File]::ReadAllLines($callsPath) | ForEach-Object { $_ | ConvertFrom-Json })
if ($calls.Count -ne 3 -or $calls[-1].GameRoot -cne $gameRoot -or $calls[-1].RefsRoot -cne $refsRoot -or
    $calls[-1].WorkRoot -cne $workRoot -or $calls[-1].DotNetPath -cne $buildDotnet) {
    throw 'Packaging did not forward game, reference, work and SDK paths to the build.'
}
Assert-AutomaticPackage
Write-PackagePass 'all four build paths are forwarded intact, including spaces'
Assert-Rejected @{ Tag = $otherTag } 'must match VERSION when building' $automaticPackage
foreach ($invalidTag in @('v01.0.0', '1.0.0', 'v1.0.0-rc.1')) {
    Assert-Rejected @{ Tag = $invalidTag } 'canonical vX.Y.Z' $automaticPackage
}
Assert-Rejected @{ OutputRoot = $assets } 'unexpected file' $automaticPackage
Assert-Rejected @{ DllPath = '' } 'DllPath' $automaticPackage
Assert-Rejected @{ DllPath = $dll; RefsRoot = $refsRoot } '' $automaticPackage 'AmbiguousParameterSet*'
if ([IO.File]::ReadAllLines($callsPath).Count -ne 3) { throw 'Invalid package settings triggered a build.' }
$assetHashes = @(Get-ChildItem -LiteralPath $automaticAssets | Sort-Object Name | Get-FileHash | Select-Object -ExpandProperty Hash)
[IO.File]::WriteAllText((Join-Path $automaticRoot 'fail-build'), '', $utf8)
Assert-Rejected @{} 'Fixture build failed' $automaticPackage
$afterHashes = @(Get-ChildItem -LiteralPath $automaticAssets | Sort-Object Name | Get-FileHash | Select-Object -ExpandProperty Hash)
if (($assetHashes -join ',') -cne ($afterHashes -join ',')) { throw 'A failed build changed existing release assets.' }
Write-PackagePass 'failed default build preserves every existing release asset'
$failedAssets = Join-Path $automaticRoot 'failed-assets'
Assert-Rejected @{ OutputRoot = $failedAssets } 'Fixture build failed' $automaticPackage
if (Test-Path -LiteralPath $failedAssets) { throw 'A failed build packaged an old DLL.' }
Write-PackagePass 'failed build creates no new package from an old DLL'

# A source can disappear after preflight. A failed replacement must retain the
# old ZIP and checksum and remove its partial temporary archive.
. (Join-Path $repoRoot 'scripts/package-files.ps1')
$atomicAssets = Join-Path $testRoot 'atomic-assets'
& $package -DllPath $dll -OutputRoot $atomicAssets | Out-Null
$atomicBefore = @(Get-ChildItem -LiteralPath $atomicAssets -Force | Sort-Object Name | Get-FileHash | Select-Object -ExpandProperty Hash)
$sourceFiles = [ordered]@{
    'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll' = $dll
    'missing.dll' = (Join-Path $testRoot 'missing-source.dll')
}
$failed = $false
try { New-AstralPluginPackage -Files $sourceFiles -OutputRoot $atomicAssets -ZipName $zipName | Out-Null } catch [IO.FileNotFoundException] { $failed = $true }
if (-not $failed) { throw 'Missing archive source was accepted.' }
$atomicAfter = @(Get-ChildItem -LiteralPath $atomicAssets -Force | Sort-Object Name | Get-FileHash | Select-Object -ExpandProperty Hash)
if (($atomicBefore -join ',') -cne ($atomicAfter -join ',') -or @(Get-ChildItem -LiteralPath $atomicAssets -Force).Count -ne 3) {
    throw 'Failed ZIP replacement changed old assets or leaked a temporary archive.'
}
Write-PackagePass 'missing ZIP source preserves the previous package and cleans its temporary archive'
Write-Output "$passed chat release package checks passed. No game or external service was used."
$global:LASTEXITCODE = 0
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        $cleanupRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $testRoot).ProviderPath)
        $cleanupParent = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $testParent).ProviderPath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $cleanupRoot.StartsWith($cleanupParent, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($cleanupRoot) -cnotmatch '\A[a-f0-9]{32}\z') {
            throw 'Refusing to clean a package fixture outside the unique test directory.'
        }
        Remove-Item -LiteralPath $cleanupRoot -Recurse -Force
    }
}
