#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'scripts/reference-files.ps1')
$fixture = Join-Path $root ('.work/reference-tests/' + [Guid]::NewGuid().ToString('N'))
$gameRoot = Join-Path $fixture 'game'
$workRoot = Join-Path $fixture 'cache'
$sourceRoot = Join-Path $gameRoot 'BepInEx'
$refs = Join-Path $workRoot 'refs'
$sampleAssembly = [System.Linq.Enumerable].Assembly.Location
$alternateAssembly = [System.Xml.Linq.XElement].Assembly.Location
$relativePaths = @(Get-AstralReferencePaths)
$script:passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Test-Case([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:passed++
    Write-Output "PASS references: $Name"
}
function Get-CacheSnapshot {
    foreach ($relative in @($relativePaths) + @('versions.json', 'unrelated.txt')) {
        (Get-FileHash -LiteralPath (Join-Path $refs $relative) -Algorithm SHA256).Hash
    }
}
function Assert-Rejected([scriptblock]$Body, [string]$MessagePattern) {
    $rejected = $false
    try { & $Body | Out-Null }
    catch {
        if ($_.Exception.Message -notlike $MessagePattern) { throw }
        $rejected = $true
    }
    Assert-True $rejected 'Invalid references were accepted.'
}

try {
    Test-Case 'required-list-matches-project-references-exactly' {
        $projectPaths = @(foreach ($project in Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.csproj' -Recurse) {
            [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
            foreach ($reference in $xml.SelectNodes('//Reference/HintPath')) {
                $relative = $reference.InnerText.Replace('\', '/') -replace '^\$\(AstralBepInExRoot\)/', ''
                if ($relative -match '^(core|interop)/') { $relative }
            }
        })
        Assert-True ($relativePaths.Count -eq @($relativePaths | Select-Object -Unique).Count) 'Required reference list contains duplicates.'
        $differences = @(Compare-Object ($projectPaths | Sort-Object -Unique) ($relativePaths | Sort-Object -Unique))
        Assert-True ($differences.Count -eq 0) 'Required reference list differs from the csproj HintPaths.'
    }
    foreach ($relative in $relativePaths) {
        $path = Join-Path $sourceRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Copy-Item -LiteralPath $sampleAssembly -Destination $path
    }
    Test-Case 'copies-valid-dlls-and-records-actual-versions-and-hashes' {
        Assert-AstralReferences -Root $sourceRoot
        Sync-AstralReferences -GameRoot $gameRoot -WorkRoot $workRoot | Out-Null
        Assert-AstralReferences -Root $refs
        $record = Get-Content -LiteralPath (Join-Path $refs 'versions.json') -Raw | ConvertFrom-Json
        Assert-True ($record.references.Count -eq $relativePaths.Count) 'Reference inventory is incomplete.'
        $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($sampleAssembly).Version.ToString()
        $fileVersion = (Get-Item -LiteralPath $sampleAssembly).VersionInfo.FileVersion
        $sourceHash = (Get-FileHash -LiteralPath $sampleAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
        foreach ($entry in $record.references) {
            Assert-True ($entry.file -in $relativePaths) 'Inventory contains an unexpected dependency.'
            Assert-True ($entry.assemblyVersion -ceq $assemblyVersion) 'Assembly version was not read from the source DLL.'
            Assert-True ($entry.fileVersion -ceq $fileVersion) 'File version was not read from the source DLL.'
            Assert-True ($entry.sha256 -ceq $sourceHash) 'Inventory hash was not the lowercase source DLL hash.'
            Assert-True ((Get-FileHash -LiteralPath (Join-Path $refs $entry.file)).Hash.ToLowerInvariant() -ceq $entry.sha256) 'Cache differs from recorded hash.'
        }
        Assert-True ($null -eq $record.PSObject.Properties['copiedAt']) 'Inventory contains a nondeterministic timestamp.'
        $bytes = [IO.File]::ReadAllBytes((Join-Path $refs 'versions.json'))
        Assert-True (-not ($bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and $bytes[2] -eq 0xbf)) 'Inventory unexpectedly contains a UTF-8 BOM.'
    }
    [IO.File]::WriteAllText((Join-Path $refs 'unrelated.txt'), 'preserve unrelated cache files')
    $snapshot = @(Get-CacheSnapshot) -join ','
    Test-Case 'repeated-sync-keeps-deterministic-metadata-and-unrelated-files' {
        Sync-AstralReferences -GameRoot $gameRoot -WorkRoot $workRoot | Out-Null
        Assert-True ((@(Get-CacheSnapshot) -join ',') -ceq $snapshot) 'Repeated sync changed identical inventory or unrelated files.'
    }
    # Make an earlier DLL different so a partial copy would be observable.
    Copy-Item -LiteralPath $alternateAssembly -Destination (Join-Path $sourceRoot $relativePaths[0]) -Force
    Test-Case 'missing-late-dependency-preserves-all-cached-dlls-and-metadata' {
        Remove-Item -LiteralPath (Join-Path $sourceRoot $relativePaths[-1])
        Assert-Rejected { Sync-AstralReferences -GameRoot $gameRoot -WorkRoot $workRoot } '*Missing build reference:*'
        Assert-True ((@(Get-CacheSnapshot) -join ',') -ceq $snapshot) 'Incomplete source set partially changed the cache.'
        Assert-Rejected { Sync-AstralReferences -GameRoot $gameRoot -WorkRoot (Join-Path $fixture 'new-missing-cache') } '*Missing build reference:*'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture 'new-missing-cache'))) 'Incomplete source set created a cache.'
    }
    Test-Case 'corrupt-late-dependency-preserves-all-cached-dlls-and-metadata' {
        [IO.File]::WriteAllText((Join-Path $sourceRoot $relativePaths[-1]), 'not a managed assembly')
        Assert-Rejected { Assert-AstralReferences -Root $sourceRoot } '*Invalid build reference:*'
        Assert-Rejected { Sync-AstralReferences -GameRoot $gameRoot -WorkRoot $workRoot } '*Invalid build reference:*'
        Assert-True ((@(Get-CacheSnapshot) -join ',') -ceq $snapshot) 'Corrupt source set partially changed the cache.'
        Assert-Rejected { Sync-AstralReferences -GameRoot $gameRoot -WorkRoot (Join-Path $fixture 'new-corrupt-cache') } '*Invalid build reference:*'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture 'new-corrupt-cache'))) 'Corrupt source set created a cache.'
    }
    Copy-Item -LiteralPath $sampleAssembly -Destination (Join-Path $sourceRoot $relativePaths[-1]) -Force
    Test-Case 'recovers-after-source-repair-and-records-changed-assembly' {
        Sync-AstralReferences -GameRoot $gameRoot -WorkRoot $workRoot | Out-Null
        $versions = @(Get-AstralReferenceVersions -Root $refs)
        Assert-True ($versions[0].sha256 -ceq (Get-FileHash -LiteralPath $alternateAssembly).Hash.ToLowerInvariant()) 'Repaired sync failed to record the new assembly.'
        Assert-AstralReferences -Root $refs
    }
    Test-Case 'thin-wrapper-resolves-relative-game-and-work-from-another-directory' {
        Push-Location -LiteralPath $fixture
        try {
            & (Join-Path $root 'scripts/sync-refs.ps1') -GameRoot './game' -WorkRoot './wrapper-cache' | Out-Null
            Assert-AstralReferences -Root './wrapper-cache/refs'
            $versions = @(Get-AstralReferenceVersions -Root './wrapper-cache/refs')
            Assert-True ($versions.Count -eq $relativePaths.Count) 'Relative reference inventory was incomplete.'
            Assert-True (Test-Path -LiteralPath (Join-Path $fixture 'wrapper-cache/refs/versions.json')) 'Wrapper used the process working directory.'
        }
        finally { Pop-Location }
    }
    Write-Output "Reference tests passed: $script:passed cases (no downloads)."
    $global:LASTEXITCODE = 0
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $root '.work/reference-tests')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test fixture location.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
