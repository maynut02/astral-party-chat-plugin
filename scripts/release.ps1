param(
    [ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch',
    [string]$Tag = '',
    [switch]$PrepareOnly,
    [switch]$AbandonPending,
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT',
    [string]$RefsRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# Keep the usual .\scripts\release.ps1 entry point usable from Windows PowerShell.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = @(
        (Join-Path $repoRoot '.work\pwsh\pwsh.exe'),
        (Join-Path $repoRoot '.work\pwsh-7.6.6\pwsh.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $pwsh) {
        $command = Get-Command pwsh -ErrorAction SilentlyContinue
        if ($command) { $pwsh = $command.Source }
    }
    if (-not $pwsh) { throw 'PowerShell 7 is required for release. Install it and run this command again.' }
    $forward = @('-NoProfile', '-File', $PSCommandPath)
    foreach ($key in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$key]
        if ($value -is [Management.Automation.SwitchParameter]) {
            if ($value.IsPresent) { $forward += "-$key" }
        } else { $forward += "-$key"; $forward += [string]$value }
    }
    & $pwsh @forward
    if ($LASTEXITCODE -ne 0) { throw 'Local release failed. See the PowerShell 7 output above.' }
    return
}
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'reference-files.ps1')
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-ReleaseGit {
    param([string[]]$Arguments)
    $output = @(& git -C $repoRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Git command failed: $($Arguments[0])." }
    $output | ForEach-Object { [string]$_ }
}

function Invoke-ReleaseGh {
    param([string[]]$Arguments, [switch]$Json)
    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0]). Check gh authentication and repository access." }
    if ($Json) { return (($output -join "`n") | ConvertFrom-Json -ErrorAction Stop) }
    $output | ForEach-Object { [string]$_ }
}

function Assert-ReleaseSource {
    param([string]$ExpectedCommit, [string]$DefaultBranch)
    if (@(Invoke-ReleaseGit -Arguments @('status', '--porcelain', '--untracked-files=all')).Count) {
        throw 'Commit and push all source changes before publishing. Use -PrepareOnly to verify an uncommitted build.'
    }
    $head = Invoke-ReleaseGit -Arguments @('rev-parse', 'HEAD')
    if ($head -cne $ExpectedCommit) { throw 'Source commit changed during release. No upload was started.' }
    $branch = Invoke-ReleaseGit -Arguments @('symbolic-ref', '--quiet', '--short', 'HEAD')
    if ($branch -cne $DefaultBranch) { throw "Publish from the default branch: $DefaultBranch." }
    $remote = Invoke-ReleaseGh -Arguments @('api', '--hostname', 'github.com', "repos/$repository/commits/$([Uri]::EscapeDataString($DefaultBranch))") -Json
    if ($remote.sha -cne $ExpectedCommit) { throw 'Push the source commit to the remote default branch before publishing.' }
}

function New-ReleaseDirectory {
    param([string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release work directory must stay inside the repository.'
    }
    $cursor = $absolute
    while ($cursor.Length -gt $repoRoot.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Release work directories must be ordinary directories.'
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    [IO.Directory]::CreateDirectory($absolute) | Out-Null
}

function Assert-ReleaseFile {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Release files must be ordinary files.'
    }
}

function Assert-ReleaseInstallerSnapshot {
    param([string]$SourceRoot, [string]$ExpectedCommit, [hashtable]$PreviewHashes)
    foreach ($name in @('install.ps1', 'distribution-installer.ps1')) {
        $path = Join-Path $SourceRoot "scripts/$name"
        Assert-ReleaseFile $path
        if ($PrepareOnly) {
            $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            if ($actualHash -cne $PreviewHashes[$name]) { throw 'Installer source snapshot changed during release.' }
        } else {
            $expectedBlob = Invoke-ReleaseGit -Arguments @('rev-parse', "${ExpectedCommit}:scripts/$name")
            $actualBlob = Invoke-ReleaseGit -Arguments @('hash-object', '--no-filters', $path)
            if ($actualBlob -cne $expectedBlob) { throw 'Installer source snapshot differs from the committed source.' }
        }
    }
}

function Write-ReleaseJson {
    param([string]$Path, [object]$Value)
    if (Test-Path -LiteralPath $Path) { Assert-ReleaseFile $Path }
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 8), $utf8)
        [IO.File]::Move($temporary, $Path, $true)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Assert-SavedRelease {
    param([object]$Info, [string]$AssetsRoot, [string]$ExpectedTag, [string]$ExpectedCommit)
    if ($Info.schemaVersion -ne 1 -or $Info.repository -cne $repository -or $Info.tag -cne $ExpectedTag -or
        $Info.version -cne $ExpectedTag.Substring(1) -or $Info.sourceCommit -cne $ExpectedCommit -or
        $Info.sourceClean -isnot [bool] -or -not $Info.sourceClean) {
        throw 'Saved release metadata does not match this clean source commit.'
    }
    $expected = @('AstralParty.Chat.dll', "AstralParty.Chat-$ExpectedTag.zip", 'SHA256SUMS.txt')
    $files = @(Get-ChildItem -LiteralPath $AssetsRoot -Force)
    if ($files.Count -ne 3 -or @($files | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $expected }).Count) {
        throw 'Saved release has unexpected assets.'
    }
    if (@($Info.assets).Count -ne 2 -or @($Info.assets.file | Select-Object -Unique).Count -ne 2) {
        throw 'Saved release asset metadata is invalid.'
    }
    foreach ($asset in $Info.assets) {
        if ($asset.file -cnotin $expected[0..1] -or $asset.sha256 -cnotmatch '\A[a-f0-9]{64}\z') {
            throw 'Saved release asset metadata is invalid.'
        }
        $path = Join-Path $AssetsRoot $asset.file
        Assert-ReleaseFile $path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) {
            throw 'Saved release assets changed. Refusing to upload modified files.'
        }
    }
}

$workRoot = Join-Path $repoRoot '.work'
New-ReleaseDirectory $workRoot
$lock = $null
$locationPushed = $false
try {
    Push-Location -LiteralPath $repoRoot
    $locationPushed = $true
    $lockPath = Join-Path $workRoot 'release.lock'
    if (Test-Path -LiteralPath $lockPath) { Assert-ReleaseFile $lockPath }
    try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another local release is already running. Wait for it to finish.' }

    $releasesRoot = Join-Path $workRoot 'releases'
    New-ReleaseDirectory $releasesRoot
    $pendingPath = Join-Path $releasesRoot 'pending.json'
    if ($AbandonPending) {
        if ($PrepareOnly -or $Tag) { throw '-AbandonPending cannot be combined with -PrepareOnly or -Tag.' }
        if (Test-Path -LiteralPath $pendingPath) {
            Assert-ReleaseFile $pendingPath
            $abandonedPath = Join-Path $releasesRoot ('pending-abandoned-' + [Guid]::NewGuid().ToString('N') + '.json')
            [IO.File]::Move($pendingPath, $abandonedPath)
            Write-Output "Pending release record archived: $abandonedPath"
            Write-Output 'Build files and GitHub tags/Releases were preserved. Run release.ps1 again for a new release.'
        } else { Write-Output 'No pending local release.' }
        return
    }
    $null = Get-Command git -ErrorAction Stop
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Install GitHub CLI and run gh auth login before releasing.' }
    $sourceCommit = Invoke-ReleaseGit -Arguments @('rev-parse', 'HEAD')
    if ($sourceCommit -cnotmatch '\A[a-f0-9]{40}\z') { throw 'A committed Git source is required.' }
    $origin = Invoke-ReleaseGit -Arguments @('remote', 'get-url', 'origin')
    $match = [regex]::Match($origin, '\A(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?\z')
    if (-not $match.Success) { throw 'origin must point to a GitHub repository using HTTPS or SSH.' }
    $repository = $match.Groups[1].Value
    if (@($repository.Split('/') | Where-Object { $_ -in @('.', '..') }).Count) { throw 'Invalid GitHub origin.' }
    $null = Invoke-ReleaseGh -Arguments @('auth', 'status', '--hostname', 'github.com')
    $remoteRepo = Invoke-ReleaseGh -Arguments @('api', '--hostname', 'github.com', "repos/$repository") -Json
    if ($remoteRepo.full_name -ine $repository -or [string]::IsNullOrEmpty($remoteRepo.default_branch)) {
        throw 'origin and GitHub repository metadata differ.'
    }
    if (-not $PrepareOnly) { Assert-ReleaseSource -ExpectedCommit $sourceCommit -DefaultBranch $remoteRepo.default_branch }
    $pending = $null
    if (-not $PrepareOnly -and (Test-Path -LiteralPath $pendingPath)) {
        Assert-ReleaseFile $pendingPath
        try { $pending = [IO.File]::ReadAllText($pendingPath) | ConvertFrom-Json }
        catch { throw 'Pending release record is invalid. Use -AbandonPending to archive it without deleting builds or remote Releases.' }
        if ($pending.schemaVersion -ne 1 -or $pending.repository -cne $repository -or
            $pending.sourceCommit -cne $sourceCommit -or $pending.buildId -cnotmatch '\A[a-f0-9]{32}\z') {
            throw 'An unfinished release belongs to a different source commit or repository. Use -AbandonPending to preserve it and start a new release.'
        }
        if ($Tag -and $Tag -cne $pending.tag) { throw "An unfinished release exists. Retry with -Tag $($pending.tag)." }
        $Tag = $pending.tag
    }

    $source = [IO.File]::ReadAllText((Join-Path $repoRoot 'src/AstralPartyChatPlugin.cs'))
    $versionMatch = [regex]::Match($source, 'public const string PluginVersion = "([0-9]+\.[0-9]+\.[0-9]+)";')
    if (-not $versionMatch.Success) { throw 'Missing initial PluginVersion.' }
    $tags = @(Invoke-ReleaseGh -Arguments @('api', '--hostname', 'github.com', '--paginate', "repos/$repository/tags?per_page=100", '--jq', '.[].name'))
    $release = & (Join-Path $PSScriptRoot 'release-version.ps1') -InitialVersion $versionMatch.Groups[1].Value -Bump $Bump -Tags $tags -ExplicitTag $Tag
    Write-Output "Repository: $repository"
    Write-Output "Release: ChatPlugin $($release.Tag)"
    Write-Output "Source: $sourceCommit"

    $buildId = if ($pending) { $pending.buildId } else { [Guid]::NewGuid().ToString('N') }
    $parent = if ($PrepareOnly) { Join-Path $workRoot 'release-previews' } else { $releasesRoot }
    $releaseRoot = Join-Path (Join-Path $parent $release.Tag) $buildId
    New-ReleaseDirectory $releaseRoot
    $assetsRoot = Join-Path $releaseRoot 'assets'
    $buildInfoPath = Join-Path $releaseRoot 'build-info.json'
    $notesPath = Join-Path $releaseRoot 'release-notes.md'
    $sourceSnapshot = Join-Path $releaseRoot 'source'
    $installerSourceHashes = @{}

    if ($pending) {
        Assert-ReleaseFile $buildInfoPath
        $info = [IO.File]::ReadAllText($buildInfoPath) | ConvertFrom-Json
        Assert-SavedRelease -Info $info -AssetsRoot $assetsRoot -ExpectedTag $release.Tag -ExpectedCommit $sourceCommit
        Write-Output 'Retrying the saved build without increasing its version or rebuilding.'
    } else {
        & (Join-Path $PSScriptRoot 'check-public-files.ps1')
        & (Join-Path $PSScriptRoot 'test.ps1')
        & (Join-Path $repoRoot 'tests/scripts/InstallTests.ps1')
        & (Join-Path $repoRoot 'tests/scripts/DistributionInstallerTests.ps1')
        & (Join-Path $repoRoot 'tests/scripts/ReleaseVersionTests.ps1')
        & (Join-Path $repoRoot 'tests/scripts/LocalReleaseTests.ps1')

        New-ReleaseDirectory $sourceSnapshot
        if ($PrepareOnly) {
            $listing = @(Invoke-ReleaseGit -Arguments @('ls-files', '--cached', '--others', '--exclude-standard', '-z'))
            $candidates = @(($listing -join "`n") -split "`0" | Where-Object { $_ } | Sort-Object -Unique)
            foreach ($relative in $candidates) {
                $original = Join-Path $repoRoot $relative
                if (-not (Test-Path -LiteralPath $original -PathType Leaf)) { continue }
                Assert-ReleaseFile $original
                $destination = Join-Path $sourceSnapshot $relative
                New-ReleaseDirectory ([IO.Path]::GetDirectoryName($destination))
                Copy-Item -LiteralPath $original -Destination $destination
            }
        } else {
            $sourceArchive = Join-Path $releaseRoot 'source.zip'
            # Archive uses working-tree conversion. Keep committed LF bytes
            # regardless of the developer's Windows line-ending preferences.
            $null = Invoke-ReleaseGit -Arguments @('-c', 'core.autocrlf=false', '-c', 'core.eol=lf',
                'archive', '--format=zip', '--output', $sourceArchive, $sourceCommit)
            Expand-Archive -LiteralPath $sourceArchive -DestinationPath $sourceSnapshot
        }
        foreach ($name in @('install.ps1', 'distribution-installer.ps1')) {
            $path = Join-Path $sourceSnapshot "scripts/$name"
            Assert-ReleaseFile $path
            $installerSourceHashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
        Assert-ReleaseInstallerSnapshot -SourceRoot $sourceSnapshot -ExpectedCommit $sourceCommit -PreviewHashes $installerSourceHashes

        if (-not $RefsRoot) {
            $localRefs = Join-Path $workRoot 'refs'
            $RefsRoot = if (Test-Path -LiteralPath (Join-Path $localRefs 'core/BepInEx.Core.dll')) { $localRefs } else { Join-Path $GameRoot 'BepInEx' }
        }
        $RefsRoot = [IO.Path]::GetFullPath($RefsRoot)
        $snapshot = Join-Path $releaseRoot 'refs'
        New-ReleaseDirectory $snapshot
        $references = @()
        foreach ($relative in Get-AstralReferencePaths) {
            $original = Join-Path $RefsRoot $relative
            Assert-ReleaseFile $original
            $destination = Join-Path $snapshot $relative
            New-ReleaseDirectory ([IO.Path]::GetDirectoryName($destination))
            $inputStream = [IO.File]::Open($original, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $outputStream = $null
            try {
                $outputStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $inputStream.CopyTo($outputStream)
            } finally { if ($outputStream) { $outputStream.Dispose() }; $inputStream.Dispose() }
            $assembly = [Reflection.AssemblyName]::GetAssemblyName($destination)
            $references += [ordered]@{
                file = $relative
                assemblyVersion = $assembly.Version.ToString()
                fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination).FileVersion
                sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        $localDotnet = Join-Path $workRoot 'dotnet/dotnet.exe'
        $dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
        $sdkVersion = (& $dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $sdkVersion -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?\z') {
            throw 'No usable .NET SDK found. Run scripts/setup.ps1 first.'
        }
        $pluginRoot = Join-Path $releaseRoot 'plugin'
        & (Join-Path $PSScriptRoot 'build.ps1') -GameRoot $GameRoot -RefsRoot $snapshot -Version $release.Version `
            -SourceRoot $sourceSnapshot -BuildRoot (Join-Path $releaseRoot 'build') -OutputRoot $pluginRoot -DotnetPath $dotnet
        $releaseDll = Join-Path $pluginRoot 'AstralParty.Chat.dll'
        & (Join-Path $repoRoot 'tests/scripts/ReferenceArchiveTests.ps1') -RefsRoot $snapshot
        & (Join-Path $repoRoot 'tests/scripts/ReleaseAssetsTests.ps1') -DllPath $releaseDll
        Assert-ReleaseInstallerSnapshot -SourceRoot $sourceSnapshot -ExpectedCommit $sourceCommit -PreviewHashes $installerSourceHashes
        & (Join-Path $PSScriptRoot 'package-release.ps1') -Tag $release.Tag -OutputRoot $assetsRoot `
            -DllPath $releaseDll -InstallerScriptPath (Join-Path $sourceSnapshot 'scripts/install.ps1')
        foreach ($reference in $references) {
            $path = Join-Path $snapshot $reference.file
            Assert-ReleaseFile $path
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $reference.sha256) {
                throw 'A build reference changed during release. No upload was started.'
            }
        }
        if ((Invoke-ReleaseGit -Arguments @('rev-parse', 'HEAD')) -cne $sourceCommit) { throw 'Source commit changed during the build.' }
        if (-not $PrepareOnly) { Assert-ReleaseSource -ExpectedCommit $sourceCommit -DefaultBranch $remoteRepo.default_branch }
        $assetInfo = @('AstralParty.Chat.dll', "AstralParty.Chat-$($release.Tag).zip") | ForEach-Object {
            [ordered]@{ file = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $assetsRoot $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $info = [ordered]@{
            schemaVersion = 1
            repository = $repository
            tag = $release.Tag
            version = $release.Version
            sourceCommit = $sourceCommit
            sourceClean = -not [bool]$PrepareOnly
            builtAtUtc = [DateTime]::UtcNow.ToString('o')
            sdkVersion = $sdkVersion
            references = @($references)
            assets = @($assetInfo)
        }
        Write-ReleaseJson -Path $buildInfoPath -Value $info
    }

    if ($PrepareOnly) {
        $lines = @("ChatPlugin $($release.Tag)", '', '로컬 검증용 빌드입니다. 업로드하지 않았습니다.', '',
            "소스: https://github.com/$repository/commit/$sourceCommit", "SDK: $($info.sdkVersion)", '', '파일 SHA-256:')
        foreach ($asset in $info.assets) { $lines += "- $($asset.file): $($asset.sha256)" }
        [IO.File]::WriteAllText($notesPath, ($lines -join "`n"), $utf8)
        Write-Output "Prepared only: $assetsRoot"
        Write-Output "Build record: $buildInfoPath"
        return
    }

    Assert-ReleaseSource -ExpectedCommit $sourceCommit -DefaultBranch $remoteRepo.default_branch
    Assert-ReleaseInstallerSnapshot -SourceRoot $sourceSnapshot -ExpectedCommit $sourceCommit -PreviewHashes $installerSourceHashes
    Assert-SavedRelease -Info $info -AssetsRoot $assetsRoot -ExpectedTag $release.Tag -ExpectedCommit $sourceCommit
    if (-not $pending) {
        Write-ReleaseJson -Path $pendingPath -Value ([ordered]@{
            schemaVersion = 1; repository = $repository; tag = $release.Tag; sourceCommit = $sourceCommit; buildId = $buildId
        })
    }
    try {
        & (Join-Path $PSScriptRoot 'publish-release.ps1') -Repository $repository -Tag $release.Tag -SourceCommit $sourceCommit `
            -AssetsRoot $assetsRoot -BuildInfoPath $buildInfoPath -NotesPath $notesPath -RecoverPublished:([bool]$pending) `
            -InstallerScriptPath (Join-Path $sourceSnapshot 'scripts/install.ps1')
    } catch {
        Write-Warning "Release was not completed. Retry: .\scripts\release.ps1 -Tag $($release.Tag)"
        throw
    }
    Remove-Item -LiteralPath $pendingPath -Force
    Write-Output "Published: https://github.com/$repository/releases/tag/$($release.Tag)"
} finally {
    if ($lock) { $lock.Dispose() }
    if ($locationPushed) { Pop-Location }
}
