param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $repoRoot 'dist\AstralParty.Chat.dll')).ProductVersion
if (-not $version) { throw 'Missing built DLL version. Run scripts/build.ps1 first.' }
$tag = "v$version"
$testParent = Join-Path $repoRoot '.work\release-tests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$passed = 0
try {
    $baseAssets = Join-Path $testRoot 'base'
    & (Join-Path $repoRoot 'scripts\package-release.ps1') -Tag $tag -OutputRoot $baseAssets
    # Exercise the actual privileged job's script, with gh shadowed by a function.
    # No token, remote checkout or GitHub API is used by these checks.
    $yaml = [IO.File]::ReadAllText((Join-Path $repoRoot '.github\workflows\release.yml'))
    $marker = '        run: |'
    $block = $yaml.Substring($yaml.LastIndexOf($marker) + $marker.Length).TrimStart("`r", "`n")
    $publishScript = (($block -split "`r?`n" | ForEach-Object { if ($_ -match '^          (.*)$') { $Matches[1] } }) -join "`n")
    $mock = @'
param([string]$Workspace, [string]$Tag, [string]$Existing, [string]$TagState, [string]$Event, [string]$Failure)
$ErrorActionPreference = 'Stop'
$env:GITHUB_WORKSPACE = $Workspace
$env:RELEASE_TAG = $Tag
$env:RELEASE_SHA = 'a' * 40
$env:RELEASE_EVENT = $Event
$env:GH_REPO = 'example/chat-plugin'
$env:GITHUB_RUN_ID = '12345'
$env:RUNNER_TEMP = $Workspace
$env:GH_TOKEN = ''
$script:tagCreated = $false
$script:notesGenerated = $false
$script:releaseCreated = $false
$script:assetsUploaded = $false
function gh {
    if ($args[0] -eq 'api') {
        if (@($args | Where-Object { "$_" -match '/releases/generate-notes$' }).Count) {
            if ('POST' -cnotin $args -or 'tag_name=' + $Tag -cnotin $args -or 'target_commitish=' + $env:RELEASE_SHA -cnotin $args) { throw 'Incorrect generated notes source.' }
            if ($Failure -eq 'generate-notes') { $global:LASTEXITCODE = 1; return }
            $script:notesGenerated = $true
            $global:LASTEXITCODE = 0
            return (@{ body = "## What's Changed`n`n* Current generated changes" } | ConvertTo-Json -Compress)
        }
        if ('POST' -cin $args) {
            if ($TagState -ne 'missing' -or $Event -ne 'workflow_dispatch') { throw 'Unexpected tag creation.' }
            if ('ref=refs/tags/' + $Tag -cnotin $args -or 'sha=' + $env:RELEASE_SHA -cnotin $args) { throw 'Incorrect tag target.' }
            if ($Failure -eq 'create-tag') { $global:LASTEXITCODE = 1; return }
            $script:tagCreated = $true
            $global:LASTEXITCODE = 0
            Write-Output 'MOCK_CALL:create-tag'
            return
        }
        if ($TagState -eq 'missing' -and -not $script:tagCreated) { $global:LASTEXITCODE = 1; return }
        $global:LASTEXITCODE = 0
        $sha = if ($TagState -eq 'wrong-commit') { 'b' * 40 } else { $env:RELEASE_SHA }
        if ($TagState -eq 'annotated' -and "$($args[1])" -match '/git/ref/tags/') {
            return (@{ object = @{ type = 'tag'; sha = 'c' * 40 } } | ConvertTo-Json -Compress)
        }
        return (@{ object = @{ type = 'commit'; sha = $sha } } | ConvertTo-Json -Compress)
    }
    $operation = $args[1]
    if ($operation -eq 'view') {
        if ($Existing -eq 'missing' -and -not $script:releaseCreated) { $global:LASTEXITCODE = 1; return }
        if ($script:assetsUploaded -and $Failure -eq 'verify-upload') { $global:LASTEXITCODE = 1; return }
        $global:LASTEXITCODE = 0
        if ($Existing -eq 'published') { return '{"isDraft":false}' }
        $names = @()
        if ($script:assetsUploaded -or $Existing -eq 'draft-complete') {
            $names = @('AstralParty.Chat.dll', "AstralParty.Chat-$Tag.zip", 'SHA256SUMS.txt')
        }
        if ($Existing -eq 'draft-extra' -or ($script:assetsUploaded -and $Failure -eq 'remote-extra')) { $names += 'private-reference.zip' }
        if ($script:assetsUploaded -and $Failure -eq 'remote-missing') { $names = @('AstralParty.Chat.dll') }
        $assetMetadata = @($names | ForEach-Object { @{ name = $_ } })
        # A previous draft may contain checksums from a different build.
        return (@{ isDraft = $true; body = 'old-checksum-still-in-draft'; assets = $assetMetadata } | ConvertTo-Json -Depth 4 -Compress)
    }
    if ($operation -notin @('create', 'upload', 'edit')) { throw 'Unexpected mock operation.' }
    if ($operation -in @('create', 'edit')) {
        $titleIndex = [Array]::IndexOf($args, '--title')
        if ($titleIndex -lt 0 -or $args[$titleIndex + 1] -cne "ChatPlugin $Tag") { throw 'Incorrect Release title.' }
    }
    if ($operation -eq 'create') {
        $targetIndex = [Array]::IndexOf($args, '--target')
        if ($targetIndex -lt 0 -or $args[$targetIndex + 1] -cne $env:RELEASE_SHA) { throw 'Incorrect Release source commit.' }
        if ('--verify-tag' -cnotin $args) { throw 'Missing tag verification.' }
    }
    if ($operation -in @('create', 'edit')) {
        $notesIndex = [Array]::IndexOf($args, '--notes-file')
        if ($notesIndex -lt 0 -or -not $script:notesGenerated) { throw 'Missing regenerated Release notes.' }
        $notes = [IO.File]::ReadAllText($args[$notesIndex + 1])
        if (-not $notes.Contains("ChatPlugin $Tag") -or -not $notes.Contains("AstralParty.Chat-$Tag.zip") -or -not $notes.Contains('SHA256SUMS.txt') -or -not $notes.Contains('/actions/runs/12345')) { throw 'Incomplete Release notes.' }
        if (-not $notes.Contains('Current generated changes') -or $notes.Contains('old-checksum-still-in-draft')) { throw 'Draft description was not refreshed.' }
        $checksums = Get-Content (Join-Path $Workspace 'release-assets/SHA256SUMS.txt')
        foreach ($checksum in $checksums) {
            if (-not $notes.Contains(($checksum -split '  ')[0])) { throw 'Release notes checksum mismatch.' }
        }
    }
    if ($Failure -eq $operation) {
        Write-Output "MOCK_FAILURE:$operation"
        $global:LASTEXITCODE = 1
        return
    }
    if ($operation -eq 'create') { $script:releaseCreated = $true }
    if ($operation -eq 'upload') { $script:assetsUploaded = $true }
    Write-Output "MOCK_CALL:$operation"
    $global:LASTEXITCODE = 0
}
'@
    $mockPath = Join-Path $testRoot 'publish.ps1'
    [IO.File]::WriteAllText($mockPath, $mock + "`n" + $publishScript)
    function Invoke-ReleaseCase([string]$Name, [scriptblock]$Mutation, [bool]$ShouldPass, [string]$Existing = 'missing', [string]$TagState = 'matching', [string]$Event = 'push', [string]$Failure = '') {
        $workspace = Join-Path $testRoot $Name
        $assets = Join-Path $workspace 'release-assets'
        New-Item -ItemType Directory -Path $assets -Force | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $baseAssets) { Copy-Item -LiteralPath $file.FullName -Destination $assets }
        & $Mutation $assets
        $output = @(& pwsh -NoProfile -File $mockPath -Workspace $workspace -Tag $tag -Existing $Existing -TagState $TagState -Event $Event -Failure $Failure 2>&1)
        $code = $LASTEXITCODE
        $published = @($output | Where-Object { "$_" -eq 'MOCK_CALL:edit' }).Count -gt 0
        if (($code -eq 0) -ne $ShouldPass -or $published -ne $ShouldPass) {
            $output | Write-Output
            throw "Release case failed: $Name (exit=$code, published=$published)"
        }
        $script:passed++
        Write-Output "PASS release: $Name"
    }
    Invoke-ReleaseCase 'valid-assets' {} $true
    Invoke-ReleaseCase 'draft-retry-refreshes-description' {} $true 'draft'
    Invoke-ReleaseCase 'complete-draft-retry' {} $true 'draft-complete'
    Invoke-ReleaseCase 'existing-draft-with-private-extra-is-blocked' {} $false 'draft-extra'
    Invoke-ReleaseCase 'published-release-protected' {} $false 'published'
    Invoke-ReleaseCase 'manual-release-creates-tag' {} $true 'missing' 'missing' 'workflow_dispatch'
    Invoke-ReleaseCase 'pushed-tag-missing' {} $false 'missing' 'missing'
    Invoke-ReleaseCase 'existing-tag-wrong-commit' {} $false 'missing' 'wrong-commit' 'workflow_dispatch'
    Invoke-ReleaseCase 'draft-tag-wrong-commit' {} $false 'draft' 'wrong-commit' 'workflow_dispatch'
    Invoke-ReleaseCase 'annotated-tag' {} $true 'missing' 'annotated'
    Invoke-ReleaseCase 'tag-creation-failed' {} $false 'missing' 'missing' 'workflow_dispatch' 'create-tag'
    Invoke-ReleaseCase 'draft-creation-failed' {} $false 'missing' 'matching' 'push' 'create'
    Invoke-ReleaseCase 'notes-generation-failed' {} $false 'missing' 'matching' 'push' 'generate-notes'
    Invoke-ReleaseCase 'upload-failed-keeps-draft' {} $false 'draft' 'matching' 'push' 'upload'
    Invoke-ReleaseCase 'extra-remote-asset-before-publish-is-blocked' {} $false 'missing' 'matching' 'push' 'remote-extra'
    Invoke-ReleaseCase 'missing-remote-asset-before-publish-is-blocked' {} $false 'missing' 'matching' 'push' 'remote-missing'
    Invoke-ReleaseCase 'upload-verification-failure-keeps-draft' {} $false 'missing' 'matching' 'push' 'verify-upload'
    Invoke-ReleaseCase 'tampered-dll' {
        param($assets)
        [IO.File]::AppendAllText((Join-Path $assets 'AstralParty.Chat.dll'), 'tampered')
    } $false
    Invoke-ReleaseCase 'private-file-in-artifact' {
        param($assets)
        [IO.File]::WriteAllText((Join-Path $assets 'private-reference.dll'), 'unexpected')
    } $false
    Invoke-ReleaseCase 'manifest-path-traversal' {
        param($assets)
        [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS.txt'), @(
            ('a' * 64) + '  ../outside.dll', ('b' * 64) + "  AstralParty.Chat-$tag.zip"
        ))
    } $false
    Invoke-ReleaseCase 'zip-with-private-member' {
        param($assets)
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zipName = "AstralParty.Chat-$tag.zip"
        $zipPath = Join-Path $assets $zipName
        $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
        try { $zip.CreateEntry('core/BepInEx.Core.dll') | Out-Null }
        finally { $zip.Dispose() }
        # Recalculate the manifest: ZIP member validation must still block it.
        $lines = foreach ($name in @('AstralParty.Chat.dll', $zipName)) {
            (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant() + "  $name"
        }
        [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS.txt'), [string[]]$lines)
    } $false
    Invoke-ReleaseCase 'zip-with-duplicate-plugin' {
        param($assets)
        $zipName = "AstralParty.Chat-$tag.zip"
        $zip = [IO.Compression.ZipFile]::Open((Join-Path $assets $zipName), [IO.Compression.ZipArchiveMode]::Update)
        try {
            $zip.GetEntry('INSTALL.txt').Delete()
            $entry = $zip.CreateEntry('BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll')
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('duplicate') }
            finally { $writer.Dispose() }
        }
        finally { $zip.Dispose() }
        $lines = foreach ($name in @('AstralParty.Chat.dll', $zipName)) {
            (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant() + "  $name"
        }
        [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS.txt'), [string[]]$lines)
    } $false
    Invoke-ReleaseCase 'zip-plugin-differs-from-standalone' {
        param($assets)
        $zipName = "AstralParty.Chat-$tag.zip"
        $zip = [IO.Compression.ZipFile]::Open((Join-Path $assets $zipName), [IO.Compression.ZipArchiveMode]::Update)
        try {
            $zip.GetEntry('BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll').Delete()
            $entry = $zip.CreateEntry('BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll')
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('different DLL') }
            finally { $writer.Dispose() }
        }
        finally { $zip.Dispose() }
        $lines = foreach ($name in @('AstralParty.Chat.dll', $zipName)) {
            (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant() + "  $name"
        }
        [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS.txt'), [string[]]$lines)
    } $false
    Write-Output "Release asset checks passed: $passed"
}
finally {
    # Delete only this invocation's checked directory under the ignored workspace.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $boundary = [IO.Path]::GetFullPath($testParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
