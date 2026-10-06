[CmdletBinding()]
param(
    [string]$Tag = '',
    [string]$AssetRoot = '',
    [string]$NotesFile = '',
    [switch]$Draft,
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'project-version.ps1')
. (Join-Path $PSScriptRoot 'release-notes.ps1')
$version = Get-AstralProjectVersion -Root $repoRoot
if (-not $Tag) { $Tag = "v$version" }
if ((ConvertTo-AstralVersion $Tag -Tag) -cne $version) { throw 'Upload tag must match VERSION.' }
$git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source

function Invoke-UploadCommand {
    param([string]$Executable, [string[]]$Arguments, [switch]$AllowNotFound)
    $savedEncoding = [Console]::OutputEncoding
    $savedPreference = $ErrorActionPreference
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $ErrorActionPreference = 'Continue'
        $output = @(& $Executable @Arguments 2>&1)
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
        [Console]::OutputEncoding = $savedEncoding
    }
    $message = $output -join "`n"
    if ($code -ne 0) {
        if ($AllowNotFound -and $message -match '\(HTTP 404\)') { return $null }
        throw "Command failed ($($Arguments[0])): $message"
    }
    return $message
}
function Invoke-UploadGit([string[]]$Arguments) {
    Invoke-UploadCommand -Executable $git -Arguments (@('-C', $repoRoot) + $Arguments)
}
function Get-UploadApi([string]$Endpoint, [switch]$AllowNotFound, [switch]$Paginate) {
    $arguments = @('api', $Endpoint, '--hostname', 'github.com', '--method', 'GET')
    if ($Paginate) { $arguments += @('--paginate', '--slurp') }
    $result = Invoke-UploadCommand -Executable $gh -Arguments $arguments -AllowNotFound:$AllowNotFound
    if ($null -eq $result) { return $null }
    return ConvertFrom-Json -InputObject $result
}

$top = Invoke-UploadGit @('rev-parse', '--show-toplevel')
if ([IO.Path]::GetFullPath($top).TrimEnd('\', '/') -ine $repoRoot.TrimEnd('\', '/')) { throw 'Upload must run from this Git repository.' }
if (Invoke-UploadGit @('status', '--porcelain=v1', '--untracked-files=normal')) { throw 'Commit source changes before uploading; build from that clean commit and push it first.' }
$sourceCommit = Invoke-UploadGit @('rev-parse', '--verify', 'HEAD^{commit}')
$origin = Invoke-UploadGit @('remote', 'get-url', 'origin')
$match = [regex]::Match($origin, '\A(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?\z', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if (-not $match.Success) { throw 'origin must identify a github.com repository using HTTPS or SSH.' }
$repository = $match.Groups['repository'].Value
$selector = "github.com/$repository"

if (-not $AssetRoot) { $AssetRoot = Join-Path $repoRoot "dist/release/$Tag" }
$AssetRoot = [IO.Path]::GetFullPath($AssetRoot)
$zipName = "AstralPartyChatPlugin-$Tag.zip"
$names = @($zipName, 'SHA256SUMS.txt')
$hashes = @{}
foreach ($name in $names) {
    $path = Join-Path $AssetRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release asset: $name. Run scripts/package-release.ps1 first." }
    $hashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest = [IO.File]::ReadAllLines((Join-Path $AssetRoot 'SHA256SUMS.txt'))
if ($manifest.Count -ne 1 -or $manifest[0] -cne "$($hashes[$zipName])  $zipName") {
    throw 'Release checksum must match the ZIP and contain only that ZIP. Re-run scripts/package-release.ps1.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $AssetRoot $zipName))
try {
    if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne 'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll') {
        throw 'Release ZIP must contain only the plugin DLL at its installation path.'
    }
} finally { $archive.Dispose() }
if ($NotesFile) {
    $NotesFile = [IO.Path]::GetFullPath($NotesFile)
    if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw 'Missing release notes file.' }
}
else {
    $preparedNotes = Join-Path $repoRoot ".work/releases/$Tag/release-notes.md"
    if (Test-Path -LiteralPath $preparedNotes -PathType Leaf) { $NotesFile = $preparedNotes }
}
if ($NotesFile) {
    $notesContent = [IO.File]::ReadAllText($NotesFile, [Text.Encoding]::UTF8)
}
else {
    if ((Invoke-UploadGit @('rev-parse', '--is-shallow-repository')) -eq 'true') {
        throw 'Automatic release notes require full Git history. Fetch the complete history and release tags first.'
    }
    $tags = Invoke-UploadGit @('tag', '--merged', $sourceCommit, '--list', 'v*')
    $previousTags = @($tags -split "`n" | ForEach-Object {
        try {
            $number = [Version](ConvertTo-AstralVersion $_ -Tag)
            if ($number -lt [Version]$version) { [pscustomobject]@{ Tag = $_; Number = $number } }
        } catch { } # Ignore unrelated or out-of-range tag names.
    } | Sort-Object Number -Descending)
    $range = if ($previousTags.Count) { "$($previousTags[0].Tag)..$sourceCommit" } else { $sourceCommit }
    $log = Invoke-UploadGit @('-c', 'i18n.logOutputEncoding=utf-8', 'log', '--no-merges', '--reverse', '-z', '--format=%H%x00%B', $range, '--')
    $commits = @(ConvertFrom-AstralCommitLog -Log $log)
    $notesContent = New-AstralReleaseNotes -Tag $Tag -Commits $commits
}
if ($Preview) {
    [pscustomobject]@{ Repository = $repository; Tag = $Tag; Title = $Tag; SourceCommit = $sourceCommit; Assets = $names; AssetRoot = $AssetRoot; Notes = if ($NotesFile) { $NotesFile } else { 'Git commit history' }; NotesContent = $notesContent; Draft = [bool]$Draft; Preview = $true }
    return
}
$ghCommand = Get-Command gh -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ghCommand) { throw 'Install GitHub CLI and run gh auth login first.' }
$gh = $ghCommand.Source
Invoke-UploadCommand $gh @('auth', 'status', '--hostname', 'github.com') | Out-Null
$remoteRepository = Get-UploadApi "repos/$repository"
if ($remoteRepository.full_name -ine $repository -or $remoteRepository.permissions.push -eq $false) { throw 'GitHub repository does not match origin or lacks write permission.' }
$remoteCommit = Get-UploadApi "repos/$repository/commits/$sourceCommit" -AllowNotFound
if (-not $remoteCommit -or $remoteCommit.sha -cne $sourceCommit) { throw 'Source commit is not on GitHub. Push the built source commit first.' }
$remoteTag = Get-UploadApi "repos/$repository/git/ref/tags/$Tag" -AllowNotFound
if ($remoteTag) {
    $target = $remoteTag.object
    for ($depth = 0; $target.type -eq 'tag' -and $depth -lt 8; $depth++) {
        $target = (Get-UploadApi "repos/$repository/git/tags/$($target.sha)").object
    }
    if ($target.type -cne 'commit' -or $target.sha -cne $sourceCommit) { throw 'Existing GitHub tag points to a different source commit.' }
}
# Listing releases includes drafts, which the published-release-by-tag endpoint omits.
$pages = Get-UploadApi "repos/$repository/releases?per_page=100" -Paginate
$releases = @(foreach ($page in $pages) { foreach ($item in $page) { if ($item.tag_name -ceq $Tag) { $item } } })
if ($releases.Count -gt 1) { throw 'Multiple releases use this tag. Resolve them on GitHub before uploading.' }
$release = if ($releases.Count) { $releases[0] } else { $null }
if ($release -and -not $remoteTag) {
    if (-not $release.draft -or $release.target_commitish -cne $sourceCommit) { throw 'Existing release does not identify the built source commit.' }
}

$snapshotParent = Join-Path $repoRoot '.work/upload-release'
$snapshotRoot = Join-Path $snapshotParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $snapshotRoot | Out-Null
try {
    foreach ($name in $names) {
        $copy = Join-Path $snapshotRoot $name
        Copy-Item -LiteralPath (Join-Path $AssetRoot $name) -Destination $copy
        if ((Get-FileHash -LiteralPath $copy).Hash.ToLowerInvariant() -cne $hashes[$name]) { throw 'Release assets changed during upload preparation.' }
    }
    $missing = @()
    foreach ($name in $names) {
        $existing = @($release.assets | Where-Object { $_.name -ceq $name })
        if ($existing.Count -gt 1) { throw "Duplicate release asset: $name" }
        if (-not $existing.Count) { $missing += (Join-Path $snapshotRoot $name); continue }
        $remoteHash = $existing[0].digest
        if ($remoteHash -notmatch '\Asha256:[0-9a-fA-F]{64}\z') {
            $downloadRoot = Join-Path $snapshotRoot 'existing'
            Invoke-UploadCommand $gh @('release', 'download', $Tag, '--repo', $selector, '--pattern', $name, '--dir', $downloadRoot) | Out-Null
            $remoteHash = 'sha256:' + (Get-FileHash -LiteralPath (Join-Path $downloadRoot $name)).Hash.ToLowerInvariant()
        }
        if ($existing[0].state -cne 'uploaded' -or $remoteHash -ine "sha256:$($hashes[$name])") { throw "Existing release asset differs: $name. Use a new version; no assets were overwritten." }
    }
    # Recheck source before the first write; file uploads use the validated snapshot.
    if ((Invoke-UploadGit @('rev-parse', '--verify', 'HEAD^{commit}')) -cne $sourceCommit -or (Invoke-UploadGit @('status', '--porcelain=v1', '--untracked-files=normal'))) { throw 'Source changed during upload preparation.' }
    if (-not $release) {
        $arguments = @('release', 'create', $Tag, '--repo', $selector, '--target', $sourceCommit, '--title', $Tag, '--draft')
        $notesCopy = Join-Path $snapshotRoot 'release-notes.md'
        [IO.File]::WriteAllText($notesCopy, $notesContent, [Text.UTF8Encoding]::new($false))
        $arguments += @('--notes-file', $notesCopy)
        Invoke-UploadCommand $gh $arguments | Out-Null
    }
    if ($missing.Count) { Invoke-UploadCommand $gh (@('release', 'upload', $Tag, '--repo', $selector) + $missing) | Out-Null }
    if (-not $Draft -and (-not $release -or $release.draft)) {
        Invoke-UploadCommand $gh @('release', 'edit', $Tag, '--repo', $selector, '--draft=false') | Out-Null
    }
    Write-Output "Release upload complete: $Tag (ZIP and SHA256SUMS.txt only)."
    Write-Output "https://github.com/$repository/releases"
} finally {
    $resolvedSnapshot = [IO.Path]::GetFullPath($snapshotRoot)
    $expectedParent = [IO.Path]::GetFullPath($snapshotParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedSnapshot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected upload snapshot location.' }
    Remove-Item -LiteralPath $resolvedSnapshot -Recurse -Force
}
