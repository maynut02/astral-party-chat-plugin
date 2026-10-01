param([string]$DllPath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $localPwsh = Join-Path $repoRoot '.work\pwsh-7.6.6\pwsh.exe'
    if (-not (Test-Path -LiteralPath $localPwsh -PathType Leaf)) {
        $command = Get-Command pwsh -ErrorAction SilentlyContinue
        if (-not $command) { throw 'PowerShell 7 is required for Release asset checks.' }
        $localPwsh = $command.Source
    }
    $bootstrapArguments = @('-NoProfile', '-File', $PSCommandPath)
    if ($DllPath) { $bootstrapArguments += @('-DllPath', $DllPath) }
    & $localPwsh @bootstrapArguments
    if ($LASTEXITCODE -ne 0) { throw 'Release asset checks failed in PowerShell 7.' }
    return
}
$PSNativeCommandUseErrorActionPreference = $false
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
if (-not $DllPath) { $DllPath = Join-Path $repoRoot 'dist\AstralParty.Chat.dll' }
$DllPath = [IO.Path]::GetFullPath($DllPath)
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($DllPath).ProductVersion
if (-not $version) { throw 'Missing built DLL version. Run scripts/build.ps1 first.' }
$tag = "v$version"
$publisher = Join-Path $repoRoot 'scripts\publish-release.ps1'
. (Join-Path $repoRoot 'scripts\reference-files.ps1')
$referencePaths = @(Get-AstralReferencePaths)
$testParent = Join-Path $repoRoot '.work\release-tests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$passed = 0
try {
    $baseAssets = Join-Path $testRoot 'base'
    & (Join-Path $repoRoot 'scripts\package-release.ps1') -Tag $tag -DllPath $DllPath -OutputRoot $baseAssets
    # Run the actual standalone publisher with a fake gh in a child process.
    # There is no real GitHub invocation, token, tag creation or source mutation.
    $mock = @'
param([string]$Workspace, [string]$Publisher, [string]$Existing, [string]$TagState, [string]$Failure)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $Workspace 'inputs.json') -Raw | ConvertFrom-Json -AsHashtable
$Tag = $config.Tag
$target = $config.SourceCommit
$repository = $config.Repository
$global:tagCreated = $false
$global:notesGenerated = $false
$global:releaseCreated = $false
$global:assetsUploaded = $false
$global:tagReads = 0
$logPath = Join-Path $Workspace 'gh-calls.txt'
function Trace-Call([string]$Operation) { [IO.File]::AppendAllText($logPath, "$Operation`n") }
function gh {
    if ($args[0] -ceq 'api') {
        $hostIndex = [Array]::IndexOf($args, '--hostname')
        if ($hostIndex -lt 0 -or $args[$hostIndex + 1] -cne 'github.com') { throw 'Missing explicit GitHub API host.' }
        $endpoints = @($args | Where-Object { "$_" -clike "repos/$repository/*" })
        if ($endpoints.Count -ne 1) { throw 'Incorrect GitHub API repository.' }
        $endpoint = $endpoints[0]
        if ($endpoint -match '/(releases|tags)\?per_page=100$') {
            if ('--paginate' -cnotin $args -or '--slurp' -cnotin $args) { throw 'Missing metadata pagination.' }
            $kind = if ($endpoint -match '/releases\?') { 'releases' } else { 'tags' }
            Trace-Call "list-$kind"
            if ($Failure -eq "list-$kind") { $global:LASTEXITCODE = 1; return }
            $global:LASTEXITCODE = 0
            if ($Failure -eq 'malformed-list') { return '{"not":"a page list"}' }
            if ($kind -eq 'releases') {
                if ($Existing -eq 'missing') { return '[[]]' }
                # Target lives in page two, not the first page.
                return '[[],[' + (@{ tag_name = $Tag; draft = ($Existing -ne 'published'); id = 42 } | ConvertTo-Json -Compress) + ']]'
            }
            if ($TagState -eq 'missing' -and -not $global:tagCreated) { return '[[]]' }
            return '[[],[' + (@{ name = $Tag } | ConvertTo-Json -Compress) + ']]'
        }
        if ($endpoint -ceq "repos/$repository/releases/42/assets?per_page=100") {
            Trace-Call 'list-published-assets'
            if ('--paginate' -cnotin $args -or '--slurp' -cnotin $args) { throw 'Missing published asset pagination.' }
            if ($Failure -eq 'recovery-list') { $global:LASTEXITCODE = 1; return }
            $names = @('AstralParty.Chat.dll', "AstralParty.Chat-$Tag.zip", 'SHA256SUMS.txt')
            if ($Failure -eq 'recovery-extra') { $names += 'private.zip' }
            if ($Failure -eq 'recovery-missing') { $names = @('AstralParty.Chat.dll') }
            $rows = @($names | ForEach-Object {
                $localPath = Join-Path $config.AssetsRoot $_
                $size = if (Test-Path -LiteralPath $localPath) { (Get-Item -LiteralPath $localPath).Length } else { 1 }
                $hash = if (Test-Path -LiteralPath $localPath) { (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { 'd' * 64 }
                $asset = @{ name = $_; size = $size; digest = "sha256:$hash" }
                if ($Failure -eq 'recovery-bad-digest') { $asset.digest = 'sha256:' + ('b' * 64) }
                if ($Failure -eq 'recovery-malformed-digest') { $asset.digest = 'sha256:not-a-hash' }
                if ($Failure -eq 'recovery-size-mismatch') { $asset.size++ }
                if ($Failure -in @('recovery-missing-digest', 'recovery-content-mismatch', 'recovery-download')) { $asset.digest = $null }
                if ($Failure -eq 'recovery-no-digest-property') { $asset.Remove('digest') }
                if ($Failure -eq 'recovery-mixed-digest' -and $_ -ceq 'SHA256SUMS.txt') { $asset.digest = $null }
                $asset
            })
            $global:LASTEXITCODE = 0
            return '[' + (ConvertTo-Json -InputObject $rows -Depth 4 -Compress) + ']'
        }
        if ($endpoint -match '/releases/generate-notes$') {
            if ('POST' -cnotin $args -or "tag_name=$Tag" -cnotin $args -or "target_commitish=$target" -cnotin $args) { throw 'Incorrect generated notes source.' }
            Trace-Call 'generate-notes'
            if ($Failure -eq 'generate-notes') { $global:LASTEXITCODE = 1; return }
            $global:notesGenerated = $true
            $global:LASTEXITCODE = 0
            if ($Failure -eq 'invalid-notes') { return '{"body":42}' }
            return (@{ body = "## What's Changed`n`n* Current generated changes" } | ConvertTo-Json -Compress)
        }
        if ('POST' -cin $args) {
            if ($endpoint -cne "repos/$repository/git/refs" -or $TagState -ne 'missing') { throw 'Unexpected tag creation.' }
            if ("ref=refs/tags/$Tag" -cnotin $args -or "sha=$target" -cnotin $args) { throw 'Incorrect tag target.' }
            Trace-Call 'create-tag'
            if ($Failure -eq 'create-tag') { $global:LASTEXITCODE = 1; return }
            $global:tagCreated = $true
            $global:LASTEXITCODE = 0
            return
        }
        if ($endpoint -match '/git/ref/tags/') {
            $global:tagReads++
            Trace-Call 'read-tag'
            if ($Failure -eq 'read-tag') { $global:LASTEXITCODE = 1; return }
            $global:LASTEXITCODE = 0
            if ($TagState -in @('annotated', 'invalid-annotated', 'annotated-loop')) {
                $sha = if ($TagState -eq 'invalid-annotated') { '../injected' } else { 'c' * 40 }
                return (@{ object = @{ type = 'tag'; sha = $sha } } | ConvertTo-Json -Compress)
            }
            $sha = if ($TagState -eq 'wrong-commit' -or ($Failure -eq 'tag-moved' -and $global:tagReads -gt 1)) { 'b' * 40 } else { $target }
            return (@{ object = @{ type = 'commit'; sha = $sha } } | ConvertTo-Json -Compress)
        }
        if ($endpoint -match '/git/tags/') {
            Trace-Call 'read-annotated-tag'
            if ($Failure -eq 'annotated-tag') { $global:LASTEXITCODE = 1; return }
            $global:LASTEXITCODE = 0
            $object = if ($TagState -eq 'annotated-loop') { @{ type = 'tag'; sha = 'c' * 40 } } else { @{ type = 'commit'; sha = $target } }
            return (@{ object = $object } | ConvertTo-Json -Compress)
        }
        throw 'Unexpected mock API operation.'
    }
    if ($args[0] -cne 'release') { throw 'Unexpected mock CLI operation.' }
    $repoIndex = [Array]::IndexOf($args, '--repo')
    if ($repoIndex -lt 0 -or $args[$repoIndex + 1] -cne "github.com/$repository") { throw 'Missing explicit Release repository.' }
    $operation = $args[1]
    if ($operation -eq 'download') {
        Trace-Call 'download'
        if ($Failure -eq 'recovery-download') { $global:LASTEXITCODE = 1; return }
        $patternIndex = [Array]::IndexOf($args, '--pattern')
        $directoryIndex = [Array]::IndexOf($args, '--dir')
        if ($patternIndex -lt 0 -or $directoryIndex -lt 0) { throw 'Incomplete verification download flags.' }
        $name = $args[$patternIndex + 1]
        if ($name -cnotin @('AstralParty.Chat.dll', "AstralParty.Chat-$Tag.zip", 'SHA256SUMS.txt')) { throw 'Unsafe recovery download pattern.' }
        $destination = Join-Path $args[$directoryIndex + 1] $name
        Copy-Item -LiteralPath (Join-Path $config.AssetsRoot $name) -Destination $destination
        if ($Failure -eq 'recovery-content-mismatch') {
            $bytes = [IO.File]::ReadAllBytes($destination)
            $bytes[0] = $bytes[0] -bxor 1
            [IO.File]::WriteAllBytes($destination, $bytes)
        }
        $global:LASTEXITCODE = 0
        return
    }
    if ($operation -eq 'view') {
        Trace-Call 'view'
        if (($global:assetsUploaded -and $Failure -eq 'verify-upload') -or (-not $global:assetsUploaded -and $Failure -eq 'view-draft')) { $global:LASTEXITCODE = 1; return }
        if ($Existing -eq 'missing' -and -not $global:releaseCreated) { throw 'Publisher guessed absence using release view.' }
        $global:LASTEXITCODE = 0
        if ($Failure -eq 'published-before-upload' -and $global:releaseCreated -and -not $global:assetsUploaded) { return '{"isDraft":false,"assets":[]}' }
        if ($Failure -eq 'remote-published' -and $global:assetsUploaded) { return '{"isDraft":false,"assets":[]}' }
        $names = @()
        if ($global:assetsUploaded -or $Existing -eq 'draft-complete') { $names = @('AstralParty.Chat.dll', "AstralParty.Chat-$Tag.zip", 'SHA256SUMS.txt') }
        if ($Existing -eq 'draft-partial') { $names = @('AstralParty.Chat.dll') }
        if ($Existing -eq 'draft-extra' -or ($global:assetsUploaded -and $Failure -eq 'remote-extra')) { $names += 'private-reference.zip' }
        if ($global:assetsUploaded -and $Failure -eq 'remote-missing') { $names = @('AstralParty.Chat.dll') }
        if ($global:assetsUploaded -and $Failure -eq 'remote-duplicate') { $names = @('AstralParty.Chat.dll', 'AstralParty.Chat.dll', 'SHA256SUMS.txt') }
        return (@{ isDraft = $true; body = 'old-checksum-still-in-draft'; assets = @($names | ForEach-Object { @{ name = $_ } }) } | ConvertTo-Json -Depth 4 -Compress)
    }
    if ($operation -notin @('create', 'upload', 'edit')) { throw 'Unexpected mock release operation.' }
    Trace-Call $operation
    if ($operation -in @('create', 'edit')) {
        $titleIndex = [Array]::IndexOf($args, '--title')
        if ($titleIndex -lt 0 -or $args[$titleIndex + 1] -cne "ChatPlugin $Tag") { throw 'Incorrect Release title.' }
        $notesIndex = [Array]::IndexOf($args, '--notes-file')
        if ($notesIndex -lt 0 -or -not $global:notesGenerated) { throw 'Missing regenerated Release notes.' }
        $notes = [IO.File]::ReadAllText($args[$notesIndex + 1])
        if (-not $notes.Contains("ChatPlugin $Tag") -or -not $notes.Contains("AstralParty.Chat-$Tag.zip") -or -not $notes.Contains('SHA256SUMS.txt') -or -not $notes.Contains("https://github.com/$repository/commit/$target") -or $notes.Contains('/actions/runs/')) { throw 'Incomplete local Release notes.' }
        if (-not $notes.Contains('Current generated changes') -or $notes.Contains('old-checksum-still-in-draft')) { throw 'Draft description was not refreshed.' }
        $info = Get-Content -LiteralPath $config.BuildInfoPath -Raw | ConvertFrom-Json
        if (-not $notes.Contains($info.sdkVersion) -or -not $notes.Contains('2026-10-01T00:00:00')) { throw 'Missing local build provenance.' }
        foreach ($reference in $info.references) {
            if (-not $notes.Contains($reference.file) -or -not $notes.Contains($reference.assemblyVersion) -or -not $notes.Contains($reference.sha256)) { throw 'Missing reference build provenance.' }
        }
        foreach ($checksum in Get-Content -LiteralPath (Join-Path $Workspace 'release-assets/SHA256SUMS.txt')) {
            if (-not $notes.Contains(($checksum -split '  ')[0])) { throw 'Release notes checksum mismatch.' }
        }
    }
    if ($operation -eq 'create') {
        $targetIndex = [Array]::IndexOf($args, '--target')
        if ($targetIndex -lt 0 -or $args[$targetIndex + 1] -cne $target -or '--verify-tag' -cnotin $args -or '--draft' -cnotin $args) { throw 'Incorrect draft Release source or flags.' }
    }
    if ($operation -eq 'upload') {
        if ('--clobber' -cnotin $args) { throw 'Missing draft retry upload flag.' }
        $paths = @($args | Where-Object { Test-Path -LiteralPath "$_" -PathType Leaf })
        if ($paths.Count -ne 3) { throw 'Incorrect upload asset count.' }
        foreach ($path in $paths) { if ([IO.Path]::GetFileName($path) -cnotin @('AstralParty.Chat.dll', "AstralParty.Chat-$Tag.zip", 'SHA256SUMS.txt')) { throw 'Unexpected uploaded file.' } }
    }
    if ($operation -eq 'edit' -and '--draft=false' -cnotin $args) { throw 'Missing final publication flag.' }
    if ($Failure -eq $operation) { $global:LASTEXITCODE = 1; return }
    if ($operation -eq 'create') { $global:releaseCreated = $true }
    if ($operation -eq 'upload') { $global:assetsUploaded = $true }
    $global:LASTEXITCODE = 0
}
& $Publisher @config
'@
    $mockPath = Join-Path $testRoot 'mock-publish.ps1'
    [IO.File]::WriteAllText($mockPath, $mock, [Text.UTF8Encoding]::new($false))
    function Update-Manifest([string]$Assets) {
        $lines = foreach ($name in @('AstralParty.Chat.dll', "AstralParty.Chat-$tag.zip")) {
            (Get-FileHash -LiteralPath (Join-Path $Assets $name) -Algorithm SHA256).Hash.ToLowerInvariant() + "  $name"
        }
        [IO.File]::WriteAllLines((Join-Path $Assets 'SHA256SUMS.txt'), [string[]]$lines)
    }
    function Invoke-ReleaseCase([string]$Name, [scriptblock]$Mutation = {}, [bool]$ShouldPass = $true, [string]$Existing = 'missing', [string]$TagState = 'matching', [string]$Failure = '', [scriptblock]$InfoMutation = {}, [hashtable]$Inputs = @{}, [string]$ExpectedError = '') {
        $workspace = Join-Path $testRoot $Name
        $assets = Join-Path $workspace 'release-assets'
        New-Item -ItemType Directory -Path $assets -Force | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $baseAssets) { Copy-Item -LiteralPath $file.FullName -Destination $assets }
        & $Mutation $assets
        $info = [pscustomobject]@{
            schemaVersion = 1; repository = 'example/chat-plugin'; tag = $tag; version = $version
            sourceCommit = 'a' * 40; sourceClean = $true; builtAtUtc = '2026-10-01T00:00:00.0000000Z'; sdkVersion = '6.0.428'
            references = @($referencePaths | ForEach-Object { [pscustomobject]@{ file = $_; assemblyVersion = '6.0.0.0'; fileVersion = ''; sha256 = 'c' * 64 } })
            assets = @(@('AstralParty.Chat.dll', "AstralParty.Chat-$tag.zip") | ForEach-Object { [pscustomobject]@{ file = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $assets $_) -Algorithm SHA256).Hash.ToLowerInvariant() } })
        }
        & $InfoMutation $info
        $buildInfoPath = Join-Path $workspace 'build-info.json'
        [IO.File]::WriteAllText($buildInfoPath, ($info | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        $config = @{ Repository = 'example/chat-plugin'; Tag = $tag; SourceCommit = 'a' * 40; AssetsRoot = $assets; BuildInfoPath = $buildInfoPath; NotesPath = (Join-Path $workspace 'notes.md') }
        foreach ($key in $Inputs.Keys) { $config[$key] = $Inputs[$key] }
        if ($Failure -eq 'notes-in-assets') { $config.NotesPath = Join-Path $assets 'notes.md' }
        if ($Failure -eq 'notes-overwrite-info') { $config.NotesPath = $buildInfoPath }
        if ($Failure -eq 'version-tag-mismatch') {
            $config.Tag = if ($tag -ceq 'v0.0.0') { 'v0.0.1' } else { 'v0.0.0' }
            $differentZip = "AstralParty.Chat-$($config.Tag).zip"
            Move-Item -LiteralPath (Join-Path $assets "AstralParty.Chat-$tag.zip") -Destination (Join-Path $assets $differentZip)
            $lines = foreach ($assetName in @('AstralParty.Chat.dll', $differentZip)) { (Get-FileHash -LiteralPath (Join-Path $assets $assetName) -Algorithm SHA256).Hash.ToLowerInvariant() + "  $assetName" }
            [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS.txt'), [string[]]$lines)
        }
        [IO.File]::WriteAllText((Join-Path $workspace 'inputs.json'), ($config | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        $output = @(& $pwshExecutable -NoProfile -File $mockPath -Workspace $workspace -Publisher $publisher -Existing $Existing -TagState $TagState -Failure $Failure 2>&1)
        $code = $LASTEXITCODE
        $calls = if (Test-Path -LiteralPath (Join-Path $workspace 'gh-calls.txt')) { @(Get-Content -LiteralPath (Join-Path $workspace 'gh-calls.txt')) } else { @() }
        $recovering = $config.ContainsKey('RecoverPublished') -and $config.RecoverPublished
        $published = ('edit' -cin $calls -and $Failure -ne 'edit') -or ($recovering -and $Existing -eq 'published' -and $code -eq 0)
        if (($code -eq 0) -ne $ShouldPass -or $published -ne $ShouldPass -or (-not $ShouldPass -and (-not $ExpectedError -or ($output -join "`n") -notmatch $ExpectedError))) {
            $output | Write-Output
            throw "Release case failed: $Name (exit=$code, published=$published)"
        }
        if (-not $ShouldPass -and $Failure -ne 'edit' -and 'edit' -cin $calls) { throw "Failed case attempted publication: $Name" }
        if ($Failure -in @('list-releases', 'list-tags', 'view-draft', 'malformed-list') -and @($calls | Where-Object { $_ -cin @('create-tag', 'create', 'upload', 'edit') }).Count) { throw "Metadata failure caused mutation: $Name" }
        if ($ShouldPass -and $TagState -eq 'missing' -and 'create-tag' -cnotin $calls) { throw "Missing tag was not created: $Name" }
        if ($TagState -ne 'missing' -and 'create-tag' -cin $calls) { throw "Existing tag was replaced: $Name" }
        if ($Failure -eq 'published-before-upload' -and 'upload' -cin $calls) { throw "Published Release upload was attempted: $Name" }
        if ($recovering -and $Existing -eq 'published' -and @($calls | Where-Object { $_ -cin @('create-tag', 'create', 'upload', 'edit', 'generate-notes') }).Count) { throw "Published recovery mutated the remote: $Name" }
        if ($ShouldPass -and $Failure -in @('recovery-missing-digest', 'recovery-no-digest-property', 'recovery-mixed-digest') -and @($calls | Where-Object { $_ -ceq 'download' }).Count -ne 3) { throw "Recovery did not download all three assets: $Name" }
        $script:passed++
        Write-Output "PASS release: $Name"
    }
    Invoke-ReleaseCase 'valid-assets'
    Invoke-ReleaseCase 'draft-retry-refreshes-description' -Existing 'draft'
    Invoke-ReleaseCase 'complete-draft-retry' -Existing 'draft-complete'
    Invoke-ReleaseCase 'partial-draft-retry' -Existing 'draft-partial'
    Invoke-ReleaseCase 'existing-draft-with-private-extra-is-blocked' -Existing 'draft-extra' -ShouldPass $false -ExpectedError 'Draft Release contains unexpected assets'
    Invoke-ReleaseCase 'published-release-protected' -Existing 'published' -ShouldPass $false -ExpectedError 'Release already published'
    Invoke-ReleaseCase 'published-recovery-matching-digests' -Existing 'published' -Inputs @{ RecoverPublished = $true }
    Invoke-ReleaseCase 'published-recovery-missing-digests-downloads-all' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-missing-digest'
    Invoke-ReleaseCase 'published-recovery-absent-digest-property-downloads-all' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-no-digest-property'
    Invoke-ReleaseCase 'published-recovery-mixed-digests-downloads-all' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-mixed-digest'
    Invoke-ReleaseCase 'published-recovery-bad-digest-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-bad-digest' -ShouldPass $false -ExpectedError 'Published Release asset checksum differs'
    Invoke-ReleaseCase 'published-recovery-malformed-digest-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-malformed-digest' -ShouldPass $false -ExpectedError 'Published Release asset checksum differs'
    Invoke-ReleaseCase 'published-recovery-content-mismatch-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-content-mismatch' -ShouldPass $false -ExpectedError 'Downloaded published asset differs'
    Invoke-ReleaseCase 'published-recovery-size-mismatch-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-size-mismatch' -ShouldPass $false -ExpectedError 'Published Release asset size differs'
    Invoke-ReleaseCase 'published-recovery-extra-asset-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-extra' -ShouldPass $false -ExpectedError 'exactly the required assets'
    Invoke-ReleaseCase 'published-recovery-missing-asset-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-missing' -ShouldPass $false -ExpectedError 'exactly the required assets'
    Invoke-ReleaseCase 'published-recovery-list-error-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-list' -ShouldPass $false -ExpectedError 'Failed to read published assets'
    Invoke-ReleaseCase 'published-recovery-download-error-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'recovery-download' -ShouldPass $false -ExpectedError 'Failed to download published assets'
    Invoke-ReleaseCase 'published-recovery-wrong-source-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -TagState 'wrong-commit' -ShouldPass $false -ExpectedError 'different source commit'
    Invoke-ReleaseCase 'published-recovery-concurrently-moved-tag-is-blocked' -Existing 'published' -Inputs @{ RecoverPublished = $true } -Failure 'tag-moved' -ShouldPass $false -ExpectedError 'different source commit'
    Invoke-ReleaseCase 'local-release-creates-missing-tag' -TagState 'missing'
    Invoke-ReleaseCase 'existing-tag-wrong-commit' -TagState 'wrong-commit' -ShouldPass $false -ExpectedError 'different source commit'
    Invoke-ReleaseCase 'draft-tag-wrong-commit' -Existing 'draft' -TagState 'wrong-commit' -ShouldPass $false -ExpectedError 'different source commit'
    Invoke-ReleaseCase 'annotated-tag' -TagState 'annotated'
    Invoke-ReleaseCase 'invalid-annotated-tag' -TagState 'invalid-annotated' -ShouldPass $false -ExpectedError 'Invalid annotated tag object'
    Invoke-ReleaseCase 'annotated-tag-loop' -TagState 'annotated-loop' -ShouldPass $false -ExpectedError 'different source commit'
    Invoke-ReleaseCase 'tag-creation-failed' -TagState 'missing' -Failure 'create-tag' -ShouldPass $false -ExpectedError 'Failed to create release tag'
    Invoke-ReleaseCase 'tag-read-failed' -Failure 'read-tag' -ShouldPass $false -ExpectedError 'Failed to verify release tag'
    Invoke-ReleaseCase 'annotated-tag-read-failed' -TagState 'annotated' -Failure 'annotated-tag' -ShouldPass $false -ExpectedError 'Failed to resolve annotated tag'
    Invoke-ReleaseCase 'draft-creation-failed' -Failure 'create' -ShouldPass $false -ExpectedError 'Failed to create draft Release'
    Invoke-ReleaseCase 'notes-generation-failed' -Failure 'generate-notes' -ShouldPass $false -ExpectedError 'Failed to generate release change notes'
    Invoke-ReleaseCase 'invalid-notes-response' -Failure 'invalid-notes' -ShouldPass $false -ExpectedError 'Invalid generated release notes response'
    Invoke-ReleaseCase 'upload-failed-keeps-draft' -Existing 'draft' -Failure 'upload' -ShouldPass $false -ExpectedError 'Asset upload failed; Release remains a draft'
    Invoke-ReleaseCase 'extra-remote-asset-before-publish-is-blocked' -Failure 'remote-extra' -ShouldPass $false -ExpectedError 'Draft Release contains unexpected assets'
    Invoke-ReleaseCase 'missing-remote-asset-before-publish-is-blocked' -Failure 'remote-missing' -ShouldPass $false -ExpectedError 'Draft Release is missing required assets'
    Invoke-ReleaseCase 'duplicate-remote-asset-is-blocked' -Failure 'remote-duplicate' -ShouldPass $false -ExpectedError 'Draft Release contains unexpected assets'
    Invoke-ReleaseCase 'upload-verification-failure-keeps-draft' -Failure 'verify-upload' -ShouldPass $false -ExpectedError 'Failed to verify uploaded Release assets'
    Invoke-ReleaseCase 'concurrently-published-release-is-protected' -Failure 'remote-published' -ShouldPass $false -ExpectedError 'Release already published'
    Invoke-ReleaseCase 'published-before-upload-is-protected' -Failure 'published-before-upload' -ShouldPass $false -ExpectedError 'Release already published'
    Invoke-ReleaseCase 'concurrently-moved-tag-is-blocked' -Failure 'tag-moved' -ShouldPass $false -ExpectedError 'different source commit'
    Invoke-ReleaseCase 'publication-failed' -Failure 'edit' -ShouldPass $false -ExpectedError 'Failed to publish Release'
    Invoke-ReleaseCase 'release-list-error-does-not-create-release' -Failure 'list-releases' -ShouldPass $false -ExpectedError 'Failed to read releases'
    Invoke-ReleaseCase 'tag-list-error-does-not-create-tag' -Failure 'list-tags' -ShouldPass $false -ExpectedError 'Failed to read tags'
    Invoke-ReleaseCase 'draft-inspection-error-does-not-create-release' -Existing 'draft' -Failure 'view-draft' -ShouldPass $false -ExpectedError 'Failed to inspect existing draft'
    Invoke-ReleaseCase 'malformed-metadata-list' -Failure 'malformed-list' -ShouldPass $false -ExpectedError 'Invalid GitHub releases list'
    Invoke-ReleaseCase 'invalid-repository' -Inputs @{ Repository = 'example/../private' } -ShouldPass $false -ExpectedError 'Invalid GitHub repository'
    Invoke-ReleaseCase 'invalid-tag' -Inputs @{ Tag = 'v01.2.3' } -ShouldPass $false -ExpectedError 'Invalid release tag'
    Invoke-ReleaseCase 'invalid-source-commit' -Inputs @{ SourceCommit = 'A' * 40 } -ShouldPass $false -ExpectedError 'Invalid release source commit'
    Invoke-ReleaseCase 'dll-version-must-match-tag' -Failure 'version-tag-mismatch' -ShouldPass $false -ExpectedError 'Release tag and built DLL version differ'
    Invoke-ReleaseCase 'notes-output-cannot-be-an-asset' -Failure 'notes-in-assets' -ShouldPass $false -ExpectedError 'Release notes must be outside'
    Invoke-ReleaseCase 'notes-output-cannot-overwrite-build-info' -Failure 'notes-overwrite-info' -ShouldPass $false -ExpectedError 'Release notes must be outside'
    foreach ($field in @('repository', 'tag', 'version', 'sourceCommit')) {
        $mutateField = { param($info) $info.$field = 'mismatched' }.GetNewClosure()
        Invoke-ReleaseCase "build-$field-mismatch" -InfoMutation $mutateField -ShouldPass $false -ExpectedError 'Build information does not match'
    }
    Invoke-ReleaseCase 'dirty-build-is-blocked' -InfoMutation { param($info) $info.sourceClean = $false } -ShouldPass $false -ExpectedError 'clean committed source'
    Invoke-ReleaseCase 'string-clean-flag-is-blocked' -InfoMutation { param($info) $info.sourceClean = 'true' } -ShouldPass $false -ExpectedError 'clean committed source'
    Invoke-ReleaseCase 'unknown-build-schema' -InfoMutation { param($info) $info.schemaVersion = 2 } -ShouldPass $false -ExpectedError 'Unsupported build information schema'
    Invoke-ReleaseCase 'extra-build-metadata-is-blocked' -InfoMutation { param($info) $info | Add-Member NoteProperty secret 'not-published' } -ShouldPass $false -ExpectedError 'Invalid build information fields'
    Invoke-ReleaseCase 'sdk-markup-is-blocked' -InfoMutation { param($info) $info.sdkVersion = '[SDK](https://example.invalid)' } -ShouldPass $false -ExpectedError 'Invalid build SDK version'
    Invoke-ReleaseCase 'timestamp-markup-is-blocked' -InfoMutation { param($info) $info.builtAtUtc = '2026-10-01<script>' } -ShouldPass $false -ExpectedError 'Invalid UTC build timestamp'
    Invoke-ReleaseCase 'invalid-timestamp-date-is-blocked' -InfoMutation { param($info) $info.builtAtUtc = '2026-13-01T00:00:00Z' } -ShouldPass $false -ExpectedError 'Invalid UTC build timestamp'
    Invoke-ReleaseCase 'missing-reference-is-blocked' -InfoMutation { param($info) $info.references = @($info.references | Select-Object -Skip 1) } -ShouldPass $false -ExpectedError 'Invalid reference assembly list'
    Invoke-ReleaseCase 'duplicate-reference-is-blocked' -InfoMutation { param($info) $info.references[1] = $info.references[0] } -ShouldPass $false -ExpectedError 'Unexpected or duplicate reference assembly'
    Invoke-ReleaseCase 'reference-path-injection-is-blocked' -InfoMutation { param($info) $info.references[0].file = '[file](https://example.invalid)' } -ShouldPass $false -ExpectedError 'Unexpected or duplicate reference assembly'
    Invoke-ReleaseCase 'reference-version-injection-is-blocked' -InfoMutation { param($info) $info.references[0].assemblyVersion = '1.0.0.0 | https://example.invalid' } -ShouldPass $false -ExpectedError 'Invalid reference assembly version'
    Invoke-ReleaseCase 'reference-fileversion-injection-is-blocked' -InfoMutation { param($info) $info.references[0].fileVersion = '<script>bad</script>' } -ShouldPass $false -ExpectedError 'Invalid reference file version'
    Invoke-ReleaseCase 'reference-hash-injection-is-blocked' -InfoMutation { param($info) $info.references[0].sha256 = 'https://example.invalid' } -ShouldPass $false -ExpectedError 'Invalid reference assembly checksum'
    Invoke-ReleaseCase 'null-reference-fileversion-is-allowed' -InfoMutation { param($info) $info.references[0].fileVersion = $null }
    Invoke-ReleaseCase 'prerelease-fileversion-is-allowed' -InfoMutation { param($info) $info.references[0].fileVersion = '6.0.0.0-be.788+abcdef' }
    Invoke-ReleaseCase 'missing-build-asset-is-blocked' -InfoMutation { param($info) $info.assets = @($info.assets[0]) } -ShouldPass $false -ExpectedError 'Invalid build asset list'
    Invoke-ReleaseCase 'duplicate-build-asset-is-blocked' -InfoMutation { param($info) $info.assets[1] = $info.assets[0] } -ShouldPass $false -ExpectedError 'Unexpected or duplicate build asset'
    Invoke-ReleaseCase 'extra-build-asset-is-blocked' -InfoMutation { param($info) $info.assets += [pscustomobject]@{ file = 'private.zip'; sha256 = 'c' * 64 } } -ShouldPass $false -ExpectedError 'Invalid build asset list'
    Invoke-ReleaseCase 'build-asset-hash-mismatch' -InfoMutation { param($info) $info.assets[0].sha256 = 'd' * 64 } -ShouldPass $false -ExpectedError 'Build information asset checksum mismatch'
    Invoke-ReleaseCase 'tampered-dll' -Mutation { param($assets) [IO.File]::AppendAllText((Join-Path $assets 'AstralParty.Chat.dll'), 'tampered') } -ShouldPass $false -ExpectedError 'Release asset checksum mismatch'
    Invoke-ReleaseCase 'private-file-in-artifact' -Mutation { param($assets) [IO.File]::WriteAllText((Join-Path $assets 'private-reference.dll'), 'unexpected') } -ShouldPass $false -ExpectedError 'Unexpected release artifact contents'
    Invoke-ReleaseCase 'manifest-path-traversal' -Mutation {
        param($assets)
        $badLines = @(
            (('a' * 64) + '  ../outside.dll')
            (('b' * 64) + "  AstralParty.Chat-$tag.zip")
        )
        [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS.txt'), [string[]]$badLines)
    } -ShouldPass $false -ExpectedError 'Unexpected checksum filename'
    Invoke-ReleaseCase 'duplicate-checksum-entry' -Mutation {
        param($assets)
        $line = @(Get-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt'))[0]
        [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS.txt'), @($line, $line))
    } -ShouldPass $false -ExpectedError 'Unexpected checksum filename'
    Invoke-ReleaseCase 'zip-with-private-member' -Mutation {
        param($assets)
        $zip = [IO.Compression.ZipFile]::Open((Join-Path $assets "AstralParty.Chat-$tag.zip"), [IO.Compression.ZipArchiveMode]::Update)
        try { $zip.CreateEntry('core/BepInEx.Core.dll') | Out-Null } finally { $zip.Dispose() }
        Update-Manifest $assets
    } -ShouldPass $false -ExpectedError 'Unexpected install ZIP contents'
    Invoke-ReleaseCase 'zip-with-duplicate-plugin' -Mutation {
        param($assets)
        $zip = [IO.Compression.ZipFile]::Open((Join-Path $assets "AstralParty.Chat-$tag.zip"), [IO.Compression.ZipArchiveMode]::Update)
        try {
            $zip.GetEntry('INSTALL.txt').Delete()
            $entry = $zip.CreateEntry('BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll')
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('duplicate') } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
        Update-Manifest $assets
    } -ShouldPass $false -ExpectedError 'Unexpected install ZIP contents'
    Invoke-ReleaseCase 'zip-plugin-differs-from-standalone' -Mutation {
        param($assets)
        $zip = [IO.Compression.ZipFile]::Open((Join-Path $assets "AstralParty.Chat-$tag.zip"), [IO.Compression.ZipArchiveMode]::Update)
        try {
            $zip.GetEntry('BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll').Delete()
            $entry = $zip.CreateEntry('BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll')
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('different DLL') } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
        Update-Manifest $assets
    } -ShouldPass $false -ExpectedError 'ZIP and standalone plugin differ'
    Write-Output "Release asset checks passed: $passed"
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $boundary = [IO.Path]::GetFullPath($testParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
