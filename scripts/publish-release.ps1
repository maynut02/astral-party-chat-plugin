#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$AssetsRoot,
    [Parameter(Mandatory = $true)][string]$BuildInfoPath,
    [Parameter(Mandatory = $true)][string]$NotesPath,
    [switch]$RecoverPublished
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
if ($Repository -cnotmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$' -or $Repository.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Invalid GitHub repository; use owner/name without a URL or .git suffix.'
}
if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid release tag.' }
if ($SourceCommit -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid release source commit.' }
$version = $Tag.Substring(1)
$title = "ChatPlugin $Tag"
$root = [IO.Path]::GetFullPath($AssetsRoot)
$BuildInfoPath = [IO.Path]::GetFullPath($BuildInfoPath)
$NotesPath = [IO.Path]::GetFullPath($NotesPath)
$boundary = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($NotesPath.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or $NotesPath.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $NotesPath.Equals($BuildInfoPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release notes must be outside the asset directory and must not overwrite build information.'
}
$zipName = "AstralParty.Chat-$Tag.zip"
$expected = @('AstralParty.Chat.dll', $zipName, 'SHA256SUMS.txt')
$files = @(Get-ChildItem -LiteralPath $root -Force)
if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint -or $files.Count -ne 3 -or @($files | Where-Object {
    $_.PSIsContainer -or $_.Name -cnotin $expected -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
}).Count) { throw 'Unexpected release artifact contents.' }
$checksums = @(Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt'))
if ($checksums.Count -ne 2) { throw 'Invalid checksum manifest.' }
$hashes = @{}
foreach ($line in $checksums) {
    $match = [regex]::Match($line, '^([a-f0-9]{64})  (.+)$')
    if (-not $match.Success) { throw 'Invalid checksum entry.' }
    $name = $match.Groups[2].Value
    if ($name -cnotin @('AstralParty.Chat.dll', $zipName) -or $hashes.ContainsKey($name)) { throw 'Unexpected checksum filename.' }
    $hash = (Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $match.Groups[1].Value) { throw 'Release asset checksum mismatch.' }
    $hashes[$name] = $hash
}
# Read archive members and assembly metadata without extracting or loading the DLL.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $root $zipName))
try {
    $members = @($zip.Entries.FullName)
    $dllMember = 'BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll'
    if ($members.Count -ne 2 -or @($members | Select-Object -Unique).Count -ne 2 -or @($members | Where-Object { $_ -cnotin @($dllMember, 'INSTALL.txt') }).Count) {
        throw 'Unexpected install ZIP contents.'
    }
    $stream = $zip.GetEntry($dllMember).Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $zipDllHash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
    if ($zipDllHash -cne $hashes['AstralParty.Chat.dll']) { throw 'ZIP and standalone plugin differ.' }
}
finally { $zip.Dispose() }
$dllPath = Join-Path $root 'AstralParty.Chat.dll'
if ([Reflection.AssemblyName]::GetAssemblyName($dllPath).Name -cne 'AstralParty.Chat') { throw 'Unexpected plugin assembly.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath).ProductVersion -cne $version) { throw 'Release tag and built DLL version differ.' }

function Assert-Fields([object]$Value, [string[]]$Fields, [string]$Context) {
    if ($Value -isnot [pscustomobject]) { throw "Invalid $Context metadata." }
    $names = @($Value.PSObject.Properties.Name)
    if ($names.Count -ne $Fields.Count -or @($names | Where-Object { $_ -cnotin $Fields }).Count) { throw "Invalid $Context fields." }
}
$infoJson = [IO.File]::ReadAllText($BuildInfoPath)
$info = ConvertFrom-Json -InputObject $infoJson -Depth 20 -NoEnumerate
Assert-Fields $info @('schemaVersion', 'repository', 'tag', 'version', 'sourceCommit', 'sourceClean', 'builtAtUtc', 'sdkVersion', 'references', 'assets') 'build information'
# ConvertFrom-Json can turn ISO timestamps into DateTime objects. Preserve the
# JSON string explicitly so validation behaves the same on PowerShell 7 releases.
$document = [System.Text.Json.JsonDocument]::Parse($infoJson)
try {
    $timestamp = $document.RootElement.GetProperty('builtAtUtc')
    if ($timestamp.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw 'Invalid UTC build timestamp.' }
    $info.builtAtUtc = $timestamp.GetString()
}
finally { $document.Dispose() }
if ($info.schemaVersion -isnot [long] -or $info.schemaVersion -ne 1) { throw 'Unsupported build information schema.' }
if ($info.repository -isnot [string] -or $info.repository -cne $Repository -or $info.tag -isnot [string] -or $info.tag -cne $Tag -or $info.version -isnot [string] -or $info.version -cne $version -or $info.sourceCommit -isnot [string] -or $info.sourceCommit -cne $SourceCommit) {
    throw 'Build information does not match the release repository, tag, version or source commit.'
}
if ($info.sourceClean -isnot [bool] -or -not $info.sourceClean) { throw 'Release build must use clean committed source.' }
$semver = '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?'
if ($info.sdkVersion -isnot [string] -or $info.sdkVersion.Length -gt 100 -or $info.sdkVersion -cnotmatch ('^' + $semver + '$')) { throw 'Invalid build SDK version.' }
$builtAt = [DateTimeOffset]::MinValue
if ($info.builtAtUtc -isnot [string] -or $info.builtAtUtc -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$' -or -not [DateTimeOffset]::TryParse($info.builtAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$builtAt)) {
    throw 'Invalid UTC build timestamp.'
}
. (Join-Path $PSScriptRoot 'reference-files.ps1')
$referencePaths = @(Get-AstralReferencePaths)
if ($info.references -isnot [Array] -or $info.references.Count -ne $referencePaths.Count) { throw 'Invalid reference assembly list.' }
$referenceByPath = @{}
foreach ($reference in $info.references) {
    Assert-Fields $reference @('file', 'assemblyVersion', 'fileVersion', 'sha256') 'reference assembly'
    if ($reference.file -isnot [string] -or $reference.file -cnotin $referencePaths -or $referenceByPath.ContainsKey($reference.file)) { throw 'Unexpected or duplicate reference assembly.' }
    if ($reference.assemblyVersion -isnot [string] -or $reference.assemblyVersion.Length -gt 100 -or $reference.assemblyVersion -cnotmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid reference assembly version.' }
    # Generated IL2CPP interop assemblies may have no FileVersion resource.
    if ($null -ne $reference.fileVersion -and ($reference.fileVersion -isnot [string] -or $reference.fileVersion.Length -gt 100 -or ($reference.fileVersion -and $reference.fileVersion -cnotmatch '^\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$'))) { throw 'Invalid reference file version.' }
    if ($reference.sha256 -isnot [string] -or $reference.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid reference assembly checksum.' }
    $referenceByPath[$reference.file] = $reference
}
if ($info.assets -isnot [Array] -or $info.assets.Count -ne 2) { throw 'Invalid build asset list.' }
$seenAssets = @()
foreach ($asset in $info.assets) {
    Assert-Fields $asset @('file', 'sha256') 'build asset'
    if ($asset.file -isnot [string] -or $asset.file -cnotin @('AstralParty.Chat.dll', $zipName) -or $asset.file -cin $seenAssets) { throw 'Unexpected or duplicate build asset.' }
    if ($asset.sha256 -isnot [string] -or $asset.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $asset.sha256 -cne $hashes[$asset.file]) { throw 'Build information asset checksum mismatch.' }
    $seenAssets += $asset.file
}

function Invoke-GhJson([string[]]$Arguments, [string]$FailureMessage) {
    $result = @(& gh @Arguments)
    if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
    try { ConvertFrom-Json -InputObject ($result -join "`n") -Depth 30 -NoEnumerate }
    catch { throw "Invalid GitHub JSON response: $FailureMessage" }
}
function Get-GhList([string]$Endpoint, [string]$Context) {
    $pages = Invoke-GhJson @('api', '--hostname', 'github.com', '--paginate', '--slurp', $Endpoint) "Failed to read $Context; no release was published."
    if ($pages -isnot [Array] -or $pages.Count -eq 0) { throw "Invalid GitHub $Context list." }
    foreach ($page in $pages) {
        if ($page -isnot [Array]) { throw "Invalid GitHub $Context page." }
        foreach ($item in $page) {
            if ($item -isnot [pscustomobject]) { throw "Invalid GitHub $Context entry." }
            $item
        }
    }
}
function Assert-DraftReleaseAssets([object]$Release, [switch]$Complete) {
    if ($Release.isDraft -isnot [bool] -or -not $Release.isDraft) { throw 'Release already published. Create a new version tag.' }
    if ($Release.assets -isnot [Array]) { throw 'Invalid draft Release asset metadata.' }
    $names = @($Release.assets | ForEach-Object { $_.name })
    if ($names.Count -gt 3 -or @($names | Where-Object { $_ -isnot [string] -or $_ -cnotin $expected }).Count -or @($names | Select-Object -Unique).Count -ne $names.Count) { throw 'Draft Release contains unexpected assets; remove them before retrying.' }
    if ($Complete -and $names.Count -ne 3) { throw 'Draft Release is missing required assets.' }
}
function Assert-RemoteTag {
    $ref = Invoke-GhJson @('api', '--hostname', 'github.com', "repos/$Repository/git/ref/tags/$Tag") 'Failed to verify release tag.'
    $object = $ref.object
    for ($depth = 0; $object.type -ceq 'tag' -and $depth -lt 5; $depth++) {
        if ($object.sha -isnot [string] -or $object.sha -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid annotated tag object.' }
        $resolved = Invoke-GhJson @('api', '--hostname', 'github.com', "repos/$Repository/git/tags/$($object.sha)") 'Failed to resolve annotated tag.'
        $object = $resolved.object
    }
    if ($object.type -cne 'commit' -or $object.sha -isnot [string] -or $object.sha -cne $SourceCommit) { throw 'Release tag points to a different source commit.' }
}
function Confirm-PublishedRelease([object]$Release) {
    if ($Release.draft -isnot [bool] -or $Release.draft) { throw 'Invalid published Release metadata.' }
    if ($Release.id -isnot [long] -or $Release.id -le 0) { throw 'Invalid published Release identifier.' }
    Assert-RemoteTag
    $remoteAssets = @(Get-GhList "repos/$Repository/releases/$($Release.id)/assets?per_page=100" 'published assets')
    $names = @($remoteAssets | ForEach-Object { $_.name })
    if ($names.Count -ne 3 -or @($names | Where-Object { $_ -isnot [string] -or $_ -cnotin $expected }).Count -or @($names | Select-Object -Unique).Count -ne 3) { throw 'Published Release does not contain exactly the required assets.' }
    $downloadAssets = @()
    foreach ($asset in $remoteAssets) {
        $localPath = Join-Path $root $asset.name
        if ($asset.size -isnot [long] -or $asset.size -ne (Get-Item -LiteralPath $localPath).Length) { throw 'Published Release asset size differs from the local build.' }
        $localHash = (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $digestProperty = $asset.PSObject.Properties['digest']
        $digest = if ($null -ne $digestProperty) { $digestProperty.Value } else { $null }
        if ($null -ne $digest -and $digest -cne '') {
            if ($digest -isnot [string] -or $digest -cnotmatch '^sha256:[a-f0-9]{64}$' -or $digest.Substring(7) -cne $localHash) { throw 'Published Release asset checksum differs from the local build.' }
        }
        else { $downloadAssets += $asset.name }
    }
    if ($downloadAssets.Count) {
        # If any digest is unavailable, verify the complete three-file set by
        # downloading it rather than trusting a mix of old and new metadata.
        $downloadAssets = $expected
        $temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $downloadRoot = Join-Path $temporaryParent ('astral-release-verify-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($downloadRoot) | Out-Null
        try {
            foreach ($name in $downloadAssets) {
                & gh release download $Tag --repo $ghRepository --pattern $name --dir $downloadRoot
                if ($LASTEXITCODE -ne 0) { throw 'Failed to download published assets for recovery verification.' }
            }
            $downloads = @(Get-ChildItem -LiteralPath $downloadRoot -Force)
            if ($downloads.Count -ne $downloadAssets.Count -or @($downloads | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $downloadAssets -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count) { throw 'Unexpected downloaded recovery assets.' }
            foreach ($name in $downloadAssets) {
                $downloaded = Join-Path $downloadRoot $name
                $localPath = Join-Path $root $name
                if ((Get-Item -LiteralPath $downloaded).Length -ne (Get-Item -LiteralPath $localPath).Length -or (Get-FileHash -LiteralPath $downloaded -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash) { throw 'Downloaded published asset differs from the local build.' }
            }
        }
        finally {
            $resolved = [IO.Path]::GetFullPath($downloadRoot)
            $temporaryBoundary = $temporaryParent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if (-not $resolved.StartsWith($temporaryBoundary, [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe recovery download cleanup path.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    Assert-RemoteTag
}
$ghRepository = "github.com/$Repository"
# Successful paginated lists establish absence. Authentication/network errors must
# never be treated as an absent tag or draft based only on gh's exit code.
$releases = @(Get-GhList "repos/$Repository/releases?per_page=100" 'releases')
$matchingReleases = @($releases | Where-Object { $_.tag_name -ceq $Tag })
if ($matchingReleases.Count -gt 1) { throw 'Multiple Releases use the requested tag.' }
$hasDraft = $matchingReleases.Count -eq 1
if ($hasDraft) {
    if ($matchingReleases[0].draft -isnot [bool]) { throw 'Invalid Release draft metadata.' }
    if (-not $matchingReleases[0].draft) {
        if (-not $RecoverPublished) { throw 'Release already published. Create a new version tag.' }
        Confirm-PublishedRelease $matchingReleases[0]
        Write-Output "Published Release verified without changes: https://github.com/$Repository/releases/tag/$Tag"
        return
    }
    $existing = Invoke-GhJson @('release', 'view', $Tag, '--repo', $ghRepository, '--json', 'isDraft,assets') 'Failed to inspect existing draft Release.'
    Assert-DraftReleaseAssets $existing
}
$tags = @(Get-GhList "repos/$Repository/tags?per_page=100" 'tags')
if (@($tags | Where-Object { $_.name -ceq $Tag }).Count -eq 0) {
    & gh api --hostname github.com --method POST "repos/$Repository/git/refs" -f "ref=refs/tags/$Tag" -f "sha=$SourceCommit" > $null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create release tag; no existing tag was overwritten.' }
}
Assert-RemoteTag
$generated = Invoke-GhJson @('api', '--hostname', 'github.com', '--method', 'POST', "repos/$Repository/releases/generate-notes", '-f', "tag_name=$Tag", '-f', "target_commitish=$SourceCommit") 'Failed to generate release change notes.'
if ($generated.body -isnot [string]) { throw 'Invalid generated release notes response.' }
$referenceRows = foreach ($file in $referencePaths) {
    $reference = $referenceByPath[$file]
    $fileVersion = if ([string]::IsNullOrEmpty($reference.fileVersion)) { '-' } else { $reference.fileVersion }
    '| ' + $file + ' | ' + $reference.assemblyVersion + ' | ' + $fileVersion + ' | ' + $reference.sha256 + ' |'
}
$notes = @(
    "$title - Astral Party의 BepInEx IL2CPP 채팅 플러그인입니다.",
    '', '## 설치', '',
    "1. $zipName 파일을 내려받아 압축을 풉니다.",
    '2. BepInEx가 설치된 게임 폴더에 압축파일의 내용을 넣습니다.',
    '3. 게임을 실행합니다. 기존 DLL만 교체하려면 AstralParty.Chat.dll을 BepInEx/plugins/AstralPartyChat/에 넣습니다.',
    '', '## 파일 확인', '',
    "- AstralParty.Chat.dll SHA-256: $($hashes['AstralParty.Chat.dll'])",
    "- $zipName SHA-256: $($hashes[$zipName])",
    '- SHA256SUMS.txt에서 두 파일의 체크섬을 확인할 수 있습니다.',
    '', '## 빌드', '',
    "- 소스: https://github.com/$Repository/commit/$SourceCommit",
    '- 커밋된 소스와 게임 참조 DLL을 사용하여 로컬에서 빌드했습니다.',
    "- .NET SDK: $($info.sdkVersion)",
    "- 빌드 시각 (UTC): $($info.builtAtUtc)",
    '', '### 참조 DLL', '',
    '| 파일 | AssemblyVersion | FileVersion | SHA-256 |',
    '| --- | --- | --- | --- |',
    ($referenceRows -join "`n"), '', $generated.body
) -join "`n"
$notesDirectory = [IO.Path]::GetDirectoryName($NotesPath)
[IO.Directory]::CreateDirectory($notesDirectory) | Out-Null
[IO.File]::WriteAllText($NotesPath, $notes, [Text.UTF8Encoding]::new($false))
if (-not $hasDraft) {
    & gh release create $Tag --repo $ghRepository --verify-tag --target $SourceCommit --draft --title $title --notes-file $NotesPath
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create draft Release.' }
}
# Notes generation and draft creation can take time. Recheck immediately before
# clobbering draft files in case someone published or added another asset meanwhile.
$beforeUpload = Invoke-GhJson @('release', 'view', $Tag, '--repo', $ghRepository, '--json', 'isDraft,assets') 'Failed to verify draft before uploading assets.'
Assert-DraftReleaseAssets $beforeUpload
$assets = @($expected | ForEach-Object { Join-Path $root $_ })
& gh release upload $Tag --repo $ghRepository @assets --clobber
if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed; Release remains a draft.' }
$uploaded = Invoke-GhJson @('release', 'view', $Tag, '--repo', $ghRepository, '--json', 'isDraft,assets') 'Failed to verify uploaded Release assets; Release remains a draft.'
Assert-DraftReleaseAssets $uploaded -Complete
Assert-RemoteTag
& gh release edit $Tag --repo $ghRepository --title $title --notes-file $NotesPath --draft=false
if ($LASTEXITCODE -ne 0) { throw 'Failed to publish Release.' }
Write-Output "Release published: https://github.com/$Repository/releases/tag/$Tag"
