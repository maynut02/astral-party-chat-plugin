#requires -Version 7.0
param([switch]$KeepWorkspace)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
foreach ($name in @('release.ps1', 'publish-release.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "scripts/$name") -PathType Leaf)) {
        throw "Missing scripts/$name. Local release tests require the local release implementation."
    }
}
$localPwsh = Join-Path $repoRoot '.work/pwsh-7.6.6/pwsh.exe'
$pwsh = if (Test-Path -LiteralPath $localPwsh) { $localPwsh } else { Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source }
$localDotnet = Join-Path $repoRoot '.work/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source }
# Application lookup can return several copies on PATH, including on Actions.
$nativeGit = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
# Nested release fixtures must stay short enough for Windows file-version reads.
$testParent = [IO.Path]::GetFullPath((Join-Path $repoRoot '.work/tests'))
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
$oldPath = $env:PATH
$oldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$script:passed = 0
. (Join-Path $repoRoot 'scripts/reference-files.ps1')
$referencePaths = @(Get-AstralReferencePaths)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Write-TestFile([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}
function Write-TestJson([string]$Path, [object]$Value) {
    Write-TestFile $Path (ConvertTo-Json -InputObject $Value -Depth 30)
}
function Invoke-FixtureGit([string]$Root, [string[]]$Arguments) {
    $output = @(& $nativeGit -C $Root @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($Arguments -join ' ')`n$($output -join "`n")" }
    return ($output -join "`n").Trim()
}
function Get-Trace([object]$Result, [string]$Stage) {
    return @($Result.Trace | Where-Object { $_.stage -ceq $Stage })
}
function Assert-NoPublishing([object]$Result) {
    Assert-True ((Get-Trace $Result 'publisher-start').Count -eq 0) 'A blocked build reached the publisher.'
    Assert-True ((Get-Trace $Result 'gh-mutation').Count -eq 0) 'A blocked build attempted a remote mutation.'
}
function Assert-Rejected([object]$Result, [string]$Pattern = '') {
    Assert-True ($Result.Code -ne 0) 'Expected the release invocation to fail.'
    if ($Pattern) { Assert-True ($Result.Output -match $Pattern) "Rejection happened at an unexpected stage: $($Result.Output)" }
}

# Each child invokes the real coordinator and publisher. Only expensive build/
# regression stages are replaced inside its disposable Git repository. gh is a
# function backed by an on-disk fake remote; git network commands are prohibited.
$childWrapper = @'
param([string]$FixtureRoot, [string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:GH_TOKEN = ''
$env:GITHUB_TOKEN = ''
$env:GH_HOST = 'github.com'
$global:LocalReleaseTestRoot = $FixtureRoot
$global:LocalReleaseTestConfig = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$global:LocalReleaseTestGit = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
$global:LocalReleaseTestTracePath = Join-Path $FixtureRoot '.work/trace.jsonl'
$global:LocalReleaseTestRemotePath = Join-Path $FixtureRoot '.work/remote.json'
$global:LocalReleaseTestRemote = Get-Content -LiteralPath $global:LocalReleaseTestRemotePath -Raw | ConvertFrom-Json
function global:Write-LocalReleaseTrace([string]$Stage, [object]$Data = $null) {
    $line = @{ stage = $Stage; data = $Data } | ConvertTo-Json -Depth 20 -Compress
    [IO.File]::AppendAllText($global:LocalReleaseTestTracePath, $line + "`n", [Text.UTF8Encoding]::new($false))
}
function global:git {
    foreach ($argument in $args) {
        if ("$argument" -in @('push', 'fetch', 'pull', 'clone', 'ls-remote', 'submodule')) {
            throw 'Git network operations are forbidden in local release fixtures.'
        }
    }
    & $global:LocalReleaseTestGit @args
    $global:LASTEXITCODE = $LASTEXITCODE
}
function global:Invoke-LocalReleaseStage([string]$Name, [object]$Data = $null) {
    Write-LocalReleaseTrace $Name $Data
    $config = $global:LocalReleaseTestConfig
    if ($config.FailureStage -ceq $Name) { throw "MOCK_STAGE_FAILURE:$Name" }
    if ($config.MutateAt -ceq $Name) {
        switch ($config.Mutation) {
            'tracked' { [IO.File]::AppendAllText((Join-Path $global:LocalReleaseTestRoot 'README.md'), 'modified during release') }
            'untracked' { [IO.File]::WriteAllText((Join-Path $global:LocalReleaseTestRoot 'unexpected-source.txt'), 'new source') }
            'commit' {
                [IO.File]::AppendAllText((Join-Path $global:LocalReleaseTestRoot 'README.md'), 'new commit during release')
                git -C $global:LocalReleaseTestRoot add -- README.md | Out-Null
                git -C $global:LocalReleaseTestRoot commit -m 'fixture changed during release' | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Fixture mutation commit failed.' }
            }
            'reference' {
                Copy-Item -LiteralPath (Join-Path $config.AssemblyPool '1.0.8/AstralParty.Chat.dll') -Destination (Join-Path $config.RefsRoot 'core/0Harmony.dll') -Force
            }
            'snapshot-reference' {
                Copy-Item -LiteralPath (Join-Path $config.AssemblyPool '1.0.8/AstralParty.Chat.dll') -Destination (Join-Path $Data.refsRoot 'core/0Harmony.dll') -Force
            }
            'snapshot-installer' { [IO.File]::AppendAllText((Join-Path $Data.sourceRoot 'scripts/install.ps1'), 'modified snapshot installer') }
            'snapshot-generator' { [IO.File]::AppendAllText((Join-Path $Data.sourceRoot 'scripts/distribution-installer.ps1'), 'modified snapshot generator') }
        }
    }
}
function global:gh {
    $arguments = @($args | ForEach-Object { "$_" })
    Write-LocalReleaseTrace 'gh-read' @{ arguments = $arguments }
    $config = $global:LocalReleaseTestConfig
    $remote = $global:LocalReleaseTestRemote
    $global:LASTEXITCODE = 0
    if ($arguments[0] -ceq 'auth' -and $arguments[1] -ceq 'status') {
        if ('--hostname' -cnotin $arguments -or 'github.com' -cnotin $arguments) { throw 'GitHub hostname was not pinned.' }
        if ($config.FailureStage -ceq 'auth') { $global:LASTEXITCODE = 1; return }
        return 'Mock authenticated account'
    }
    if ($arguments[0] -ceq 'api') {
        if ('--hostname' -cnotin $arguments -or 'github.com' -cnotin $arguments) { throw 'GitHub API hostname was not pinned.' }
        $endpoint = @($arguments | Where-Object { $_.StartsWith('repos/', [StringComparison]::Ordinal) })
        if ($endpoint.Count -ne 1) { throw 'Unexpected GitHub API arguments.' }
        $endpoint = $endpoint[0]
        $prefix = 'repos/example/chat-plugin'
        if (-not ($endpoint -ceq $prefix -or $endpoint.StartsWith($prefix + '/', [StringComparison]::Ordinal))) { throw "Unexpected repository endpoint: $endpoint" }
        if ($endpoint -ceq $prefix) {
            if ($config.FailureStage -ceq 'repository-api') { $global:LASTEXITCODE = 1; return }
            return (@{ full_name = 'example/chat-plugin'; default_branch = 'main' } | ConvertTo-Json -Compress)
        }
        if ($endpoint -ceq "$prefix/commits/main") {
            if ($config.FailureStage -ceq 'commit-api') { $global:LASTEXITCODE = 1; return }
            return (@{ sha = $config.RemoteCommit } | ConvertTo-Json -Compress)
        }
        if ($endpoint -ceq "$prefix/tags?per_page=100") {
            if ($config.FailureStage -ceq 'tags-api') { $global:LASTEXITCODE = 1; return }
            $tags = @(@($config.Tags) + @($remote.tags) | Select-Object -Unique)
            if ('--jq' -cin $arguments) { return $tags }
            $page = ConvertTo-Json -InputObject @($tags | ForEach-Object { @{ name = $_ } }) -Depth 5 -Compress
            return "[$page]"
        }
        if ($endpoint -ceq "$prefix/releases?per_page=100") {
            $page = ConvertTo-Json -InputObject @($remote.draftTags | ForEach-Object { @{ tag_name = $_; draft = ($_ -cnotin @($remote.publishedTags)) } }) -Depth 5 -Compress
            return "[$page]"
        }
        if ($endpoint.StartsWith("$prefix/git/ref/tags/", [StringComparison]::Ordinal)) {
            return (@{ object = @{ type = 'commit'; sha = $config.RemoteCommit } } | ConvertTo-Json -Depth 5 -Compress)
        }
        if ($endpoint -ceq "$prefix/git/refs" -and 'POST' -cin $arguments) {
            Write-LocalReleaseTrace 'gh-mutation' @{ operation = 'create-tag'; arguments = $arguments }
            $ref = @($arguments | Where-Object { $_.StartsWith('ref=refs/tags/', [StringComparison]::Ordinal) })
            if ($ref.Count -ne 1) { throw 'Invalid mock tag creation.' }
            $remote.tags = @($remote.tags) + @($ref[0].Substring('ref=refs/tags/'.Length))
            return '{}'
        }
        if ($endpoint -ceq "$prefix/releases/generate-notes" -and 'POST' -cin $arguments) {
            Write-LocalReleaseTrace 'gh-mutation' @{ operation = 'generate-notes'; arguments = $arguments }
            return '{"body":"Mock generated changes"}'
        }
        throw "Unexpected mock API endpoint: $endpoint"
    }
    if ($arguments[0] -ceq 'release' -and $arguments[1] -cin @('create', 'upload', 'edit', 'view')) {
        $operation = $arguments[1]
        $tag = $arguments[2]
        if ($operation -ceq 'view') {
            $names = if ($tag -cin @($remote.uploadedTags)) { @('AstralParty.Chat.dll', "AstralParty.Chat-$tag.zip", 'SHA256SUMS.txt') } else { @() }
            return (@{ isDraft = ($tag -cnotin @($remote.publishedTags)); assets = @($names | ForEach-Object { @{ name = $_ } }) } | ConvertTo-Json -Depth 5 -Compress)
        }
        Write-LocalReleaseTrace 'gh-mutation' @{ operation = "release-$operation"; arguments = $arguments }
        if ($config.FailureStage -ceq "release-$operation") { $global:LASTEXITCODE = 1; return }
        switch ($operation) {
            'create' { $remote.draftTags = @($remote.draftTags) + @($tag) }
            'upload' { $remote.uploadedTags = @($remote.uploadedTags) + @($tag) }
            'edit' { $remote.publishedTags = @($remote.publishedTags) + @($tag) }
        }
        return 'Mock release operation succeeded'
    }
    throw "Unexpected gh invocation: $($arguments -join ' ')"
}
Set-Location -LiteralPath $FixtureRoot
try {
    $parameters = @{}
    $config = $global:LocalReleaseTestConfig
    if (-not $config.UseDefaultRefs) { $parameters.RefsRoot = $config.RefsRoot }
    $parameters.GameRoot = $config.GameRoot
    if ($config.Bump) { $parameters.Bump = $config.Bump }
    if ($config.Tag) { $parameters.Tag = $config.Tag }
    if ($config.PrepareOnly) { $parameters.PrepareOnly = $true }
    if ($config.AbandonPending) { $parameters.AbandonPending = $true }
    & (Join-Path $FixtureRoot 'scripts/release.ps1') @parameters
    exit 0
}
catch {
    Write-Output "MOCK_RELEASE_ERROR:$($_.Exception.Message)"
    exit 1
}
finally {
    [IO.File]::WriteAllText($global:LocalReleaseTestRemotePath, ($global:LocalReleaseTestRemote | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}
'@
$buildStub = @'
param([string]$GameRoot = '', [string]$RefsRoot = '', [string]$Version = '', [switch]$Deploy,
    [string]$SourceRoot = '', [string]$BuildRoot = '', [string]$OutputRoot = '', [string]$DotnetPath = '')
if ($Deploy) { throw 'Game deployment is forbidden in local release fixtures.' }
$snapshot = [IO.Path]::GetFullPath($RefsRoot)
$source = [IO.Path]::GetFullPath($global:LocalReleaseTestConfig.RefsRoot)
if ($snapshot.Equals($source, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build did not use an immutable reference snapshot.' }
. (Join-Path $PSScriptRoot 'reference-files.ps1')
$expected = @(Get-AstralReferencePaths)
$actual = @(Get-ChildItem -LiteralPath $snapshot -File -Recurse -Force | ForEach-Object { [IO.Path]::GetRelativePath($snapshot, $_.FullName).Replace('\', '/') })
if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { $_ -cnotin $expected }).Count) { throw 'Reference snapshot contains unexpected files.' }
foreach ($name in $expected) { [Reflection.AssemblyName]::GetAssemblyName((Join-Path $snapshot $name)) | Out-Null }
Invoke-LocalReleaseStage 'build.ps1' @{ gameRoot = $GameRoot; refsRoot = $snapshot; version = $Version; deploy = [bool]$Deploy; sourceRoot = $SourceRoot; buildRoot = $BuildRoot; outputRoot = $OutputRoot; dotnetPath = $DotnetPath }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($SourceRoot) {
    $sourceBoundary = [IO.Path]::GetFullPath($SourceRoot)
    if ($sourceBoundary.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath (Join-Path $sourceBoundary 'src/AstralPartyChatPlugin.cs') -PathType Leaf)) {
        throw 'Build must use an isolated complete source snapshot.'
    }
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'dist' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
Copy-Item -LiteralPath (Join-Path $global:LocalReleaseTestConfig.AssemblyPool "$Version/AstralParty.Chat.dll") -Destination (Join-Path $OutputRoot 'AstralParty.Chat.dll') -Force
'@
$publishWrapper = @'
param([string]$Repository, [string]$Tag, [string]$SourceCommit, [string]$AssetsRoot, [string]$BuildInfoPath, [string]$NotesPath, [switch]$RecoverPublished, [string]$InstallerScriptPath)
Invoke-LocalReleaseStage 'publisher-start' @{ repository = $Repository; tag = $Tag; sourceCommit = $SourceCommit; assetsRoot = $AssetsRoot; buildInfoPath = $BuildInfoPath; notesPath = $NotesPath; recoverPublished = [bool]$RecoverPublished }
& (Join-Path $PSScriptRoot 'publish-release.actual.ps1') @PSBoundParameters
Write-LocalReleaseTrace 'publisher-complete' @{ tag = $Tag }
'@

function New-ReleaseFixture([string]$Name, [switch]$NoCommit,
    [ValidateSet('true', 'input', 'false')][string]$AutoCrlf = 'true',
    [ValidateSet('native', 'lf', 'crlf')][string]$CoreEol = 'native') {
    $root = Join-Path $testRoot $Name
    [IO.Directory]::CreateDirectory((Join-Path $root 'scripts')) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'scripts') -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $root 'scripts')
    }
    Copy-Item -LiteralPath (Join-Path $root 'scripts/publish-release.ps1') -Destination (Join-Path $root 'scripts/publish-release.actual.ps1')
    Write-TestFile (Join-Path $root 'scripts/publish-release.ps1') $publishWrapper
    Write-TestFile (Join-Path $root 'scripts/build.ps1') $buildStub
    foreach ($relative in @('scripts/check-public-files.ps1', 'scripts/test.ps1', 'tests/scripts/InstallTests.ps1', 'tests/scripts/DistributionInstallerTests.ps1', 'tests/scripts/ReleaseVersionTests.ps1', 'tests/scripts/LocalReleaseTests.ps1', 'tests/scripts/ReferenceArchiveTests.ps1', 'tests/scripts/ReleaseAssetsTests.ps1')) {
        $stage = [IO.Path]::GetFileName($relative)
        Write-TestFile (Join-Path $root $relative) "param([string]`$RefsRoot = '')`nInvoke-LocalReleaseStage '$stage' @{ refsRoot = `$RefsRoot; arguments = @(`$args) }`n"
    }
    # No setup/install/sync fallback may access the real game, download a SDK,
    # or deploy anything. The normal test path supplies all references itself.
    foreach ($name in @('setup.ps1', 'install.ps1', 'sync-refs.ps1', 'import-refs.ps1')) {
        Write-TestFile (Join-Path $root "scripts/$name") "throw 'Unexpected $name in an isolated local release test.'`n"
    }
    Write-TestFile (Join-Path $root '.gitignore') ".work/`ndist/`nbin/`nobj/`n"
    Write-TestFile (Join-Path $root 'README.md') 'Local release fixture source.'
    Write-TestFile (Join-Path $root 'dist/AstralParty.Chat.dll') 'existing developer build must remain untouched'
    Write-TestFile (Join-Path $root 'src/AstralPartyChatPlugin.cs') 'public static class AstralPartyChatPlugin { public const string PluginVersion = "1.0.7"; }'
    Write-TestFile (Join-Path $root 'docs/install.txt') 'Fixture installation instructions.'
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'global.json')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination (Join-Path $root 'global.json')
    }
    $refsRoot = Join-Path $root '.work/refs'
    foreach ($relative in $referencePaths) {
        $destination = Join-Path $refsRoot $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $assemblyPool '1.0.7/AstralParty.Chat.dll') -Destination $destination
    }
    Invoke-FixtureGit $root @('init', '--initial-branch=main') | Out-Null
    Invoke-FixtureGit $root @('config', 'user.name', 'Local release test') | Out-Null
    Invoke-FixtureGit $root @('config', 'user.email', 'local-release-test@example.invalid') | Out-Null
    Invoke-FixtureGit $root @('config', 'commit.gpgsign', 'false') | Out-Null
    Invoke-FixtureGit $root @('config', 'core.autocrlf', $AutoCrlf) | Out-Null
    Invoke-FixtureGit $root @('config', 'core.eol', $CoreEol) | Out-Null
    Invoke-FixtureGit $root @('config', 'core.hooksPath', (Join-Path $root '.work/empty-hooks')) | Out-Null
    Invoke-FixtureGit $root @('remote', 'add', 'origin', 'https://github.com/example/chat-plugin.git') | Out-Null
    $head = ''
    if (-not $NoCommit) {
        Invoke-FixtureGit $root @('add', '--all') | Out-Null
        Invoke-FixtureGit $root @('commit', '-m', 'isolated local release fixture') | Out-Null
        $head = Invoke-FixtureGit $root @('rev-parse', 'HEAD')
    }
    $config = [ordered]@{
        AssemblyPool = $assemblyPool; RefsRoot = $refsRoot; GameRoot = (Join-Path $root '.work/fake-game')
        RemoteCommit = $head; Tags = @('v1.0.7'); Bump = ''; Tag = ''; PrepareOnly = $false; UseDefaultRefs = $false; AbandonPending = $false
        FailureStage = ''; MutateAt = ''; Mutation = ''
    }
    Write-TestJson (Join-Path $root '.work/remote.json') @{ tags = @(); draftTags = @(); uploadedTags = @(); publishedTags = @() }
    return [pscustomobject]@{ Root = $root; Head = $head; Config = $config }
}
function Invoke-LocalRelease([object]$Fixture) {
    $configPath = Join-Path $Fixture.Root '.work/config.json'
    Write-TestJson $configPath $Fixture.Config
    $tracePath = Join-Path $Fixture.Root '.work/trace.jsonl'
    Write-TestFile $tracePath ''
    $output = @(& $pwsh -NoProfile -File $wrapperPath -FixtureRoot $Fixture.Root -ConfigPath $configPath 2>&1)
    $code = $LASTEXITCODE
    $trace = @(Get-Content -LiteralPath $tracePath | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    return [pscustomobject]@{ Code = $code; Output = ($output -join "`n"); Trace = $trace }
}
function Get-BuildInfo([object]$Fixture, [bool]$Preview = $false) {
    $area = if ($Preview) { '.work/release-previews' } else { '.work/releases' }
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $Fixture.Root $area) -Filter '*.json' -File -Recurse | Where-Object {
        $_.Name -cne 'pending.json' -and (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).PSObject.Properties.Name -contains 'references'
    })
    Assert-True ($candidates.Count -eq 1) 'Expected one build information file in the selected release area.'
    return [pscustomobject]@{ Path = $candidates[0].FullName; Info = (Get-Content -LiteralPath $candidates[0].FullName -Raw | ConvertFrom-Json) }
}
function Assert-Manifest([object]$Fixture, [object]$Build, [string]$Version, [bool]$Clean, [string]$ReferenceSource = '') {
    $info = $Build.Info
    $expectedFields = @('schemaVersion', 'repository', 'tag', 'version', 'sourceCommit', 'sourceClean', 'builtAtUtc', 'sdkVersion', 'references', 'assets')
    Assert-True (@($info.PSObject.Properties.Name).Count -eq $expectedFields.Count -and @($info.PSObject.Properties.Name | Where-Object { $_ -cnotin $expectedFields }).Count -eq 0) 'Unexpected build information fields.'
    Assert-True ($info.schemaVersion -eq 1 -and $info.repository -ceq 'example/chat-plugin' -and $info.tag -ceq "v$Version" -and $info.version -ceq $Version) 'Incorrect release identity in build information.'
    Assert-True ($info.sourceCommit -ceq $Fixture.Head -and $info.sourceClean -is [bool] -and $info.sourceClean -eq $Clean) 'Incorrect source provenance.'
    Assert-True ($info.sdkVersion -ceq $sdkVersion) 'Incorrect SDK version.'
    # Newer PowerShell parses JSON ISO timestamps into DateTime automatically.
    # Preserve its UTC kind rather than using its locale-dependent string cast.
    $timestampText = if ($info.builtAtUtc -is [DateTime]) { $info.builtAtUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture) } else { [string]$info.builtAtUtc }
    $timestamp = [DateTimeOffset]::Parse($timestampText, [Globalization.CultureInfo]::InvariantCulture)
    Assert-True ($timestamp.Offset -eq [TimeSpan]::Zero -and [Math]::Abs(([DateTimeOffset]::UtcNow - $timestamp).TotalMinutes) -lt 10) 'Build timestamp must reflect the current UTC build.'
    Assert-True ($info.references -is [Array] -and $info.references.Count -eq 12 -and @($info.references.file | Select-Object -Unique).Count -eq 12) 'The reference list must contain the exact 12 unique paths.'
    if (-not $ReferenceSource) { $ReferenceSource = $Fixture.Config.RefsRoot }
    foreach ($reference in $info.references) {
        Assert-True ($reference.file -cin $referencePaths) 'Unexpected build reference.'
        $path = Join-Path $ReferenceSource $reference.file
        Assert-True ($reference.assemblyVersion -ceq [Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString()) 'Reference assembly version mismatch.'
        Assert-True ($reference.fileVersion -ceq [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion) 'Reference file version mismatch.'
        Assert-True ($reference.sha256 -ceq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) 'Reference checksum mismatch.'
    }
    Assert-True ($info.assets -is [Array] -and $info.assets.Count -eq 2 -and @($info.assets.file | Select-Object -Unique).Count -eq 2) 'Build information must list only the DLL and install ZIP.'
    $buildDirectory = Split-Path -Parent $Build.Path
    foreach ($asset in $info.assets) {
        Assert-True ($asset.file -cin @('AstralParty.Chat.dll', "AstralParty.Chat-v$Version.zip")) 'Unexpected manifest asset.'
        $path = Join-Path (Join-Path $buildDirectory 'assets') $asset.file
        Assert-True ((Test-Path -LiteralPath $path -PathType Leaf) -and $asset.sha256 -ceq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) 'Build asset checksum mismatch.'
    }
}
function New-FailedUpload([string]$Name) {
    $fixture = New-ReleaseFixture $Name
    $fixture.Config.FailureStage = 'release-upload'
    $result = Invoke-LocalRelease $fixture
    Assert-Rejected $result 'upload'
    Assert-True ((Get-Trace $result 'publisher-start').Count -eq 1 -and (Get-Trace $result 'publisher-complete').Count -eq 0) 'Expected a failed publisher invocation.'
    $pendingPath = Join-Path $fixture.Root '.work/releases/pending.json'
    Assert-True (Test-Path -LiteralPath $pendingPath -PathType Leaf) 'An upload failure must preserve pending release state.'
    $fixture.Config.FailureStage = ''
    return [pscustomobject]@{ Fixture = $fixture; Result = $result; PendingPath = $pendingPath; Pending = (Get-Content -LiteralPath $pendingPath -Raw | ConvertFrom-Json); Build = (Get-BuildInfo $fixture) }
}
function Invoke-LocalReleaseCase([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; Write-Output "PASS local release: $Name" }
    catch { throw "FAIL local release ${Name}: $($_.Exception.Message)" }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $env:PATH = [IO.Path]::GetDirectoryName($dotnet) + [IO.Path]::PathSeparator + [IO.Path]::GetDirectoryName($pwsh) + [IO.Path]::PathSeparator + $oldPath
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $sdkVersion = (@(& $dotnet --version) -join '').Trim()
    Assert-True ($LASTEXITCODE -eq 0 -and $sdkVersion -match '^\d+\.\d+\.\d+') 'A .NET SDK is required for the tiny managed assembly fixtures.'
    $assemblyPool = Join-Path $testRoot 'assembly-pool'
    $projectRoot = Join-Path $testRoot 'assembly-source'
    Write-TestFile (Join-Path $projectRoot 'Fixture.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net6.0</TargetFramework><AssemblyName>AstralParty.Chat</AssemblyName><IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>'
    Write-TestFile (Join-Path $projectRoot 'Fixture.cs') 'public static class ReleaseFixture { public const string Value = "fixture only"; }'
    Write-TestFile (Join-Path $projectRoot 'NuGet.Config') '<configuration><packageSources><clear /></packageSources></configuration>'
    foreach ($version in @('1.0.7', '1.0.8', '1.1.0', '2.0.0')) {
        $destination = Join-Path $assemblyPool $version
        $buildOutput = @(& $dotnet build (Join-Path $projectRoot 'Fixture.csproj') --configuration Release --nologo --output $destination "-p:Version=$version" 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Tiny assembly fixture build failed: $($buildOutput -join "`n")" }
        Assert-True ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $destination 'AstralParty.Chat.dll')).ProductVersion -ceq $version) 'Fixture assembly informational version mismatch.'
    }
    $wrapperPath = Join-Path $testRoot 'invoke-release.ps1'
    Write-TestFile $wrapperPath $childWrapper

    foreach ($autoCrlf in @('true', 'input', 'false')) {
        foreach ($eol in @('crlf', 'lf')) {
            Invoke-LocalReleaseCase "committed-source-autocrlf-$autoCrlf-eol-$eol" {
                $f = New-ReleaseFixture ("eol-$autoCrlf-$eol") -AutoCrlf $autoCrlf -CoreEol $eol
                $r = Invoke-LocalRelease $f
                Assert-True ($r.Code -eq 0) $r.Output
                Assert-True ((Get-Trace $r 'publisher-complete').Count -eq 1) 'Git line-ending settings prevented publication.'
                $snapshot = (Get-Trace $r 'build.ps1')[0].data.sourceRoot
                foreach ($name in @('install.ps1', 'distribution-installer.ps1')) {
                    $expected = Invoke-FixtureGit $f.Root @('rev-parse', "$($f.Head):scripts/$name")
                    $actual = Invoke-FixtureGit $f.Root @('hash-object', '--no-filters', (Join-Path $snapshot "scripts/$name"))
                    Assert-True ($actual -ceq $expected) "Source snapshot changed committed bytes: $name."
                }
                Assert-True ((Invoke-FixtureGit $f.Root @('config', 'core.autocrlf')) -ceq $autoCrlf) 'Release changed the developer Git autocrlf setting.'
                Assert-True ((Invoke-FixtureGit $f.Root @('config', 'core.eol')) -ceq $eol) 'Release changed the developer Git eol setting.'
                Assert-Manifest $f (Get-BuildInfo $f) '1.0.8' $true
            }
        }
    }

    Invoke-LocalReleaseCase 'multiple-git-executables-on-path' {
        $f = New-ReleaseFixture 'multiple git paths'
        $gitBins = @((Join-Path $f.Root '.work/first git'), (Join-Path $f.Root '.work/second git'))
        foreach ($directory in $gitBins) {
            Write-TestFile (Join-Path $directory 'git.cmd') "@echo off`r`n`"$nativeGit`" %*`r`nexit /b %errorlevel%`r`n"
        }
        $casePath = $env:PATH
        try {
            $env:PATH = ($gitBins -join [IO.Path]::PathSeparator) + [IO.Path]::PathSeparator + $casePath
            $applications = @(Get-Command git -CommandType Application -ErrorAction Stop)
            Assert-True ($applications.Count -ge 3 -and $applications[0].Source -ceq (Join-Path $gitBins[0] 'git.cmd')) 'Expected multiple Git executables in PATH precedence order.'
            $r = Invoke-LocalRelease $f
            Assert-True ($r.Code -eq 0) $r.Output
            Assert-True ((Get-Trace $r 'publisher-complete').Count -eq 1) 'Multiple Git paths prevented the mocked release from completing.'
            Assert-Manifest $f (Get-BuildInfo $f) '1.0.8' $true
        } finally { $env:PATH = $casePath }
    }

    Invoke-LocalReleaseCase 'clean-source-publishes-after-all-checks' {
        $f = New-ReleaseFixture 'clean-source'
        $r = Invoke-LocalRelease $f
        Assert-True ($r.Code -eq 0) $r.Output
        $stages = @('check-public-files.ps1', 'test.ps1', 'InstallTests.ps1', 'DistributionInstallerTests.ps1', 'ReleaseVersionTests.ps1', 'LocalReleaseTests.ps1', 'build.ps1', 'ReferenceArchiveTests.ps1', 'ReleaseAssetsTests.ps1', 'publisher-start', 'publisher-complete')
        $previous = -1
        foreach ($stage in $stages) {
            Assert-True ((Get-Trace $r $stage).Count -eq 1) "Expected exactly one $stage invocation."
            $index = [Array]::IndexOf(@($r.Trace.stage), $stage)
            Assert-True ($index -gt $previous) "Incorrect release stage order: $stage"
            $previous = $index
        }
        Assert-Manifest $f (Get-BuildInfo $f) '1.0.8' $true
        $build = (Get-Trace $r 'build.ps1')[0].data
        $boundary = [IO.Path]::GetFullPath((Join-Path $f.Root '.work/releases')) + [IO.Path]::DirectorySeparatorChar
        foreach ($path in @($build.sourceRoot, $build.buildRoot, $build.outputRoot)) {
            Assert-True ($path -and [IO.Path]::GetFullPath($path).StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) 'Release build did not isolate source, build and plugin output directories.'
        }
        Assert-True ([IO.File]::ReadAllText((Join-Path $build.sourceRoot 'README.md')) -ceq 'Local release fixture source.') 'Source snapshot did not contain committed source.'
        Assert-True ([IO.File]::ReadAllText((Join-Path $f.Root 'dist/AstralParty.Chat.dll')) -ceq 'existing developer build must remain untouched') 'Release overwrote the normal developer DLL.'
        Assert-True (-not (Get-Trace $r 'publisher-start')[0].data.recoverPublished) 'A new release enabled published-release recovery.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $f.Root '.work/releases/pending.json'))) 'Successful publication did not clear pending state.'
    }
    foreach ($origin in @('git@github.com:example/chat-plugin.git', 'ssh://git@github.com/example/chat-plugin.git')) {
        Invoke-LocalReleaseCase "github-origin-$origin" {
            $f = New-ReleaseFixture ('origin-' + $script:passed)
            Invoke-FixtureGit $f.Root @('remote', 'set-url', 'origin', $origin) | Out-Null
            $r = Invoke-LocalRelease $f
            Assert-True ($r.Code -eq 0) $r.Output
            Assert-Manifest $f (Get-BuildInfo $f) '1.0.8' $true
        }
    }
    foreach ($choice in @(@{ Name = 'first-release'; Bump = ''; Tags = @(); Version = '1.0.7' }, @{ Name = 'minor'; Bump = 'minor'; Tags = @('v1.0.7'); Version = '1.1.0' }, @{ Name = 'major'; Bump = 'major'; Tags = @('v1.0.7'); Version = '2.0.0' })) {
        Invoke-LocalReleaseCase $choice.Name {
            $f = New-ReleaseFixture $choice.Name
            $f.Config.Bump = $choice.Bump
            $f.Config.Tags = $choice.Tags
            $r = Invoke-LocalRelease $f
            Assert-True ($r.Code -eq 0) $r.Output
            Assert-Manifest $f (Get-BuildInfo $f) $choice.Version $true
        }
    }
    Invoke-LocalReleaseCase 'explicit-tag-does-not-bump' {
        $f = New-ReleaseFixture 'explicit-tag'
        $f.Config.Tags = @('v1.0.7', 'v1.0.8', 'v1.0.9')
        $f.Config.Tag = 'v1.0.8'
        $f.Config.Bump = 'major'
        $r = Invoke-LocalRelease $f
        Assert-True ($r.Code -eq 0) $r.Output
        Assert-Manifest $f (Get-BuildInfo $f) '1.0.8' $true
    }
    Invoke-LocalReleaseCase 'default-local-refs' {
        $f = New-ReleaseFixture 'default-refs'
        $f.Config.UseDefaultRefs = $true
        $r = Invoke-LocalRelease $f
        Assert-True ($r.Code -eq 0) $r.Output
        Assert-Manifest $f (Get-BuildInfo $f) '1.0.8' $true
    }
    foreach ($dirty in @($false, $true)) {
        Invoke-LocalReleaseCase "preview-never-publishes-dirty-$dirty" {
            $f = New-ReleaseFixture ('preview-' + $dirty)
            $f.Config.PrepareOnly = $true
            if ($dirty) {
                Invoke-FixtureGit $f.Root @('checkout', '-b', 'feature/local-preview') | Out-Null
                Write-TestFile (Join-Path $f.Root 'README.md') 'uncommitted local preview'
                Write-TestFile (Join-Path $f.Root 'untracked.txt') 'untracked local preview'
                $f.Config.RemoteCommit = 'b' * 40
            }
            $r = Invoke-LocalRelease $f
            Assert-True ($r.Code -eq 0) $r.Output
            Assert-NoPublishing $r
            Assert-Manifest $f (Get-BuildInfo $f $true) '1.0.8' $false
            $source = (Get-Trace $r 'build.ps1')[0].data.sourceRoot
            Assert-True ($source -and $source -like '*release-previews*') 'Preview did not compile an isolated preview source snapshot.'
            if ($dirty) {
                Assert-True ([IO.File]::ReadAllText((Join-Path $source 'README.md')) -ceq 'uncommitted local preview') 'Preview did not include tracked local edits.'
                Assert-True ([IO.File]::ReadAllText((Join-Path $source 'untracked.txt')) -ceq 'untracked local preview') 'Preview did not include non-ignored untracked source.'
            }
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $source '.work')) -and -not (Test-Path -LiteralPath (Join-Path $source 'dist'))) 'Source snapshot copied ignored local references or build artifacts.'
            Assert-True ([IO.File]::ReadAllText((Join-Path $f.Root 'dist/AstralParty.Chat.dll')) -ceq 'existing developer build must remain untouched') 'Preview overwrote the normal developer DLL.'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $f.Root '.work/releases/pending.json'))) 'Preview created pending publication state.'
        }
    }
    Invoke-LocalReleaseCase 'missing-source-commit' {
        $f = New-ReleaseFixture 'missing-commit' -NoCommit
        $r = Invoke-LocalRelease $f
        Assert-Rejected $r 'commit|HEAD|source|rev-parse'
        Assert-NoPublishing $r
    }
    foreach ($missing in @('commit', 'origin')) {
        Invoke-LocalReleaseCase "preview-still-requires-$missing" {
            $f = New-ReleaseFixture ('preview-missing-' + $missing) -NoCommit:($missing -ceq 'commit')
            $f.Config.PrepareOnly = $true
            if ($missing -ceq 'origin') { Invoke-FixtureGit $f.Root @('remote', 'remove', 'origin') | Out-Null }
            $r = Invoke-LocalRelease $f
            Assert-Rejected $r 'commit|HEAD|source|rev-parse|origin|remote'
            Assert-NoPublishing $r
        }
    }
    Invoke-LocalReleaseCase 'missing-origin' {
        $f = New-ReleaseFixture 'missing-origin'
        Invoke-FixtureGit $f.Root @('remote', 'remove', 'origin') | Out-Null
        $r = Invoke-LocalRelease $f
        Assert-Rejected $r 'origin|remote'
        Assert-NoPublishing $r
    }
    Invoke-LocalReleaseCase 'non-github-origin' {
        $f = New-ReleaseFixture 'invalid-origin'
        Invoke-FixtureGit $f.Root @('remote', 'set-url', 'origin', 'https://example.invalid/example/chat-plugin.git') | Out-Null
        $r = Invoke-LocalRelease $f
        Assert-Rejected $r 'origin|GitHub|github.com'
        Assert-NoPublishing $r
    }
    foreach ($kind in @('tracked', 'untracked', 'staged')) {
        Invoke-LocalReleaseCase "dirty-source-$kind" {
            $f = New-ReleaseFixture ('dirty-' + $kind)
            if ($kind -ceq 'untracked') { Write-TestFile (Join-Path $f.Root 'untracked.txt') 'untracked source' }
            else { Write-TestFile (Join-Path $f.Root 'README.md') 'changed source' }
            if ($kind -ceq 'staged') { Invoke-FixtureGit $f.Root @('add', '--', 'README.md') | Out-Null }
            $r = Invoke-LocalRelease $f
            Assert-Rejected $r 'clean|dirty|uncommitted|tracked|commit'
            Assert-NoPublishing $r
            Assert-True ((Get-Trace $r 'build.ps1').Count -eq 0) 'Dirty source reached the build stage.'
        }
    }
    foreach ($detached in @($false, $true)) {
        Invoke-LocalReleaseCase "non-default-branch-detached-$detached" {
            $f = New-ReleaseFixture ('branch-' + $detached)
            if ($detached) { Invoke-FixtureGit $f.Root @('checkout', '--detach', 'HEAD') | Out-Null }
            else { Invoke-FixtureGit $f.Root @('checkout', '-b', 'feature/not-released') | Out-Null }
            $r = Invoke-LocalRelease $f
            Assert-Rejected $r 'branch|main|detached|symbolic-ref'
            Assert-NoPublishing $r
        }
    }
    Invoke-LocalReleaseCase 'unpushed-source-commit' {
        $f = New-ReleaseFixture 'unpushed'
        $f.Config.RemoteCommit = 'b' * 40
        $r = Invoke-LocalRelease $f
        Assert-Rejected $r 'push|HEAD|commit'
        Assert-NoPublishing $r
    }
    foreach ($stage in @('auth', 'repository-api', 'commit-api', 'tags-api')) {
        Invoke-LocalReleaseCase "github-failure-$stage" {
            $f = New-ReleaseFixture ('api-failure-' + $stage)
            $f.Config.FailureStage = $stage
            $r = Invoke-LocalRelease $f
            Assert-Rejected $r 'GitHub|auth|repository|commit|tag'
            Assert-NoPublishing $r
            Assert-True ((Get-Trace $r 'build.ps1').Count -eq 0) 'GitHub metadata failure reached the build.'
        }
    }
    foreach ($invalid in @($false, $true)) {
        Invoke-LocalReleaseCase "invalid-reference-managed-$invalid" {
            $f = New-ReleaseFixture ('bad-reference-' + $invalid)
            $path = Join-Path $f.Config.RefsRoot 'core/0Harmony.dll'
            if ($invalid) { Write-TestFile $path 'invalid managed DLL' }
            else { [IO.File]::Delete($path) }
            $r = Invoke-LocalRelease $f
            Assert-Rejected $r 'reference|managed|DLL|0Harmony|GetAssemblyName|IL format|BadImage'
            Assert-NoPublishing $r
        }
    }
    foreach ($stage in @('check-public-files.ps1', 'test.ps1', 'DistributionInstallerTests.ps1', 'build.ps1', 'ReferenceArchiveTests.ps1', 'ReleaseAssetsTests.ps1')) {
        Invoke-LocalReleaseCase "failed-validation-$stage" {
            $f = New-ReleaseFixture ('failed-stage-' + $stage)
            $f.Config.FailureStage = $stage
            $r = Invoke-LocalRelease $f
            Assert-Rejected $r ("MOCK_STAGE_FAILURE:" + [regex]::Escape($stage))
            Assert-NoPublishing $r
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $f.Root '.work/releases/pending.json'))) 'A failed build/check became a pending publication.'
        }
    }
    foreach ($mutation in @(@{ At = 'test.ps1'; Kind = 'tracked' }, @{ At = 'build.ps1'; Kind = 'untracked' }, @{ At = 'build.ps1'; Kind = 'commit' })) {
        Invoke-LocalReleaseCase "changed-source-during-$($mutation.At)-$($mutation.Kind)" {
            $f = New-ReleaseFixture ('changed-source-' + $mutation.Kind)
            $f.Config.MutateAt = $mutation.At
            $f.Config.Mutation = $mutation.Kind
            $r = Invoke-LocalRelease $f
            Assert-Rejected $r 'clean|changed|dirty|source|HEAD|commit|tracked'
            Assert-True ((Get-Trace $r $mutation.At).Count -eq 1) 'The requested source mutation stage was not reached.'
            Assert-NoPublishing $r
        }
    }
    Invoke-LocalReleaseCase 'reference-snapshot-survives-source-reference-change' {
        $f = New-ReleaseFixture 'immutable-reference-snapshot'
        $f.Config.MutateAt = 'build.ps1'
        $f.Config.Mutation = 'reference'
        $r = Invoke-LocalRelease $f
        Assert-True ($r.Code -eq 0) $r.Output
        $snapshot = (Get-Trace $r 'build.ps1')[0].data.refsRoot
        Assert-Manifest $f (Get-BuildInfo $f) '1.0.8' $true $snapshot
        $copiedHash = (Get-FileHash -LiteralPath (Join-Path $snapshot 'core/0Harmony.dll') -Algorithm SHA256).Hash
        Assert-True ($copiedHash -cne (Get-FileHash -LiteralPath (Join-Path $f.Config.RefsRoot 'core/0Harmony.dll') -Algorithm SHA256).Hash) 'Test did not mutate the original reference.'
    }
    Invoke-LocalReleaseCase 'changed-reference-snapshot-blocks-publication' {
        $f = New-ReleaseFixture 'changed-reference-snapshot'
        $f.Config.MutateAt = 'build.ps1'
        $f.Config.Mutation = 'snapshot-reference'
        $r = Invoke-LocalRelease $f
        Assert-Rejected $r 'reference.*changed|changed.*reference'
        Assert-True ((Get-Trace $r 'build.ps1').Count -eq 1) 'Reference mutation did not reach the build.'
        Assert-NoPublishing $r
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $f.Root '.work/releases/pending.json'))) 'A modified reference snapshot became a pending publication.'
    }
    foreach ($kind in @('snapshot-installer', 'snapshot-generator')) {
        foreach ($preview in @($false, $true)) {
            Invoke-LocalReleaseCase "changed-$kind-preview-$preview-blocks-publication" {
                $f = New-ReleaseFixture ("cmd-source-$kind-$preview")
                $f.Config.PrepareOnly = $preview
                $f.Config.MutateAt = 'build.ps1'
                $f.Config.Mutation = $kind
                $r = Invoke-LocalRelease $f
                Assert-Rejected $r 'Installer source snapshot'
                Assert-True ((Get-Trace $r 'build.ps1').Count -eq 1) 'Installer source mutation did not reach the build.'
                Assert-NoPublishing $r
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $f.Root '.work/releases/pending.json'))) 'Modified installer source became a pending publication.'
            }
        }
    }
    Invoke-LocalReleaseCase 'concurrent-release-lock' {
        $f = New-ReleaseFixture 'concurrent-lock'
        $lockPath = Join-Path $f.Root '.work/release.lock'
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $r = Invoke-LocalRelease $f
            Assert-Rejected $r 'already running|another.*release'
            Assert-NoPublishing $r
            Assert-True ((Get-Trace $r 'build.ps1').Count -eq 0) 'Concurrent invocation reached the build.'
        }
        finally { $lock.Dispose() }
    }
    Invoke-LocalReleaseCase 'upload-failure-preserves-pending-build-and-assets' {
        $failed = New-FailedUpload 'failed-upload'
        Assert-Manifest $failed.Fixture $failed.Build '1.0.8' $true
        $pending = $failed.Pending
        Assert-True ($pending.schemaVersion -eq 1 -and $pending.repository -ceq 'example/chat-plugin' -and $pending.tag -ceq 'v1.0.8' -and $pending.sourceCommit -ceq $failed.Fixture.Head -and $pending.buildId -match '^[a-f0-9]{32}$') 'Invalid pending release identity.'
    }
    Invoke-LocalReleaseCase 'retry-reuses-identical-tag-build-and-assets' {
        $failed = New-FailedUpload 'retry-upload'
        $beforeInfo = (Get-FileHash -LiteralPath $failed.Build.Path -Algorithm SHA256).Hash
        $failed.Fixture.Config.Tags = @('v1.0.7', 'v1.0.8', 'v1.0.9')
        $failed.Fixture.Config.Bump = 'major'
        $r = Invoke-LocalRelease $failed.Fixture
        Assert-True ($r.Code -eq 0) $r.Output
        Assert-True ((Get-Trace $r 'build.ps1').Count -eq 0 -and (Get-Trace $r 'test.ps1').Count -eq 0) 'Pending retry rebuilt or reran the build pipeline.'
        Assert-True ((Get-Trace $r 'publisher-start').Count -eq 1 -and (Get-Trace $r 'publisher-start')[0].data.tag -ceq 'v1.0.8') 'Pending retry changed the release tag.'
        Assert-True ((Get-Trace $r 'publisher-start')[0].data.recoverPublished) 'Pending retry did not enable safe published-release recovery.'
        Assert-True ((Get-FileHash -LiteralPath $failed.Build.Path -Algorithm SHA256).Hash -ceq $beforeInfo) 'Retry replaced the build provenance.'
        Assert-Manifest $failed.Fixture $failed.Build '1.0.8' $true
        Assert-True (-not (Test-Path -LiteralPath $failed.PendingPath)) 'Successful retry did not clear pending state.'
    }
    Invoke-LocalReleaseCase 'failed-retry-keeps-original-pending-record' {
        $failed = New-FailedUpload 'retry-fails-again'
        $beforeHash = (Get-FileHash -LiteralPath $failed.PendingPath -Algorithm SHA256).Hash
        $beforeWrite = (Get-Item -LiteralPath $failed.PendingPath).LastWriteTimeUtc.Ticks
        $failed.Fixture.Config.FailureStage = 'release-upload'
        $r = Invoke-LocalRelease $failed.Fixture
        Assert-Rejected $r 'upload'
        Assert-True ((Get-Trace $r 'build.ps1').Count -eq 0) 'A failed retry rebuilt the saved plugin.'
        Assert-True ((Get-FileHash -LiteralPath $failed.PendingPath -Algorithm SHA256).Hash -ceq $beforeHash -and (Get-Item -LiteralPath $failed.PendingPath).LastWriteTimeUtc.Ticks -eq $beforeWrite) 'A failed retry rewrote its original pending record.'
        Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $failed.Fixture.Root '.work/releases') -Filter '*.tmp' -File).Count -eq 0) 'Atomic pending writes left temporary files.'
    }
    Invoke-LocalReleaseCase 'pending-explicit-tag-mismatch' {
        $failed = New-FailedUpload 'pending-tag-mismatch'
        $failed.Fixture.Config.Tag = 'v1.0.9'
        $r = Invoke-LocalRelease $failed.Fixture
        Assert-Rejected $r 'pending|tag|retry'
        Assert-NoPublishing $r
        Assert-True (Test-Path -LiteralPath $failed.PendingPath) 'Rejected retry removed pending state.'
    }
    Invoke-LocalReleaseCase 'pending-source-commit-mismatch' {
        $failed = New-FailedUpload 'pending-commit-mismatch'
        Write-TestFile (Join-Path $failed.Fixture.Root 'README.md') 'new source after failed upload'
        Invoke-FixtureGit $failed.Fixture.Root @('add', '--', 'README.md') | Out-Null
        Invoke-FixtureGit $failed.Fixture.Root @('commit', '-m', 'different source after failed upload') | Out-Null
        $failed.Fixture.Config.RemoteCommit = Invoke-FixtureGit $failed.Fixture.Root @('rev-parse', 'HEAD')
        $r = Invoke-LocalRelease $failed.Fixture
        Assert-Rejected $r 'pending|source|commit|retry'
        Assert-NoPublishing $r
        Assert-True (Test-Path -LiteralPath $failed.PendingPath) 'Rejected source mismatch removed pending state.'
    }
    foreach ($field in @('repository', 'buildId')) {
        Invoke-LocalReleaseCase "pending-invalid-$field" {
            $failed = New-FailedUpload ('pending-invalid-' + $field)
            if ($field -ceq 'repository') { $failed.Pending.repository = 'example/different-plugin' }
            else { $failed.Pending.buildId = '../../outside-fixture' }
            Write-TestJson $failed.PendingPath $failed.Pending
            $r = Invoke-LocalRelease $failed.Fixture
            Assert-Rejected $r 'unfinished|pending|source|repository|retry'
            Assert-NoPublishing $r
            Assert-True ((Get-Trace $r 'build.ps1').Count -eq 0) 'Invalid pending metadata started a new build.'
            Assert-True (Test-Path -LiteralPath $failed.PendingPath) 'Rejected pending metadata removed pending state.'
        }
    }
    Invoke-LocalReleaseCase 'preview-keeps-existing-pending-publication-unchanged' {
        $failed = New-FailedUpload 'preview-existing-pending'
        $beforePending = (Get-FileHash -LiteralPath $failed.PendingPath -Algorithm SHA256).Hash
        $beforeRemote = (Get-FileHash -LiteralPath (Join-Path $failed.Fixture.Root '.work/remote.json') -Algorithm SHA256).Hash
        $failed.Fixture.Config.PrepareOnly = $true
        $failed.Fixture.Config.Tag = 'v1.0.8'
        $r = Invoke-LocalRelease $failed.Fixture
        Assert-True ($r.Code -eq 0) $r.Output
        Assert-NoPublishing $r
        Assert-Manifest $failed.Fixture (Get-BuildInfo $failed.Fixture $true) '1.0.8' $false
        Assert-True ((Get-FileHash -LiteralPath $failed.PendingPath -Algorithm SHA256).Hash -ceq $beforePending) 'Preview changed pending publication state.'
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $failed.Fixture.Root '.work/remote.json') -Algorithm SHA256).Hash -ceq $beforeRemote) 'Preview changed fake remote state.'
    }
    foreach ($kind in @('corrupt', 'old-source')) {
        Invoke-LocalReleaseCase "abandon-$kind-pending-without-github-or-source-checks" {
            $failed = New-FailedUpload ('abandon-' + $kind)
            if ($kind -ceq 'corrupt') { Write-TestFile $failed.PendingPath '{ invalid pending json' }
            else {
                Write-TestFile (Join-Path $failed.Fixture.Root 'README.md') 'new local source after failed upload'
                Invoke-FixtureGit $failed.Fixture.Root @('add', '--', 'README.md') | Out-Null
                Invoke-FixtureGit $failed.Fixture.Root @('commit', '-m', 'new source before abandonment') | Out-Null
            }
            Invoke-FixtureGit $failed.Fixture.Root @('remote', 'remove', 'origin') | Out-Null
            $beforePending = (Get-FileHash -LiteralPath $failed.PendingPath -Algorithm SHA256).Hash
            $beforeBuild = (Get-FileHash -LiteralPath $failed.Build.Path -Algorithm SHA256).Hash
            $beforeRemote = (Get-FileHash -LiteralPath (Join-Path $failed.Fixture.Root '.work/remote.json') -Algorithm SHA256).Hash
            $failed.Fixture.Config.AbandonPending = $true
            $failed.Fixture.Config.FailureStage = 'auth'
            $r = Invoke-LocalRelease $failed.Fixture
            Assert-True ($r.Code -eq 0) $r.Output
            Assert-NoPublishing $r
            Assert-True ((Get-Trace $r 'gh-read').Count -eq 0 -and (Get-Trace $r 'build.ps1').Count -eq 0) 'Abandoning pending state contacted GitHub or ran the build.'
            Assert-True (-not (Test-Path -LiteralPath $failed.PendingPath)) 'Abandonment left active pending state.'
            $archives = @(Get-ChildItem -LiteralPath (Join-Path $failed.Fixture.Root '.work/releases') -Filter 'pending-abandoned-*.json' -File)
            Assert-True ($archives.Count -eq 1 -and (Get-FileHash -LiteralPath $archives[0].FullName -Algorithm SHA256).Hash -ceq $beforePending) 'Abandonment did not preserve the original pending bytes.'
            Assert-True ((Get-FileHash -LiteralPath $failed.Build.Path -Algorithm SHA256).Hash -ceq $beforeBuild) 'Abandonment modified preserved build information.'
            Assert-True ((Get-FileHash -LiteralPath (Join-Path $failed.Fixture.Root '.work/remote.json') -Algorithm SHA256).Hash -ceq $beforeRemote) 'Abandonment changed fake remote state.'
            $assetsRoot = (Get-Trace $failed.Result 'publisher-start')[0].data.assetsRoot
            foreach ($asset in $failed.Build.Info.assets) {
                Assert-True ((Get-FileHash -LiteralPath (Join-Path $assetsRoot $asset.file) -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $asset.sha256) 'Abandonment modified the saved release assets.'
            }
        }
    }
    Invoke-LocalReleaseCase 'abandon-without-pending-is-local-noop' {
        $f = New-ReleaseFixture 'abandon-no-pending' -NoCommit
        Invoke-FixtureGit $f.Root @('remote', 'remove', 'origin') | Out-Null
        $f.Config.AbandonPending = $true
        $r = Invoke-LocalRelease $f
        Assert-True ($r.Code -eq 0) $r.Output
        Assert-NoPublishing $r
        Assert-True ((Get-Trace $r 'gh-read').Count -eq 0 -and (Get-Trace $r 'build.ps1').Count -eq 0) 'Abandon no-op contacted GitHub or ran a build.'
    }
    foreach ($name in @('AstralParty.Chat.dll', 'AstralParty.Chat-v1.0.8.zip', 'SHA256SUMS.txt')) {
        Invoke-LocalReleaseCase "pending-tampered-$name" {
            $failed = New-FailedUpload ('tampered-' + $name)
            $assetRoot = (Get-Trace $failed.Result 'publisher-start')[0].data.assetsRoot
            [IO.File]::AppendAllText((Join-Path $assetRoot $name), 'tampered pending artifact')
            $r = Invoke-LocalRelease $failed.Fixture
            Assert-Rejected $r 'checksum|asset|artifact|ZIP'
            Assert-True ((Get-Trace $r 'gh-mutation').Count -eq 0 -and (Get-Trace $r 'publisher-complete').Count -eq 0) 'Tampered pending asset reached a remote mutation.'
            Assert-True ((Get-Trace $r 'build.ps1').Count -eq 0) 'Tampered pending assets were silently rebuilt.'
            Assert-True (Test-Path -LiteralPath $failed.PendingPath) 'Rejected artifact mutation removed pending state.'
        }
    }
    Write-Output "Local release tests passed: $($script:passed) cases."
}
finally {
    $env:PATH = $oldPath
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldTelemetry
    if ($KeepWorkspace) { Write-Output "Local release fixture workspace: $testRoot" }
    elseif (Test-Path -LiteralPath $testRoot) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $boundary = $testParent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe local release test cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

# Expected child failures are test results, not this suite's exit status. Set
# success only after all assertions and cleanup finish; exceptions still fail.
$global:LASTEXITCODE = 0
