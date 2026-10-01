param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testParent = Join-Path $repoRoot '.work\install-tests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$script:passed = 0
$installTestState = [pscustomobject]@{ Running = $false; FailInstall = $false }

# Fixtures use a copied installer and dummy DLL, never the real game or build.
function Get-Process {
    param([string[]]$Name, [object]$ErrorAction)
    if ($installTestState.Running) { [pscustomobject]@{ ProcessName = 'AstralParty_INT' } }
}
function Move-Item {
    param([string]$LiteralPath, [string]$Destination)
    if ($installTestState.FailInstall -and [IO.Path]::GetFileName($LiteralPath) -eq 'new-AstralParty.Chat.dll') {
        throw 'Simulated final installation failure.'
    }
    Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
}
function Write-FixtureFile([string]$Path, [string]$Content) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}
function Assert-Content([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or [IO.File]::ReadAllText($Path) -cne $Expected) {
        throw "Unexpected fixture file content: $Path"
    }
}
function Invoke-InstallCase([string]$Name, [scriptblock]$Check) {
    $caseRoot = Join-Path $testRoot $Name
    $fixtureRepo = Join-Path $caseRoot 'repo'
    $gameRoot = Join-Path $caseRoot 'game'
    $plugins = Join-Path $gameRoot 'BepInEx\plugins'
    $canonical = Join-Path $plugins 'AstralPartyChat\AstralParty.Chat.dll'
    $installer = Join-Path $fixtureRepo 'scripts\install.ps1'
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($installer)), $plugins -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\install.ps1') -Destination $installer
    Write-FixtureFile (Join-Path $fixtureRepo 'dist\AstralParty.Chat.dll') 'new-plugin'
    $installTestState.Running = $false
    $installTestState.FailInstall = $false
    & $Check $installer $gameRoot $plugins $canonical $fixtureRepo $caseRoot
    $script:passed++
    Write-Output "PASS install: $Name"
}
function Invoke-ExpectFailure([string]$Installer, [string]$GameRoot, [string]$Message) {
    $failed = $false
    try { & $Installer -GameRoot $GameRoot | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $failed = $true
    }
    if (-not $failed) { throw "Installer should have refused: $Message" }
}

try {
    Invoke-InstallCase 'clean-install' {
        param($installer, $gameRoot, $plugins, $canonical)
        & $installer -GameRoot $gameRoot | Out-Null
        Assert-Content $canonical 'new-plugin'
        if (@(Get-ChildItem -LiteralPath $plugins -Filter 'AstralParty.Chat.dll' -Recurse).Count -ne 1) {
            throw 'Clean install should contain exactly one plugin DLL.'
        }
    }
    Invoke-InstallCase 'root-legacy-migration' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        $legacy = Join-Path $plugins 'AstralParty.Chat.dll'
        Write-FixtureFile $legacy 'old-root-plugin'
        & $installer -GameRoot $gameRoot | Out-Null
        Assert-Content $canonical 'new-plugin'
        if (Test-Path -LiteralPath $legacy) { throw 'Root legacy DLL still loads.' }
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $fixtureRepo '.work\plugin-backups') -File -Recurse)
        if ($backups.Count -ne 1) { throw 'Missing legacy backup.' }
        Assert-Content $backups[0].FullName 'old-root-plugin'
        if ($backups[0].FullName.StartsWith($plugins, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Backup remained in plugin search tree.'
        }
    }
    Invoke-InstallCase 'nested-duplicates-and-other-files' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Write-FixtureFile (Join-Path $plugins 'AstralParty.Chat.dll') 'old-root'
        Write-FixtureFile $canonical 'old-canonical'
        Write-FixtureFile (Join-Path $plugins 'AstralPartyDiagnostics\deeper\astralparty.chat.DLL') 'old-nested'
        $otherDll = Join-Path $plugins 'AstralPartyDiagnostics\Other.dll'
        $otherConfig = Join-Path $plugins 'AstralPartyDiagnostics\settings.cfg'
        Write-FixtureFile $otherDll 'other-plugin'
        Write-FixtureFile $otherConfig 'other-settings'
        & $installer -GameRoot $gameRoot | Out-Null
        Assert-Content $canonical 'new-plugin'
        Assert-Content $otherDll 'other-plugin'
        Assert-Content $otherConfig 'other-settings'
        if (@(Get-ChildItem -LiteralPath $plugins -Filter 'AstralParty.Chat.dll' -Recurse).Count -ne 1) {
            throw 'Duplicate DLL still loads.'
        }
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $fixtureRepo '.work\plugin-backups') -File -Recurse)
        $contents = @($backups | ForEach-Object { [IO.File]::ReadAllText($_.FullName) })
        if ($backups.Count -ne 3 -or @('old-root', 'old-canonical', 'old-nested' | Where-Object { $_ -notin $contents }).Count) {
            throw 'Duplicate migration did not preserve all originals.'
        }
    }
    Invoke-InstallCase 'repeated-install-preserves-backups' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Write-FixtureFile $canonical 'original-plugin'
        & $installer -GameRoot $gameRoot | Out-Null
        & $installer -GameRoot $gameRoot | Out-Null
        Assert-Content $canonical 'new-plugin'
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $fixtureRepo '.work\plugin-backups') -File -Recurse)
        $contents = @($backups | ForEach-Object { [IO.File]::ReadAllText($_.FullName) })
        if ($backups.Count -ne 2 -or 'original-plugin' -notin $contents -or 'new-plugin' -notin $contents) {
            throw 'Repeated installation overwrote a previous backup.'
        }
    }
    Invoke-InstallCase 'running-game-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Write-FixtureFile $canonical 'original-plugin'
        $installTestState.Running = $true
        Invoke-ExpectFailure $installer $gameRoot 'Close the game'
        Assert-Content $canonical 'original-plugin'
        if (Test-Path -LiteralPath (Join-Path $fixtureRepo '.work\plugin-backups')) { throw 'Running-game refusal created backups.' }
    }
    Invoke-InstallCase 'linked-plugin-tree-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $outside = Join-Path $caseRoot 'outside'
        $outsideDll = Join-Path $outside 'AstralParty.Chat.dll'
        Write-FixtureFile $outsideDll 'outside-plugin'
        $link = Join-Path $plugins 'linked'
        New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
        try {
            Invoke-ExpectFailure $installer $gameRoot 'junction or symbolic link'
            Assert-Content $outsideDll 'outside-plugin'
            if (Test-Path -LiteralPath $canonical) { throw 'Linked-tree refusal installed a DLL.' }
        }
        finally {
            # Remove only the fixture junction, without traversing its target.
            if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
        }
    }
    Invoke-InstallCase 'failed-install-restores-originals' {
        param($installer, $gameRoot, $plugins, $canonical)
        $legacy = Join-Path $plugins 'AstralParty.Chat.dll'
        Write-FixtureFile $legacy 'old-root'
        Write-FixtureFile $canonical 'old-canonical'
        $installTestState.FailInstall = $true
        Invoke-ExpectFailure $installer $gameRoot 'Simulated final installation failure'
        Assert-Content $legacy 'old-root'
        Assert-Content $canonical 'old-canonical'
    }
    Write-Output "Install checks passed: $script:passed"
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $boundary = [IO.Path]::GetFullPath($testParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe install-test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
