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
$zipName = "AstralParty.Chat-$tag.zip"
$localDotnet = Join-Path $repoRoot '.work/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source }
[IO.File]::WriteAllText((Join-Path $assemblyRoot 'Fixture.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net6.0</TargetFramework><AssemblyName>AstralParty.Chat</AssemblyName><Version>$version</Version><IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>", $utf8)
[IO.File]::WriteAllText((Join-Path $assemblyRoot 'Fixture.cs'), 'public static class Fixture { }', $utf8)
[IO.File]::WriteAllText((Join-Path $assemblyRoot 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>', $utf8)
$dllRoot = Join-Path $testRoot 'dll'
$output = @(& $dotnet build (Join-Path $assemblyRoot 'Fixture.csproj') --configuration Release --output $dllRoot --nologo 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Package fixture build failed: $($output -join "`n")" }
$dll = Join-Path $dllRoot 'AstralParty.Chat.dll'
$assets = Join-Path $testRoot 'assets'
& $package -DllPath $dll -OutputRoot $assets | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $assets $zipName))
try {
    if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne 'BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll') {
        throw 'Distribution ZIP must contain only the plugin DLL at the installation path.'
    }
    $extracted = Join-Path $testRoot 'extracted'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.Entries[0], $extracted)
    if ((Get-FileHash -LiteralPath $dll).Hash -cne (Get-FileHash -LiteralPath $extracted).Hash) {
        throw 'Packaged DLL differs from the built DLL.'
    }
} finally { $archive.Dispose() }
if (@(Get-ChildItem -LiteralPath $assets).Count -ne 3) { throw 'Unexpected release assets.' }
if ((Get-FileHash -LiteralPath $dll).Hash -cne (Get-FileHash -LiteralPath (Join-Path $assets 'AstralParty.Chat.dll')).Hash) { throw 'Standalone DLL differs.' }
$checksums = [IO.File]::ReadAllLines((Join-Path $assets 'SHA256SUMS.txt'))
if ($checksums.Count -ne 2) { throw 'Release manifest must include the ZIP and standalone DLL.' }
foreach ($line in $checksums) {
    $parts = $line -split '  ', 2
    if ($parts[1] -cnotin @('AstralParty.Chat.dll', $zipName)) { throw 'Unexpected release checksum entry.' }
    if ((Get-FileHash -LiteralPath (Join-Path $assets $parts[1])).Hash.ToLowerInvariant() -cne $parts[0]) { throw 'Release checksum mismatch.' }
}
# Repackaging replaces the previous ZIP rather than retaining obsolete entries.
& $package -Tag $tag -DllPath $dll -OutputRoot $assets | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $assets $zipName))
try { if ($archive.Entries.Count -ne 1) { throw 'Repeated packaging added files.' } } finally { $archive.Dispose() }
function Assert-Rejected([hashtable]$Arguments, [string]$Message) {
    $failed = $false
    try { & $package @Arguments | Out-Null } catch {
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
Assert-Rejected @{ DllPath = (Join-Path $testRoot 'missing.dll'); OutputRoot = $mismatch } 'before packaging'
[IO.File]::WriteAllText((Join-Path $assets 'unrelated.txt'), 'preserve', $utf8)
$before = (Get-FileHash -LiteralPath (Join-Path $assets $zipName)).Hash
Assert-Rejected @{ DllPath = $dll; OutputRoot = $assets } 'unexpected file'
if ((Get-FileHash -LiteralPath (Join-Path $assets $zipName)).Hash -cne $before) { throw 'Rejected packaging changed the existing ZIP.' }
Write-Output 'Release package tests passed: ZIP contents, DLL identity, checksums, repeat packaging and rejection cases.'
$global:LASTEXITCODE = 0
