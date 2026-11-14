#requires -Version 7.2
param([string]$DotNetPath = '')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testParent = [IO.Path]::GetFullPath((Join-Path $repoRoot '.work/package-tests'))
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
$savedSigningKey = $env:ASTRAL_UPDATE_SIGNING_KEY
try {
$utf8 = [Text.UTF8Encoding]::new($false)
$passed = 0
function Write-PackagePass([string]$Name) {
    $script:passed++
    Write-Output "PASS package: $Name"
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $repoRoot 'scripts/project-version.ps1')
$version = Get-AstralProjectVersion -Root $repoRoot
$tag = "v$version"
$dllEntry = 'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll'
$updateZip = "update-$tag.zip"
$installZip = "AstralPartyChatPlugin-$tag.zip"
$assetNames = @($updateZip, $installZip, 'update.json', 'update.json.sig', 'SHA256SUMS.txt')
$outputNames = @('AstralPartyChatPlugin.dll') + $assetNames | Sort-Object
. (Join-Path $repoRoot 'scripts/dotnet-sdk.ps1')
$dotnet = Get-AstralDotnet -Root $repoRoot -WorkRoot (Join-Path $repoRoot '.work') -DotNetPath $DotNetPath

function New-AssemblyFixture([string]$Name, [string]$FixtureVersion, [string]$Directory) {
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $Directory 'Fixture.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net6.0</TargetFramework><AssemblyName>$Name</AssemblyName><Version>$FixtureVersion</Version><IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>", $utf8)
    [IO.File]::WriteAllText((Join-Path $Directory 'Fixture.cs'), 'public static class Fixture { }', $utf8)
    [IO.File]::WriteAllText((Join-Path $Directory 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>', $utf8)
    $outputRoot = Join-Path $Directory 'bin'
    $result = @(& $dotnet build (Join-Path $Directory 'Fixture.csproj') --configuration Release --output $outputRoot --nologo 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Assembly fixture build failed: $($result -join "`n")" }
    return Join-Path $outputRoot "$Name.dll"
}
$dll = New-AssemblyFixture 'AstralPartyChatPlugin' $version (Join-Path $testRoot 'assembly')
$expectedHash = (Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant()

# The release scripts run from a copy: a throwaway key replaces the trusted one and a small
# archive stands in for the pinned BepInEx download. The build collaborator records its calls.
$fixtureRoot = Join-Path $testRoot 'repository'
$fixtureScripts = Join-Path $fixtureRoot 'scripts'
foreach ($directory in @($fixtureScripts, (Join-Path $fixtureRoot 'installer'), (Join-Path $fixtureRoot 'assets/third-party'))) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/package-release.ps1'), (Join-Path $repoRoot 'scripts/project-version.ps1'), (Join-Path $repoRoot 'scripts/package-files.ps1') -Destination $fixtureScripts
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer/install.bat'), (Join-Path $repoRoot 'installer/install.ps1') -Destination (Join-Path $fixtureRoot 'installer')
Copy-Item -LiteralPath (Join-Path $repoRoot 'assets/third-party/LICENSE-BepInEx.txt') -Destination (Join-Path $fixtureRoot 'assets/third-party')
[IO.File]::WriteAllText((Join-Path $fixtureRoot 'VERSION'), $version, $utf8)
Copy-Item -LiteralPath $dll -Destination (Join-Path $fixtureRoot 'build-input.dll')
$runtimeEntries = @('BepInEx/core/BepInEx.Core.dll', 'dotnet/coreclr.dll', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')
$runtime = Join-Path $testRoot 'bepinex-fixture.zip'
$archive = [IO.Compression.ZipFile]::Open($runtime, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $runtimeEntries) {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open(), $utf8)
        try { $writer.Write("runtime $name") } finally { $writer.Dispose() }
    }
} finally { $archive.Dispose() }
function New-SigningKey([string]$Path) {
    $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        [IO.File]::WriteAllText($Path, [string]::new([Security.Cryptography.PemEncoding]::Write('PRIVATE KEY', $key.ExportPkcs8PrivateKey())), $utf8)
        return [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
    } finally { $key.Dispose() }
}
$keyPath = Join-Path $testRoot 'signing.pem'
$publicKey = New-SigningKey $keyPath
$otherKeyPath = Join-Path $testRoot 'other-signing.pem'
New-SigningKey $otherKeyPath | Out-Null
[IO.File]::AppendAllText((Join-Path $fixtureScripts 'package-files.ps1'),
    "`nfunction Get-AstralUpdatePublicKey { '$publicKey' }`nfunction Get-AstralBepInExArchive { param([string]`$WorkRoot) '$($runtime.Replace("'", "''"))' }`n", $utf8)
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
[IO.File]::WriteAllText((Join-Path $fixtureScripts 'build.ps1'), $buildStub, $utf8)
$package = Join-Path $fixtureScripts 'package-release.ps1'
$callsPath = Join-Path $fixtureRoot 'build-calls.jsonl'
$env:ASTRAL_UPDATE_SIGNING_KEY = $keyPath

function Read-ZipEntries([string]$Path) {
    $entries = @{}
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            $stream = $entry.Open()
            $buffer = [IO.MemoryStream]::new()
            try { $stream.CopyTo($buffer); $entries[$entry.FullName] = $buffer.ToArray() } finally { $buffer.Dispose(); $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    return $entries
}
function Get-BytesHash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Get-AssetFingerprint([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Force | Sort-Object Name | ForEach-Object { "$($_.Name)|$((Get-FileHash -LiteralPath $_.FullName).Hash)" }) -join "`n"
}

# What players and installed plugins receive, checked independently of the packaging code.
function Assert-ReleasePackage([string]$Root, [string]$Repository = 'maynut02/astral-party-chat-plugin') {
    $names = @(Get-ChildItem -LiteralPath $Root -Force | Sort-Object Name | Select-Object -ExpandProperty Name)
    if (($names -join ',') -cne ($outputNames -join ',')) { throw "Unexpected release output: $($names -join ', ')" }
    if ((Get-FileHash -LiteralPath (Join-Path $Root 'AstralPartyChatPlugin.dll')).Hash.ToLowerInvariant() -cne $expectedHash) { throw 'Standalone DLL differs from the built DLL.' }

    $update = Read-ZipEntries (Join-Path $Root $updateZip)
    if ($update.Count -ne 1 -or -not $update.ContainsKey($dllEntry) -or (Get-BytesHash $update[$dllEntry]) -cne $expectedHash) {
        throw 'Update ZIP must contain only the built plugin DLL at its installation path.'
    }
    $install = Read-ZipEntries (Join-Path $Root $installZip)
    $expectedInstall = @($runtimeEntries + @($dllEntry, 'BepInEx/config/BepInEx.cfg', 'LICENSE-BepInEx.txt', 'install.bat', 'install.ps1') | Sort-Object)
    if ((@($install.Keys | Sort-Object) -join ',') -cne ($expectedInstall -join ',')) { throw "Unexpected install ZIP entries: $(@($install.Keys | Sort-Object) -join ', ')" }
    if ((Get-BytesHash $install[$dllEntry]) -cne $expectedHash) { throw 'Install ZIP contains a different plugin DLL.' }
    foreach ($name in $runtimeEntries) {
        if ($utf8.GetString($install[$name]) -cne "runtime $name") { throw "Install ZIP changed BepInEx file: $name" }
    }
    foreach ($name in @('install.bat', 'install.ps1')) {
        if ((Get-BytesHash $install[$name]) -cne (Get-FileHash -LiteralPath (Join-Path $repoRoot "installer/$name")).Hash.ToLowerInvariant()) { throw "Install ZIP changed $name." }
    }
    $config = $utf8.GetString($install['BepInEx/config/BepInEx.cfg'])
    if ($config -notmatch '(?m)^\[Logging\]\r?\nUnityLogListening = false\r?$' -or $config -notmatch '(?m)^\[Logging\.Console\]\r?\nEnabled = false\r?$') {
        throw 'Install ZIP does not disable Unity log listening and the console for a fresh BepInEx.'
    }

    $expectedChecksums = foreach ($name in @($updateZip, $installZip, 'update.json')) {
        "$((Get-FileHash -LiteralPath (Join-Path $Root $name)).Hash.ToLowerInvariant())  $name"
    }
    if (([IO.File]::ReadAllLines((Join-Path $Root 'SHA256SUMS.txt')) -join "`n") -cne ($expectedChecksums -join "`n")) { throw 'SHA256SUMS.txt does not list exactly both ZIPs and update.json.' }

    $manifest = [IO.File]::ReadAllText((Join-Path $Root 'update.json')) | ConvertFrom-Json
    if ($manifest.package.url -cne "https://github.com/$Repository/releases/download/$tag/$updateZip") { throw 'Update manifest points at the wrong download.' }
}

# The plugin's own verifier is the authority on whether a release can be installed.
function Invoke-PluginVerifier([string]$Root, [string]$Key = $publicKey) {
    $output = @(& $dotnet run --project (Join-Path $repoRoot 'tests/update/UpdateTests.csproj') --configuration Release -- verify $Root $tag $Key 2>&1)
    return [pscustomobject]@{ Accepted = ($LASTEXITCODE -eq 0); Output = ($output -join "`n") }
}

function Assert-Rejected([hashtable]$Arguments, [string]$Message, [string]$ErrorId = '') {
    $failed = $false
    try { & $package @Arguments | Out-Null } catch {
        if ($ErrorId) {
            if ($_.FullyQualifiedErrorId -notlike $ErrorId) { throw }
        } elseif ($_.Exception.Message -notlike "*$Message*") { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'Invalid release package was accepted.' }
    Write-PackagePass ("rejected: " + $(if ($ErrorId) { $ErrorId } else { $Message }))
}

$assets = Join-Path $testRoot 'assets'
& $package -DllPath $dll -OutputRoot $assets | Out-Null
Assert-ReleasePackage $assets
Write-PackagePass 'explicit DLL defaults to VERSION with install ZIP, update ZIP, manifest, signature and checksums'

$verified = Invoke-PluginVerifier $assets
if (-not $verified.Accepted -or $verified.Output -notmatch "(?m)^VERIFIED $expectedHash\s*$") { throw "The plugin rejected a packaged release: $($verified.Output)" }
Write-PackagePass 'the plugin verifier accepts the packaged release and extracts the built DLL'
if ((Invoke-PluginVerifier $assets (New-SigningKey (Join-Path $testRoot 'untrusted.pem'))).Accepted) { throw 'The plugin accepted a release signed by an untrusted key.' }
Write-PackagePass 'the plugin verifier rejects the same release under another trust key'

# Repackaging replaces the previous ZIPs rather than retaining obsolete entries.
foreach ($name in @($updateZip, $installZip)) {
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $assets $name), [IO.Compression.ZipArchiveMode]::Update)
    try { $archive.CreateEntry('obsolete.txt') | Out-Null } finally { $archive.Dispose() }
}
& $package -Tag $tag -DllPath $dll -OutputRoot $assets | Out-Null
Assert-ReleasePackage $assets
Write-PackagePass 'repeated explicit packaging replaces obsolete ZIP entries'

& $package -DllPath (Join-Path $assets 'AstralPartyChatPlugin.dll') -OutputRoot $assets | Out-Null
Assert-ReleasePackage $assets
Write-PackagePass 'explicit DLL already in the output directory packages successfully'

$renamedDll = Join-Path $testRoot 'renamed-input.dll'
Copy-Item -LiteralPath $dll -Destination $renamedDll
$renamedAssets = Join-Path $testRoot 'renamed-assets'
& $package -DllPath $renamedDll -OutputRoot $renamedAssets | Out-Null
Assert-ReleasePackage $renamedAssets
Write-PackagePass 'renamed valid input DLL produces only canonical filenames and ZIP entries'

$forkAssets = Join-Path $testRoot 'fork-assets'
& $package -DllPath $dll -OutputRoot $forkAssets -Repository 'owner/fork' -SigningKeyPath $keyPath | Out-Null
Assert-ReleasePackage $forkAssets 'owner/fork'
if ((Invoke-PluginVerifier $forkAssets).Accepted) { throw 'The plugin accepted a release that downloads from another repository.' }
Write-PackagePass 'a release packaged for another repository is self-consistent but refused by the plugin'

$mismatch = Join-Path $testRoot 'mismatch'
$otherTag = 'v' + (Get-AstralNextVersion -Version $version -Bump patch)
Assert-Rejected @{ Tag = $otherTag; DllPath = $dll; OutputRoot = $mismatch } 'must match the built DLL version'
Assert-Rejected @{ Tag = 'v01.0.0'; DllPath = $dll; OutputRoot = $mismatch } 'canonical vX.Y.Z'
Assert-Rejected @{ DllPath = (Join-Path $testRoot 'missing.dll'); OutputRoot = $mismatch } 'Missing plugin DLL'
Assert-Rejected @{ DllPath = $dll; OutputRoot = $mismatch; Repository = 'https://github.com/owner/repo' } 'Repository must be owner/name'
$wrongIdentity = New-AssemblyFixture 'UnrelatedPlugin' $version (Join-Path $testRoot 'wrong-identity')
$wrongVersion = New-AssemblyFixture 'AstralPartyChatPlugin' ($otherTag.Substring(1)) (Join-Path $testRoot 'wrong-version')
Assert-Rejected @{ DllPath = $wrongIdentity; OutputRoot = $mismatch } 'Unexpected plugin assembly'
Assert-Rejected @{ DllPath = $wrongVersion; OutputRoot = $mismatch } 'must match the built DLL version'
# A release nobody can verify must not be produced: wrong key, missing key, and the environment fallback.
Assert-Rejected @{ DllPath = $dll; OutputRoot = $mismatch; SigningKeyPath = $otherKeyPath } 'does not match the trusted P-256 public key'
Assert-Rejected @{ DllPath = $dll; OutputRoot = $mismatch; SigningKeyPath = (Join-Path $testRoot 'missing.pem') } 'Missing update signing key'
$env:ASTRAL_UPDATE_SIGNING_KEY = $otherKeyPath
try { Assert-Rejected @{ DllPath = $dll; OutputRoot = $mismatch } 'does not match the trusted P-256 public key' }
finally { $env:ASTRAL_UPDATE_SIGNING_KEY = $keyPath }
if (Test-Path -LiteralPath $mismatch) { throw 'Rejected packaging created release output.' }
Write-PackagePass 'rejected inputs create no release output'

[IO.File]::WriteAllText((Join-Path $assets 'unrelated.txt'), 'preserve', $utf8)
$before = Get-AssetFingerprint $assets
Assert-Rejected @{ DllPath = $dll; OutputRoot = $assets } 'unexpected file'
if ((Get-AssetFingerprint $assets) -cne $before) { throw 'Rejected packaging changed the existing release.' }

# Default packaging builds through build.ps1 (the recording stub here).
$automaticAssets = Join-Path $fixtureRoot "dist/release/$tag"
$automaticDll = Join-Path $fixtureRoot 'dist/AstralPartyChatPlugin.dll'
& $package | Out-Null
Assert-ReleasePackage $automaticAssets
if ([IO.File]::ReadAllLines($callsPath).Count -ne 1) { throw 'Packaging did not build exactly once.' }
Write-PackagePass 'default packaging builds exactly once and produces fresh assets'
[IO.File]::WriteAllText($automaticDll, 'stale DLL', $utf8)
& $package | Out-Null
Assert-ReleasePackage $automaticAssets
if ([IO.File]::ReadAllLines($callsPath).Count -ne 2) { throw 'Packaging reused an existing DLL without rebuilding.' }
Write-PackagePass 'repeated default packaging rebuilds instead of reusing a stale DLL'
& $package -DllPath $dll -OutputRoot (Join-Path $fixtureRoot 'explicit') | Out-Null
if ([IO.File]::ReadAllLines($callsPath).Count -ne 2) { throw 'Explicit DLL packaging unexpectedly built the plugin.' }
Write-PackagePass 'explicit DLL packaging bypasses the build'
$gameRoot = 'D:\Steam Library\Astral Party\8vJXnINT'
$refsRoot = 'D:\Build References\BepInEx'
$workRoot = Join-Path $testRoot 'Package Work\SDK and References'
$buildDotnet = 'D:\Dotnet SDK\dotnet.exe'
& $package -GameRoot $gameRoot -RefsRoot $refsRoot -WorkRoot $workRoot -DotNetPath $buildDotnet | Out-Null
$calls = @([IO.File]::ReadAllLines($callsPath) | ForEach-Object { $_ | ConvertFrom-Json })
if ($calls.Count -ne 3 -or $calls[-1].GameRoot -cne $gameRoot -or $calls[-1].RefsRoot -cne $refsRoot -or
    $calls[-1].WorkRoot -cne $workRoot -or $calls[-1].DotNetPath -cne $buildDotnet) {
    throw 'Packaging did not forward game, reference, work and SDK paths to the build.'
}
Assert-ReleasePackage $automaticAssets
if (@(Get-ChildItem -LiteralPath $workRoot -Recurse -File -Force).Count -ne 0) { throw 'Packaging left staging files in the work directory.' }
Write-PackagePass 'all four build paths are forwarded intact, including spaces, and staging is removed'
Assert-Rejected @{ Tag = $otherTag } 'must match VERSION when building'
foreach ($invalidTag in @('v01.0.0', '1.0.0', 'v1.0.0-rc.1')) {
    Assert-Rejected @{ Tag = $invalidTag } 'canonical vX.Y.Z'
}
Assert-Rejected @{ OutputRoot = $assets } 'unexpected file'
Assert-Rejected @{ DllPath = '' } 'DllPath'
Assert-Rejected @{ DllPath = $dll; RefsRoot = $refsRoot } '' 'AmbiguousParameterSet*'
Assert-Rejected @{ SigningKeyPath = $otherKeyPath } 'does not match the trusted P-256 public key'
if ([IO.File]::ReadAllLines($callsPath).Count -ne 3) { throw 'Invalid package settings triggered a build.' }
$before = Get-AssetFingerprint $automaticAssets
[IO.File]::WriteAllText((Join-Path $fixtureRoot 'fail-build'), '', $utf8)
Assert-Rejected @{} 'Fixture build failed'
if ((Get-AssetFingerprint $automaticAssets) -cne $before) { throw 'A failed build changed existing release assets.' }
Write-PackagePass 'failed default build preserves every existing release asset'
$failedAssets = Join-Path $fixtureRoot 'failed-assets'
Assert-Rejected @{ OutputRoot = $failedAssets } 'Fixture build failed'
if (Test-Path -LiteralPath $failedAssets) { throw 'A failed build packaged an old DLL.' }
Write-PackagePass 'failed build creates no new package from an old DLL'
Remove-Item -LiteralPath (Join-Path $fixtureRoot 'fail-build')

# A missing installer source is found before anything is built or written.
$installerSource = Join-Path $fixtureRoot 'installer/install.ps1'
Rename-Item -LiteralPath $installerSource -NewName 'install.ps1.hidden'
try {
    $before = Get-AssetFingerprint $automaticAssets
    Assert-Rejected @{} 'Missing install package source'
    if ((Get-AssetFingerprint $automaticAssets) -cne $before -or [IO.File]::ReadAllLines($callsPath).Count -ne 5) {
        throw 'A missing installer source still built or changed the release.'
    }
} finally { Rename-Item -LiteralPath "$installerSource.hidden" -NewName 'install.ps1' }

# A source can disappear after preflight. A failed replacement must retain the
# old ZIP and remove its partial temporary archive.
. (Join-Path $fixtureScripts 'package-files.ps1')
$atomicAssets = Join-Path $testRoot 'atomic-assets'
& $package -DllPath $dll -OutputRoot $atomicAssets | Out-Null
$before = Get-AssetFingerprint $atomicAssets
$sourceFiles = [ordered]@{ $dllEntry = $dll; 'missing.dll' = (Join-Path $testRoot 'missing-source.dll') }
$failed = $false
try { New-AstralZipPackage -Files $sourceFiles -OutputRoot $atomicAssets -ZipName $updateZip | Out-Null } catch [IO.FileNotFoundException] { $failed = $true }
if (-not $failed) { throw 'Missing archive source was accepted.' }
if ((Get-AssetFingerprint $atomicAssets) -cne $before) { throw 'Failed ZIP replacement changed old assets or leaked a temporary archive.' }
Write-PackagePass 'missing ZIP source preserves the previous package and cleans its temporary archive'
$failed = $false
try { New-AstralZipPackage -Files ([ordered]@{ 'WINHTTP.dll' = $dll }) -OutputRoot $atomicAssets -ZipName $installZip -BaseArchive $runtime | Out-Null }
catch { if ($_.Exception.Message -notlike '*collides with BepInEx*') { throw }; $failed = $true }
if (-not $failed -or (Get-AssetFingerprint $atomicAssets) -cne $before) { throw 'A plugin file silently replaced a BepInEx file in the install ZIP.' }
Write-PackagePass 'a package path that collides with BepInEx is rejected'
Write-Output "$passed chat release package checks passed. No game, external service or real signing key was used."
$global:LASTEXITCODE = 0
} finally {
    $env:ASTRAL_UPDATE_SIGNING_KEY = $savedSigningKey
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
