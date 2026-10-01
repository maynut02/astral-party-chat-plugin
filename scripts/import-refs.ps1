param(
    [string]$ArchivePath = '',
    [Parameter(Mandatory = $true)]
    [string]$ExpectedSha256,
    [string]$DestinationRoot = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'reference-files.ps1')

# Every externally caused failure is reported through a fixed stage message.
# In particular, never print a request URI or an HTTP exception/inner exception.
$failureMessage = 'Reference import failed.'
$downloadPath = $null
$downloadOwned = $false
$archiveStream = $null
$archive = $null
$createdFiles = [Collections.Generic.List[string]]::new()
$createdDirectories = [Collections.Generic.List[string]]::new()
$completed = $false

function Assert-ReferenceDirectoryPath {
    param([string]$Path)
    $cursor = $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Unsafe destination directory.'
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function New-ReferenceDirectory {
    param([string]$Path, [Collections.Generic.List[string]]$CreatedDirectories)
    Assert-ReferenceDirectoryPath $Path
    $missing = [Collections.Generic.List[string]]::new()
    $cursor = $Path
    while (-not (Test-Path -LiteralPath $cursor)) {
        $missing.Add($cursor)
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    # Create each missing level explicitly so cleanup also owns any new parents.
    for ($i = $missing.Count - 1; $i -ge 0; $i--) {
        $directoryPath = $missing[$i]
        if (-not (Test-Path -LiteralPath $directoryPath)) {
            [IO.Directory]::CreateDirectory($directoryPath) | Out-Null
            $CreatedDirectories.Add($directoryPath)
        }
        Assert-ReferenceDirectoryPath $directoryPath
    }
}

function Wait-ReferenceDownloadTask {
    param(
        [Threading.Tasks.Task]$Task,
        [Diagnostics.Stopwatch]$Timer,
        [Threading.CancellationTokenSource]$Cancellation
    )
    $remaining = 60000L - $Timer.ElapsedMilliseconds
    # Wait has its own deadline as well as the token: older .NET stream
    # implementations do not always honor cancellation during a pending read.
    if ($remaining -le 0 -or -not $Task.Wait([int]$remaining)) {
        $Cancellation.Cancel()
        throw 'Download deadline exceeded.'
    }
    $Task.GetAwaiter().GetResult()
}

function Save-ReferenceDownload {
    param([Uri]$Uri, [IO.Stream]$Output)
    Add-Type -AssemblyName System.Net.Http
    $handler = $null
    $client = $null
    $response = $null
    $inputStream = $null
    $cancellation = $null
    try {
        $handler = [Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseDefaultCredentials = $false
        $client = [Net.Http.HttpClient]::new($handler)
        $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
        $cancellation = [Threading.CancellationTokenSource]::new()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $cancellation.CancelAfter(60000)
        $requestTask = $client.GetAsync($Uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cancellation.Token)
        $response = Wait-ReferenceDownloadTask $requestTask $timer $cancellation
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw 'Download status rejected.' }
        $contentLength = $response.Content.Headers.ContentLength
        if ($null -ne $contentLength -and ($contentLength -lt 0 -or $contentLength -gt 64MB)) {
            throw 'Download length rejected.'
        }
        $inputStream = Wait-ReferenceDownloadTask ($response.Content.ReadAsStreamAsync()) $timer $cancellation
        $buffer = [byte[]]::new(81920)
        [long]$received = 0
        while ($true) {
            # Read at most one byte beyond the limit, and never write that byte.
            $count = [int][Math]::Min([long]$buffer.Length, 64MB - $received + 1)
            $readTask = $inputStream.ReadAsync($buffer, 0, $count, $cancellation.Token)
            $read = Wait-ReferenceDownloadTask $readTask $timer $cancellation
            if ($read -eq 0) { break }
            $received += $read
            if ($received -gt 64MB) { throw 'Download limit exceeded.' }
            $writeTask = $Output.WriteAsync($buffer, 0, $read, $cancellation.Token)
            Wait-ReferenceDownloadTask $writeTask $timer $cancellation | Out-Null
        }
        if ($timer.ElapsedMilliseconds -ge 60000) { throw 'Download deadline exceeded.' }
        if ($null -ne $contentLength -and $received -ne $contentLength) { throw 'Download length mismatch.' }
    }
    finally {
        if ($null -ne $inputStream) { $inputStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $client) { $client.Dispose() }
        if ($null -ne $handler) { $handler.Dispose() }
        if ($null -ne $cancellation) { $cancellation.Dispose() }
    }
}

try {
    $failureMessage = 'ExpectedSha256 must contain exactly 64 hexadecimal characters.'
    if ($ExpectedSha256 -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'Invalid expected hash.' }

    $failureMessage = 'Destination must be an empty, ordinary directory with no reparse-point ancestors.'
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if ([string]::IsNullOrWhiteSpace($DestinationRoot)) { $DestinationRoot = Join-Path $repoRoot '.work/ci-refs' }
    $DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
    if ($DestinationRoot.Length -gt [IO.Path]::GetPathRoot($DestinationRoot).Length) {
        $DestinationRoot = $DestinationRoot.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    }
    Assert-ReferenceDirectoryPath $DestinationRoot
    if ((Test-Path -LiteralPath $DestinationRoot) -and
        @(Get-ChildItem -LiteralPath $DestinationRoot -Force).Count -ne 0) {
        throw 'Destination is not empty.'
    }

    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        $failureMessage = 'ASTRAL_REFS_URL must specify an absolute HTTPS URL without userinfo.'
        $downloadUri = $null
        $url = [Environment]::GetEnvironmentVariable('ASTRAL_REFS_URL')
        if ([string]::IsNullOrWhiteSpace($url) -or
            -not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$downloadUri) -or
            $downloadUri.Scheme -ne 'https' -or -not [string]::IsNullOrEmpty($downloadUri.UserInfo) -or
            $url -match '\Ahttps://[^/?#]*@') {
            throw 'Download URL rejected.'
        }
        $failureMessage = 'Reference download failed (HTTPS only, no redirects, 60-second deadline, 64 MiB maximum).'
        $downloadPath = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
        $downloadStream = $null
        try {
            $downloadStream = [IO.File]::Open($downloadPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $downloadOwned = $true
            Save-ReferenceDownload $downloadUri $downloadStream
        }
        finally { if ($null -ne $downloadStream) { $downloadStream.Dispose() } }
        $ArchivePath = $downloadPath
    }

    $failureMessage = 'Reference archive cannot be read or exceeds 64 MiB.'
    $archiveStream = [IO.File]::Open([IO.Path]::GetFullPath($ArchivePath), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ($archiveStream.Length -gt 64MB) { throw 'Archive size limit exceeded.' }
    $failureMessage = 'Reference archive SHA256 does not match ExpectedSha256.'
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($archiveStream)).Replace('-', '') }
    finally { $hasher.Dispose() }
    if (-not [string]::Equals($actualHash, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Archive hash mismatch.'
    }
    $archiveStream.Position = 0

    $failureMessage = 'Reference ZIP is invalid.'
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    $referencePaths = @(Get-AstralReferencePaths)
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $referencePaths) { $allowed.Add($path) | Out-Null }
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
    [long]$totalDeclared = 0
    foreach ($entry in $archive.Entries) {
        $failureMessage = "Reference ZIP must contain exactly the $($referencePaths.Count) allowlisted DLL paths, with no duplicates or other entries."
        if (-not $allowed.Contains($entry.FullName) -or $entries.ContainsKey($entry.FullName)) { throw 'ZIP path rejected.' }
        $failureMessage = 'Reference ZIP entry exceeds 24 MiB or has invalid size metadata.'
        if ($entry.Length -lt 0 -or $entry.Length -gt 24MB) { throw 'ZIP entry size rejected.' }
        $totalDeclared += $entry.Length
        $failureMessage = 'Reference ZIP entries exceed 64 MiB in total.'
        if ($totalDeclared -gt 64MB) { throw 'ZIP total size rejected.' }
        $entries.Add($entry.FullName, $entry)
    }
    $failureMessage = "Reference ZIP must contain exactly the $($referencePaths.Count) allowlisted DLL paths, with no duplicates or other entries."
    if ($entries.Count -ne $referencePaths.Count) { throw 'Missing ZIP entries.' }

    $failureMessage = 'Reference DLL extraction failed or exceeded its declared size, 24 MiB per file, or 64 MiB in total.'
    # Construct output paths from the trusted list, never from an entry name.
    New-ReferenceDirectory $DestinationRoot $createdDirectories
    if (@(Get-ChildItem -LiteralPath $DestinationRoot -Force).Count -ne 0) { throw 'Destination changed.' }
    $buffer = [byte[]]::new(81920)
    [long]$totalWritten = 0
    foreach ($relativePath in $referencePaths) {
        $entry = $entries[$relativePath]
        $targetPath = Join-Path $DestinationRoot $relativePath
        $parentPath = [IO.Path]::GetDirectoryName($targetPath)
        New-ReferenceDirectory $parentPath $createdDirectories
        $inputStream = $null
        $targetStream = $null
        [long]$written = 0
        try {
            $inputStream = $entry.Open()
            $targetStream = [IO.File]::Open($targetPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $createdFiles.Add($targetPath)
            while ($true) {
                $remaining = [Math]::Min(24MB - $written, 64MB - $totalWritten)
                $remaining = [Math]::Min($remaining, $entry.Length - $written)
                $count = [int][Math]::Min([long]$buffer.Length, $remaining + 1)
                $read = $inputStream.Read($buffer, 0, $count)
                if ($read -eq 0) { break }
                if ($read -gt $remaining) { throw 'Expanded ZIP size rejected.' }
                $targetStream.Write($buffer, 0, $read)
                $written += $read
                $totalWritten += $read
            }
            if ($written -ne $entry.Length) { throw 'ZIP size mismatch.' }
        }
        finally {
            if ($null -ne $targetStream) { $targetStream.Dispose() }
            if ($null -ne $inputStream) { $inputStream.Dispose() }
        }
        $failureMessage = "Invalid managed DLL in reference ZIP: $relativePath"
        [Reflection.AssemblyName]::GetAssemblyName($targetPath) | Out-Null
        $failureMessage = 'Reference DLL extraction failed or exceeded its declared size, 24 MiB per file, or 64 MiB in total.'
    }
    $completed = $true
    Write-Output "refs=$DestinationRoot"
}
catch {
    # Do not rethrow $_: it can contain signed URLs, credentials or HTTP details.
    throw $failureMessage
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $archiveStream) { $archiveStream.Dispose() }
    if (-not $completed) {
        foreach ($createdPath in $createdFiles) {
            try { [IO.File]::Delete($createdPath) }
            catch { Write-Warning 'Could not remove an import-created DLL.' }
        }
        for ($i = $createdDirectories.Count - 1; $i -ge 0; $i--) {
            try { [IO.Directory]::Delete($createdDirectories[$i], $false) }
            catch { Write-Warning 'Could not remove an import-created empty directory.' }
        }
    }
    if ($downloadOwned) {
        try { [IO.File]::Delete($downloadPath) }
        catch { Write-Warning 'Could not remove the import-created download file.' }
    }
}
