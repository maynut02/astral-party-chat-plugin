param(
    [ValidateSet('auto', 'patch', 'minor', 'major')][string]$Bump = 'auto',
    [string]$Version = '',
    [switch]$Preview,
    [string]$RepositoryRoot = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $RepositoryRoot) { $RepositoryRoot = Join-Path $PSScriptRoot '..' }
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
. (Join-Path $PSScriptRoot 'project-version.ps1')
. (Join-Path $PSScriptRoot 'release-notes.ps1')
$current = Get-AstralProjectVersion -Root $RepositoryRoot
$git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source

function Invoke-ReleaseGit {
    param([string[]]$Arguments)
    $savedEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $output = @(& $git -c i18n.logOutputEncoding=utf-8 -C $RepositoryRoot @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Git failed ($($Arguments[0])): $($output -join "`n")" }
        return ($output -join "`n")
    } finally { [Console]::OutputEncoding = $savedEncoding }
}

$top = Invoke-ReleaseGit @('rev-parse', '--show-toplevel')
if ([IO.Path]::GetFullPath($top).TrimEnd('\', '/') -ine $RepositoryRoot.TrimEnd('\', '/')) {
    throw 'RepositoryRoot must be the Git repository root.'
}
if ((Invoke-ReleaseGit @('rev-parse', '--is-shallow-repository')) -eq 'true') {
    throw 'Release preparation requires full Git history. Fetch the complete history and release tags first.'
}
$sourceCommit = Invoke-ReleaseGit @('rev-parse', '--verify', 'HEAD^{commit}')
$status = Invoke-ReleaseGit @('status', '--porcelain=v1', '--untracked-files=normal')
foreach ($line in @($status -split "`n" | Where-Object { $_ })) {
    if ($line.Length -lt 4 -or $line.Substring(3) -cne 'VERSION') {
        throw 'Commit source changes before preparing a release. Only VERSION may have uncommitted changes.'
    }
}

# Only release tags reachable from this source commit belong to its history.
$tags = Invoke-ReleaseGit @('tag', '--merged', $sourceCommit, '--list', 'v*')
$eligible = @($tags -split "`n" | ForEach-Object {
    if ($_ -cmatch '\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
        try {
            $number = ConvertTo-AstralVersion $_ -Tag
            [pscustomobject]@{ Tag = $_; Number = [Version]$number }
        } catch { } # Ignore unrelated or out-of-range tag names.
    }
} | Sort-Object Number -Descending)
$previousTag = ''
$previous = $null
if ($eligible.Count -gt 0) {
    $previousTag = $eligible[0].Tag
    $previous = $eligible[0].Number
    if ([Version]$current -lt $previous) { throw 'VERSION is older than the latest reachable release tag.' }
    $taggedVersion = (Invoke-ReleaseGit @('show', "${previousTag}:VERSION")).Trim()
    if ($taggedVersion -cne $previous.ToString()) {
        throw 'The previous release tag does not match its committed VERSION file.'
    }
}
$range = if ($previousTag) { "$previousTag..$sourceCommit" } else { $sourceCommit }
$log = Invoke-ReleaseGit @('log', '--no-merges', '--reverse', '-z', '--format=%H%x00%B', $range, '--')
$commits = @(ConvertFrom-AstralCommitLog -Log $log)

$detected = 'none'
foreach ($commit in $commits) {
    if ($commit.Breaking) { $detected = 'major'; break }
    if ($commit.Type -eq 'feat') { $detected = 'minor' }
    elseif ($detected -eq 'none' -and $commit.Type -in @('fix', 'perf', 'revert')) { $detected = 'patch' }
}
# Pre-1.0 compatibility changes use minor; an explicit major promotes to 1.0.0.
$automatic = $detected
if ($automatic -eq 'major' -and $null -ne $previous -and $previous.Major -eq 0) { $automatic = 'minor' }
$selected = if ($Bump -eq 'auto') { $automatic } else { $Bump }
$pending = $null -ne $previous -and [Version]$current -gt $previous
$target = $current

if ($Version) {
    $target = ConvertTo-AstralVersion $Version
    if ([Version]$target -lt [Version]$current -or ($null -ne $previous -and [Version]$target -le $previous)) {
        throw 'An explicit version must not decrease VERSION and must be newer than the previous release.'
    }
}
elseif ($null -ne $previous) {
    if ($selected -eq 'none') {
        if (-not $pending) { throw 'No release changes found. Use -Bump patch/minor/major for an intentional release.' }
    }
    else {
        $recommended = Get-AstralNextVersion -Version $previous.ToString() -Bump $selected
        if ($pending -and $Bump -eq 'auto') {
            if ([Version]$current -lt [Version]$recommended) {
                throw "Prepared VERSION is too low for these commits. Re-run with -Bump $automatic or an explicit -Version."
            }
        }
        else {
            $target = $recommended
            if ([Version]$target -lt [Version]$current) { throw 'The requested bump would decrease the prepared VERSION.' }
        }
    }
}
# Without a previous release, the current VERSION is the bootstrap release.
$tag = "v$target"
$existingTags = (Invoke-ReleaseGit @('tag', '--list', $tag))
if ($existingTags) { throw 'The target release tag already exists. Prepare a newer version.' }

$notes = New-AstralReleaseNotes -Tag $tag -Commits $commits
$notesPath = Join-Path $RepositoryRoot ".work/releases/$tag/release-notes.md"
if (-not $Preview) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($notesPath)) | Out-Null
    [IO.File]::WriteAllText($notesPath, $notes, [Text.UTF8Encoding]::new($false))
    if ($target -cne $current) {
        [IO.File]::WriteAllText((Join-Path $RepositoryRoot 'VERSION'), "$target`n", [Text.UTF8Encoding]::new($false))
    }
}
[pscustomobject]@{
    Version = $target
    Tag = $tag
    Title = $tag
    PreviousTag = $previousTag
    DetectedBump = $detected
    CommitCount = $commits.Count
    SourceCommit = $sourceCommit
    VersionChanged = (-not $Preview -and $target -cne $current)
    Preview = [bool]$Preview
    NotesPath = if ($Preview) { '' } else { $notesPath }
}
