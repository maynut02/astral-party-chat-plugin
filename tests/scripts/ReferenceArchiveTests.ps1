param(
    [string]$RefsRoot = '',
    [switch]$KeepWorkspace
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exportScript = Join-Path $repoRoot 'scripts/export-refs.ps1'
$importScript = Join-Path $repoRoot 'scripts/import-refs.ps1'
. (Join-Path $repoRoot 'scripts/reference-files.ps1')
Add-Type -AssemblyName System.IO.Compression
if ([string]::IsNullOrWhiteSpace($RefsRoot)) { $RefsRoot = Join-Path $repoRoot '.work/refs' }
$RefsRoot = [IO.Path]::GetFullPath($RefsRoot)
$paths = @(Get-AstralReferencePaths)
$workParent = [IO.Path]::GetFullPath((Join-Path $repoRoot '.work'))
$workspace = Join-Path $workParent ('reference-archive-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workspace) | Out-Null
$oldUrl = [Environment]::GetEnvironmentVariable('ASTRAL_REFS_URL')
$script:passed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-ArchiveTest {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:passed++
        Write-Output "PASS $Name"
    }
    catch { throw "FAIL ${Name}: $($_.Exception.Message)" }
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$MessagePattern)
    $caught = $null
    try { & $Action | Out-Null }
    catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'Expected rejection, but the operation succeeded.'
    Assert-True ($caught.Exception.Message -match $MessagePattern) 'Rejection happened at the wrong validation stage.'
    # The entire formatted error record must not expose the fake URL secret.
    $details = $caught | Out-String
    Assert-True (-not $details.Contains('TEST_URL_SECRET')) 'A URL secret appeared in an error record.'
}

function Assert-NoImportedFiles {
    param([string]$Destination)
    if (Test-Path -LiteralPath $Destination) {
        Assert-True (@(Get-ChildItem -LiteralPath $Destination -File -Recurse -Force).Count -eq 0) 'Failed import left files behind.'
    }
}

function New-TestArchive {
    param(
        [string]$Path,
        [string[]]$Names,
        [switch]$UseRealDlls,
        [string]$InvalidPath = ''
    )
    $fileStream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($name in $Names) {
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $sourceStream = $null
            try {
                if ($UseRealDlls -and $name -ne $InvalidPath) {
                    $sourceStream = [IO.File]::OpenRead((Join-Path $RefsRoot $name))
                    $sourceStream.CopyTo($entryStream)
                }
                else {
                    $bytes = [Text.Encoding]::UTF8.GetBytes('not a managed DLL')
                    $entryStream.Write($bytes, 0, $bytes.Length)
                }
            }
            finally {
                if ($null -ne $sourceStream) { $sourceStream.Dispose() }
                $entryStream.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $zip) { $zip.Dispose() }
        $fileStream.Dispose()
    }
}

function Set-TestZipDeclaredLengths {
    param([string]$Path, [uint32[]]$Lengths)
    # Patch the actual ZIP central directory and local headers without allocating
    # huge payloads. Metadata bombs must be rejected before any DLL is written.
    $bytes = [IO.File]::ReadAllBytes($Path)
    $end = $bytes.Length - 22
    Assert-True ([BitConverter]::ToUInt32($bytes, $end) -eq 0x06054b50) 'Test ZIP has no end record.'
    $count = [BitConverter]::ToUInt16($bytes, $end + 10)
    Assert-True ($count -eq $Lengths.Count) 'Test ZIP entry count mismatch.'
    $offset = [int][BitConverter]::ToUInt32($bytes, $end + 16)
    for ($i = 0; $i -lt $count; $i++) {
        Assert-True ([BitConverter]::ToUInt32($bytes, $offset) -eq 0x02014b50) 'Test ZIP central directory is invalid.'
        $lengthBytes = [BitConverter]::GetBytes($Lengths[$i])
        [Array]::Copy($lengthBytes, 0, $bytes, $offset + 24, 4)
        $localOffset = [int][BitConverter]::ToUInt32($bytes, $offset + 42)
        [Array]::Copy($lengthBytes, 0, $bytes, $localOffset + 22, 4)
        $offset += 46 + [BitConverter]::ToUInt16($bytes, $offset + 28) +
            [BitConverter]::ToUInt16($bytes, $offset + 30) + [BitConverter]::ToUInt16($bytes, $offset + 32)
    }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Test-RejectedArchive {
    param([string]$Name, [string[]]$Names, [string]$Pattern = '[0-9]+ allowlisted DLL')
    $zipPath = Join-Path $workspace ($Name + '.zip')
    $destination = Join-Path $workspace ($Name + '-destination')
    New-TestArchive $zipPath $Names
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Assert-Rejected { & $importScript -ArchivePath $zipPath -ExpectedSha256 $hash -DestinationRoot $destination } $Pattern
    Assert-NoImportedFiles $destination
}

try {
    $fixtureRoot = Join-Path $workspace 'source'
    foreach ($relativePath in $paths) {
        $sourcePath = Join-Path $RefsRoot $relativePath
        Assert-True (Test-Path -LiteralPath $sourcePath -PathType Leaf) "Missing test prerequisite: $relativePath"
        $fixturePath = Join-Path $fixtureRoot $relativePath
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fixturePath)) | Out-Null
        [IO.File]::Copy($sourcePath, $fixturePath)
    }
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'versions.json'), '{"mustNotBeExported":true}')
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'extra.dll'), 'must not be exported')
    $validArchive = Join-Path $workspace 'valid.zip'

    Invoke-ArchiveTest 'export/import round trip contains only the required DLLs' {
        $output = @(& $exportScript -RefsRoot $fixtureRoot -OutputPath $validArchive)
        $script:validHash = (Get-FileHash -LiteralPath $validArchive -Algorithm SHA256).Hash
        Assert-True ($output -contains "archive=$validArchive") 'Export did not report its ZIP path.'
        Assert-True ($output -contains ('sha256=' + $script:validHash.ToLowerInvariant())) 'Export did not report its SHA256.'
        $stream = [IO.File]::OpenRead($validArchive)
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            Assert-True ($zip.Entries.Count -eq $paths.Count) 'Export included extra files or directory entries.'
            foreach ($entry in $zip.Entries) { Assert-True ($paths -ccontains $entry.FullName) 'Export used an unexpected path.' }
        }
        finally { $zip.Dispose(); $stream.Dispose() }
        $destination = Join-Path $workspace 'roundtrip'
        & $importScript -ArchivePath $validArchive -ExpectedSha256 $script:validHash.ToLowerInvariant() -DestinationRoot $destination | Out-Null
        Assert-True (@(Get-ChildItem -LiteralPath $destination -File -Recurse -Force).Count -eq $paths.Count) 'Import file count differs.'
        foreach ($relativePath in $paths) {
            $before = (Get-FileHash -LiteralPath (Join-Path $fixtureRoot $relativePath) -Algorithm SHA256).Hash
            $after = (Get-FileHash -LiteralPath (Join-Path $destination $relativePath) -Algorithm SHA256).Hash
            Assert-True ($before -eq $after) "Round-trip bytes differ: $relativePath"
        }
    }

    Invoke-ArchiveTest 'existing export requires Force and preserves the old ZIP on failure' {
        Assert-Rejected { & $exportScript -RefsRoot $fixtureRoot -OutputPath $validArchive } 'Use -Force'
        Assert-True ((Get-FileHash -LiteralPath $validArchive -Algorithm SHA256).Hash -eq $script:validHash) 'Refused export changed the old ZIP.'
        $badFixture = Join-Path $fixtureRoot $paths[0]
        $originalBytes = [IO.File]::ReadAllBytes($badFixture)
        try {
            [IO.File]::WriteAllText($badFixture, 'invalid DLL')
            Assert-Rejected { & $exportScript -RefsRoot $fixtureRoot -OutputPath $validArchive -Force } 'Invalid managed DLL'
            Assert-True ((Get-FileHash -LiteralPath $validArchive -Algorithm SHA256).Hash -eq $script:validHash) 'Failed forced export destroyed the old ZIP.'
        }
        finally { [IO.File]::WriteAllBytes($badFixture, $originalBytes) }
        & $exportScript -RefsRoot $fixtureRoot -OutputPath $validArchive -Force | Out-Null
        $script:validHash = (Get-FileHash -LiteralPath $validArchive -Algorithm SHA256).Hash
        $destination = Join-Path $workspace 'forced-export-import'
        & $importScript -ArchivePath $validArchive -ExpectedSha256 $script:validHash -DestinationRoot $destination | Out-Null
    }

    Invoke-ArchiveTest 'missing export reference creates no ZIP' {
        $outputPath = Join-Path $workspace 'missing-export.zip'
        Assert-Rejected { & $exportScript -RefsRoot (Join-Path $workspace 'missing-source') -OutputPath $outputPath } 'Missing build reference'
        Assert-True (-not (Test-Path -LiteralPath $outputPath)) 'Failed export created a ZIP.'
    }

    Invoke-ArchiveTest 'wrong SHA256 is rejected before parsing or extracting' {
        $destination = Join-Path $workspace 'wrong-hash'
        $wrongHash = '0' * 64
        Assert-Rejected { & $importScript -ArchivePath $validArchive -ExpectedSha256 $wrongHash -DestinationRoot $destination } 'SHA256 does not match'
        Assert-NoImportedFiles $destination
        $invalidArchive = Join-Path $workspace 'not-a-zip'
        [IO.File]::WriteAllText($invalidArchive, 'not a ZIP')
        Assert-Rejected { & $importScript -ArchivePath $invalidArchive -ExpectedSha256 $wrongHash -DestinationRoot $destination } 'SHA256 does not match'
        $actualHash = (Get-FileHash -LiteralPath $invalidArchive -Algorithm SHA256).Hash
        Assert-Rejected { & $importScript -ArchivePath $invalidArchive -ExpectedSha256 $actualHash -DestinationRoot $destination } 'ZIP is invalid'
    }

    Invoke-ArchiveTest 'missing entry' { Test-RejectedArchive 'missing' $paths[0..9] }
    Invoke-ArchiveTest 'extra file and manifest' { Test-RejectedArchive 'extra' ($paths + 'versions.json') }
    Invoke-ArchiveTest 'directory entries are rejected' { Test-RejectedArchive 'directory' ($paths + 'core/') }
    Invoke-ArchiveTest 'duplicate entry even when total entry count is correct' { Test-RejectedArchive 'duplicate' ($paths[0..($paths.Count - 2)] + $paths[0]) }

    Invoke-ArchiveTest 'traversal and noncanonical paths cannot escape the allowlist' {
        $outsidePath = Join-Path $workspace 'outside.dll'
        [IO.File]::WriteAllText($outsidePath, 'preserve this file')
        $outsideHash = (Get-FileHash -LiteralPath $outsidePath -Algorithm SHA256).Hash
        $badNames = @('../outside.dll', '../../outside.dll', '/core/0Harmony.dll', 'C:/outside.dll', 'core\0Harmony.dll', 'CORE/0Harmony.dll', 'core/../core/0Harmony.dll')
        for ($i = 0; $i -lt $badNames.Count; $i++) {
            Test-RejectedArchive ('bad-path-' + $i) (@($badNames[$i]) + $paths[1..10])
        }
        Assert-True ((Get-FileHash -LiteralPath $outsidePath -Algorithm SHA256).Hash -eq $outsideHash) 'Traversal touched a file outside the destination.'
    }

    Invoke-ArchiveTest 'oversized per-file ZIP metadata is rejected before extraction' {
        $zipPath = Join-Path $workspace 'oversized-entry.zip'
        New-TestArchive $zipPath $paths
        $lengths = [uint32[]]@(foreach ($path in $paths) { 17 })
        $lengths[0] = 24MB + 1
        Set-TestZipDeclaredLengths $zipPath $lengths
        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        $destination = Join-Path $workspace 'oversized-entry'
        Assert-Rejected { & $importScript -ArchivePath $zipPath -ExpectedSha256 $hash -DestinationRoot $destination } '24 MiB.*metadata'
        Assert-True (-not (Test-Path -LiteralPath $destination)) 'Metadata rejection created a destination.'
    }

    Invoke-ArchiveTest 'oversized total ZIP metadata is rejected before extraction' {
        $zipPath = Join-Path $workspace 'oversized-total.zip'
        New-TestArchive $zipPath $paths
        Set-TestZipDeclaredLengths $zipPath ([uint32[]]@(foreach ($path in $paths) { 7MB }))
        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        $destination = Join-Path $workspace 'oversized-total'
        Assert-Rejected { & $importScript -ArchivePath $zipPath -ExpectedSha256 $hash -DestinationRoot $destination } '64 MiB in total'
        Assert-True (-not (Test-Path -LiteralPath $destination)) 'Metadata rejection created a destination.'
    }

    Invoke-ArchiveTest 'expanded data exceeding metadata is rejected and cleaned' {
        $zipPath = Join-Path $workspace 'false-length.zip'
        New-TestArchive $zipPath $paths -UseRealDlls
        $lengths = [uint32[]]@(foreach ($path in $paths) { (Get-Item -LiteralPath (Join-Path $RefsRoot $path)).Length })
        $lengths[1] = 1
        Set-TestZipDeclaredLengths $zipPath $lengths
        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        $destination = Join-Path $workspace 'false-length'
        # .NET Framework exposes excess decompressed bytes; newer .NET can
        # truncate at the declared length, producing an invalid managed DLL.
        Assert-Rejected { & $importScript -ArchivePath $zipPath -ExpectedSha256 $hash -DestinationRoot $destination } 'extraction failed.*declared size|Invalid managed DLL'
        Assert-NoImportedFiles $destination
    }

    Invoke-ArchiveTest 'invalid DLL preserves existing directories and permits retry at the same path' {
        $zipPath = Join-Path $workspace 'invalid-dll.zip'
        New-TestArchive $zipPath $paths -UseRealDlls -InvalidPath $paths[1]
        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        $destination = Join-Path $workspace 'invalid-dll'
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        $siblingPath = Join-Path $workspace 'unrelated.txt'
        [IO.File]::WriteAllText($siblingPath, 'unrelated file')
        Assert-Rejected { & $importScript -ArchivePath $zipPath -ExpectedSha256 $hash -DestinationRoot $destination } 'Invalid managed DLL'
        Assert-NoImportedFiles $destination
        Assert-True ([IO.File]::ReadAllText($siblingPath) -eq 'unrelated file') 'Cleanup changed an unrelated file.'
        Assert-True (Test-Path -LiteralPath $destination -PathType Container) 'Cleanup removed a preexisting directory.'
        Assert-True (@(Get-ChildItem -LiteralPath $destination -Force).Count -eq 0) 'Cleanup left import-created directories behind.'
        & $importScript -ArchivePath $validArchive -ExpectedSha256 $script:validHash -DestinationRoot $destination | Out-Null
        Assert-True (@(Get-ChildItem -LiteralPath $destination -File -Recurse -Force).Count -eq $paths.Count) 'Retry at the same destination failed.'
    }

    Invoke-ArchiveTest 'failed import removes its own empty directory tree and permits retry' {
        $zipPath = Join-Path $workspace 'invalid-dll.zip'
        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        $createdParent = Join-Path $workspace 'new-parent'
        $destination = Join-Path $createdParent 'new-destination'
        Assert-Rejected { & $importScript -ArchivePath $zipPath -ExpectedSha256 $hash -DestinationRoot $destination } 'Invalid managed DLL'
        Assert-True (-not (Test-Path -LiteralPath $createdParent)) 'Cleanup left its new empty parent/destination directories behind.'
        Assert-True (Test-Path -LiteralPath $workspace -PathType Container) 'Cleanup removed the preexisting parent.'
        # Trailing separators must not cause a directory to be recorded twice.
        & $importScript -ArchivePath $validArchive -ExpectedSha256 $script:validHash -DestinationRoot ($destination + [IO.Path]::DirectorySeparatorChar) | Out-Null
        Assert-True (@(Get-ChildItem -LiteralPath $destination -File -Recurse -Force).Count -eq $paths.Count) 'Retry after cleaning an owned directory tree failed.'
    }

    Invoke-ArchiveTest 'nonempty destination including hidden files is preserved' {
        $destination = Join-Path $workspace 'nonempty'
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        $sentinel = Join-Path $destination 'keep.txt'
        [IO.File]::WriteAllText($sentinel, 'keep')
        [IO.File]::SetAttributes($sentinel, [IO.FileAttributes]::Hidden)
        Assert-Rejected { & $importScript -ArchivePath $validArchive -ExpectedSha256 $script:validHash -DestinationRoot $destination } 'empty, ordinary directory'
        Assert-True ([IO.File]::ReadAllText($sentinel) -eq 'keep') 'Import changed a preexisting file.'
        Assert-True (@(Get-ChildItem -LiteralPath $destination -Force).Count -eq 1) 'Import added files to a nonempty destination.'
    }

    Invoke-ArchiveTest 'compressed local archive exceeding 64 MiB is rejected' {
        $zipPath = Join-Path $workspace 'large-archive.zip'
        $stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
        try { $stream.SetLength(64MB + 1) }
        finally { $stream.Dispose() }
        $destination = Join-Path $workspace 'large-archive'
        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        Assert-Rejected { & $importScript -ArchivePath $zipPath -ExpectedSha256 $hash -DestinationRoot $destination } 'archive cannot be read or exceeds 64 MiB'
        Assert-NoImportedFiles $destination
    }

    Invoke-ArchiveTest 'URL is read only from the environment and requires HTTPS without userinfo' {
        $destination = Join-Path $workspace 'url-rejected'
        foreach ($url in @('', 'http://127.0.0.1/?token=TEST_URL_SECRET', 'https://TEST_URL_SECRET:password@127.0.0.1/', 'https://@127.0.0.1/?token=TEST_URL_SECRET')) {
            [Environment]::SetEnvironmentVariable('ASTRAL_REFS_URL', $url)
            Assert-Rejected { & $importScript -ExpectedSha256 $script:validHash -DestinationRoot $destination } 'HTTPS URL without userinfo'
        }
        # A bogus environment URL is ignored when the caller supplies a ZIP.
        [Environment]::SetEnvironmentVariable('ASTRAL_REFS_URL', 'http://TEST_URL_SECRET.invalid/')
        & $importScript -ArchivePath $validArchive -ExpectedSha256 $script:validHash -DestinationRoot (Join-Path $workspace 'local-ignores-url') | Out-Null
        Assert-NoImportedFiles $destination
    }

    Invoke-ArchiveTest 'download failure hides URL and network error details' {
        # Port zero is never a valid remote service; no external host or secret
        # is used. This exercises the download error path on both runtimes.
        [Environment]::SetEnvironmentVariable('ASTRAL_REFS_URL', 'https://127.0.0.1:0/?token=TEST_URL_SECRET')
        $destination = Join-Path $workspace 'download-failure'
        Assert-Rejected { & $importScript -ExpectedSha256 $script:validHash -DestinationRoot $destination } '\AReference download failed \(HTTPS only, no redirects, 60-second deadline, 64 MiB maximum\)\.'
        Assert-NoImportedFiles $destination
    }

    Write-Output ("ReferenceArchiveTests: {0} passed (PowerShell {1})." -f $script:passed, $PSVersionTable.PSVersion)
}
finally {
    [Environment]::SetEnvironmentVariable('ASTRAL_REFS_URL', $oldUrl)
    if ($KeepWorkspace) { Write-Output "workspace=$workspace" }
    else {
        # This root was created by this test run, directly under the workspace.
        # Check the resolved absolute target before any recursive deletion.
        $resolved = [IO.Path]::GetFullPath($workspace)
        if ([IO.Path]::GetDirectoryName($resolved) -ne $workParent -or
            [IO.Path]::GetFileName($resolved) -notmatch '\Areference-archive-tests-[0-9a-f]{32}\z') {
            throw 'Refusing to clean an unexpected test workspace path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
