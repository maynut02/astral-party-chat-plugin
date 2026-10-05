param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testParent = Join-Path $repoRoot '.work\install-tests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:passed = 0
$fixtureLinks = [Collections.Generic.List[string]]::new()
$installTestState = @{}

function Assert-FixturePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = $testRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Test attempted to access a path outside its fixtures: $full"
    }
}

# All process/registry/input results are fixture-controlled. File-operation
# mocks also enforce the fixture boundary before simulating failures.
function Get-Process {
    param([string[]]$Name, [object]$ErrorAction)
    $installTestState.ProcessChecks++
    if ($installTestState.Running -or
        ($installTestState.StartOnCheck -gt 0 -and $installTestState.ProcessChecks -ge $installTestState.StartOnCheck)) {
        [pscustomobject]@{ ProcessName = 'AstralParty_INT' }
    }
}
function Get-ItemProperty {
    param([string]$LiteralPath, [object]$ErrorAction)
    $installTestState.RegistryCalls++
    if ($LiteralPath -eq 'HKCU:\Software\Valve\Steam' -and $installTestState.RegistrySteamRoot) {
        [pscustomobject]@{ SteamPath = $installTestState.RegistrySteamRoot.Replace('\', '/') }
    }
}
function Read-Host {
    param([string]$Prompt)
    $installTestState.PromptCount++
    if ($null -eq $installTestState.PromptAnswer) { throw 'Unexpected interactive prompt.' }
    return $installTestState.PromptAnswer
}
function Copy-Item {
    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
    Assert-FixturePath $LiteralPath
    Assert-FixturePath $Destination
    Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    if ([IO.Path]::GetFileName($Destination) -eq 'new-AstralParty.Chat.dll' -and $installTestState.CorruptStage) {
        [IO.File]::WriteAllText($Destination, 'corrupt-stage')
    }
}
function Move-Item {
    param([string]$LiteralPath, [string]$Destination)
    Assert-FixturePath $LiteralPath
    Assert-FixturePath $Destination
    if ([IO.Path]::GetFileName($LiteralPath) -eq 'new-AstralParty.Chat.dll') {
        if ($installTestState.FailInstall) {
            if ($installTestState.PartialInstall) { [IO.File]::WriteAllText($Destination, 'partial-new-plugin') }
            throw 'Simulated final installation failure.'
        }
        Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
        if ($installTestState.CorruptInstall) { [IO.File]::WriteAllText($Destination, 'corrupt-installed-plugin') }
    }
    else {
        $installTestState.OldMoveCalls++
        if ($installTestState.OldMoveCalls -eq $installTestState.FailOldMoveAt) {
            [IO.File]::WriteAllText($Destination, 'partial-backup')
            throw 'Simulated old DLL migration failure.'
        }
        Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
    }
}
function Write-FixtureFile([string]$Path, [string]$Content) {
    Assert-FixturePath $Path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}
function Write-GameFixture([string]$Root) {
    Write-FixtureFile (Join-Path $Root 'AstralParty_INT.exe') 'fake-game'
    Write-FixtureFile (Join-Path $Root 'BepInEx\core\BepInEx.Core.dll') 'fake-core'
    Write-FixtureFile (Join-Path $Root 'BepInEx\core\BepInEx.Unity.IL2CPP.dll') 'fake-il2cpp'
}
function Assert-Content([string]$Path, [string]$Expected) {
    Assert-FixturePath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or [IO.File]::ReadAllText($Path) -cne $Expected) {
        throw "Unexpected fixture file content: $Path"
    }
}
function Assert-NoBackups([string]$FixtureRepo) {
    if (Test-Path -LiteralPath (Join-Path $FixtureRepo '.work\plugin-backups')) {
        throw 'Refusal created backups before validation completed.'
    }
}
function Assert-OneDll([string]$Plugins) {
    if (@(Get-ChildItem -LiteralPath $Plugins -Filter 'AstralParty.Chat.dll' -Recurse).Count -ne 1) {
        throw 'Expected exactly one DLL in the plugin search tree.'
    }
}
function Get-Backups([string]$FixtureRepo) {
    Get-ChildItem -LiteralPath (Join-Path $FixtureRepo '.work\plugin-backups') -File -Recurse
}
function New-FixtureJunction([string]$Path, [string]$Target) {
    Assert-FixturePath $Path
    Assert-FixturePath $Target
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    New-Item -ItemType Junction -Path $Path -Target $Target | Out-Null
    $fixtureLinks.Add($Path)
}
function Remove-FixtureJunction([string]$Path) {
    Assert-FixturePath $Path
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item) {
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Fixture link became a real directory.' }
        # Delete ONLY the junction, never its target (also on Windows PS 5.1).
        [IO.Directory]::Delete($Path)
    }
}
function ConvertTo-VdfValue([string]$Value) {
    return $Value.Replace('\', '\\').Replace('"', '\"')
}
function New-SteamGame([string]$Library, [string]$Name = 'Astral Party', [string]$AppId = '2622000') {
    $game = Join-Path (Join-Path $Library 'steamapps\common') "$Name\8vJXnINT"
    Write-GameFixture $game
    $manifest = '"AppState" { "appid" "' + $AppId + '" "installdir" "' + (ConvertTo-VdfValue $Name) + '" }'
    Write-FixtureFile (Join-Path $Library 'steamapps\appmanifest_2622000.acf') $manifest
    return $game
}
function Write-LibraryFolders([string]$Steam, [string]$Secondary, [bool]$Modern = $true, [string]$Relative = 'steamapps\libraryfolders.vdf') {
    if ($Modern) {
        $text = '"libraryfolders" { "0" { "path" "' + (ConvertTo-VdfValue $Steam) +
            '" "apps" { "2622000" "42" } } "1" { "path" "' + (ConvertTo-VdfValue $Secondary) +
            '" "apps" { "2622000" "42" } } } // fixture comment'
    }
    else {
        $text = '"LibraryFolders" { "TimeNextStatsReport" "123" "0" "' + (ConvertTo-VdfValue $Steam) +
            '" "1" "' + (ConvertTo-VdfValue $Secondary) + '" }'
    }
    Write-FixtureFile (Join-Path $Steam $Relative) $text
}
function Invoke-InstallCase([string]$Name, [scriptblock]$Check) {
    # Short paths keep nested backups within .NET Framework's MAX_PATH limit.
    $caseRoot = Join-Path $testRoot ('c{0:d2}' -f ($script:passed + 1))
    $fixtureRepo = Join-Path $caseRoot 'repo'
    $gameRoot = Join-Path $caseRoot 'game'
    $plugins = Join-Path $gameRoot 'BepInEx\plugins'
    $canonical = Join-Path $plugins 'AstralPartyChat\AstralParty.Chat.dll'
    $installer = Join-Path $fixtureRepo 'scripts\install.ps1'
    [IO.Directory]::CreateDirectory($plugins) | Out-Null
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($installer)) | Out-Null
    Assert-FixturePath $installer
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\install.ps1') -Destination $installer
    Write-FixtureFile (Join-Path $fixtureRepo 'dist\AstralParty.Chat.dll') 'new-plugin'
    Write-GameFixture $gameRoot
    $installTestState.Clear()
    $installTestState.Running = $false
    $installTestState.FailInstall = $false
    $installTestState.PartialInstall = $false
    $installTestState.CorruptInstall = $false
    $installTestState.CorruptStage = $false
    $installTestState.ProcessChecks = 0
    $installTestState.StartOnCheck = 0
    $installTestState.OldMoveCalls = 0
    $installTestState.FailOldMoveAt = -1
    $installTestState.PromptCount = 0
    $installTestState.PromptAnswer = $null
    $installTestState.RegistryCalls = 0
    $installTestState.RegistrySteamRoot = $null
    & $Check $installer $gameRoot $plugins $canonical $fixtureRepo $caseRoot
    $script:passed++
    Write-Output "PASS install: $Name"
}
function Invoke-ExpectFailure([string]$Installer, [string]$GameRoot, [string]$Message, [hashtable]$Parameters = @{}) {
    $arguments = @{ GameRoot = $GameRoot }
    foreach ($key in $Parameters.Keys) { $arguments[$key] = $Parameters[$key] }
    $failed = $false
    try { & $Installer @arguments 3>$null | Out-Null }
    catch {
        if (-not $_.Exception.Message.Contains($Message)) { throw }
        $failed = $true
    }
    if (-not $failed) { throw "Installer should have refused: $Message" }
}

try {
    # Preserve and strengthen the original seven cases.
    Invoke-InstallCase 'clean-install' {
        param($installer, $gameRoot, $plugins, $canonical)
        $global:LASTEXITCODE = 17
        & $installer -GameRoot $gameRoot | Out-Null
        Assert-Content $canonical 'new-plugin'
        Assert-OneDll $plugins
        if ($global:LASTEXITCODE -ne 0) { throw 'Successful installer retained a stale exit code.' }
    }
    Invoke-InstallCase 'root-legacy-migration' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        $legacy = Join-Path $plugins 'AstralParty.Chat.dll'
        Write-FixtureFile $legacy 'old-root-plugin'
        & $installer -GameRoot $gameRoot | Out-Null
        Assert-Content $canonical 'new-plugin'
        if (Test-Path -LiteralPath $legacy) { throw 'Root legacy DLL still loads.' }
        $backups = @(Get-Backups $fixtureRepo)
        if ($backups.Count -ne 1) { throw 'Missing legacy backup.' }
        Assert-Content $backups[0].FullName 'old-root-plugin'
        if ($backups[0].FullName.StartsWith($gameRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Backup remained in the game directory.'
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
        Assert-OneDll $plugins
        $backups = @(Get-Backups $fixtureRepo)
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
        $parent = Join-Path $fixtureRepo '.work\plugin-backups'
        if (@(Get-ChildItem -LiteralPath $parent -Directory).Count -ne 2) { throw 'Each run needs a unique backup child.' }
        $backups = @(Get-Backups $fixtureRepo)
        $contents = @($backups | ForEach-Object { [IO.File]::ReadAllText($_.FullName) })
        if ($backups.Count -ne 2 -or 'original-plugin' -notin $contents -or 'new-plugin' -notin $contents) {
            throw 'Repeated installation overwrote a previous backup.'
        }
    }
    Invoke-InstallCase 'running-game-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Write-FixtureFile $canonical 'original-plugin'
        $installTestState.Running = $true
        Invoke-ExpectFailure $installer $gameRoot '게임을 종료'
        Assert-Content $canonical 'original-plugin'
        Assert-NoBackups $fixtureRepo
    }
    Invoke-InstallCase 'linked-plugin-tree-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $outside = Join-Path $caseRoot 'outside'
        $outsideDll = Join-Path $outside 'AstralParty.Chat.dll'
        Write-FixtureFile $outsideDll 'outside-plugin'
        $link = Join-Path $plugins 'linked'
        New-FixtureJunction $link $outside
        try {
            Invoke-ExpectFailure $installer $gameRoot '정션 또는 심볼릭 링크'
            Assert-Content $outsideDll 'outside-plugin'
            Assert-NoBackups $fixtureRepo
            if (Test-Path -LiteralPath $canonical) { throw 'Linked-tree refusal installed a DLL.' }
        }
        finally { Remove-FixtureJunction $link }
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

    Invoke-InstallCase 'detect-primary-steam-library' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $detected = New-SteamGame $steam
        & $installer -SteamRoot $steam -Interactive | Out-Null
        Assert-Content (Join-Path $detected 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
        if ($installTestState.PromptCount -ne 0 -or $installTestState.RegistryCalls -ne 0) {
            throw 'Single explicit-Steam match prompted or inspected the real registry.'
        }
        if (Test-Path -LiteralPath $canonical) { throw 'Discovery touched the unrelated fixture game.' }
    }
    Invoke-InstallCase 'detect-secondary-modern-vdf' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $secondary = Join-Path $caseRoot '보조 라이브러리 [게임]'
        $detected = New-SteamGame $secondary
        Write-LibraryFolders $steam $secondary
        & $installer -SteamRoot $steam | Out-Null
        Assert-Content (Join-Path $detected 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
    }
    Invoke-InstallCase 'detect-secondary-legacy-vdf' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $secondary = Join-Path $caseRoot 'Legacy Library'
        $detected = New-SteamGame $secondary
        Write-LibraryFolders $steam $secondary $false
        & $installer -SteamRoot $steam | Out-Null
        Assert-Content (Join-Path $detected 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
    }
    Invoke-InstallCase 'detect-legacy-config-vdf' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $secondary = Join-Path $caseRoot 'Other Library'
        $detected = New-SteamGame $secondary
        Write-LibraryFolders $steam $secondary $false 'config\libraryfolders.vdf'
        & $installer -SteamRoot $steam | Out-Null
        Assert-Content (Join-Path $detected 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
    }
    Invoke-InstallCase 'detect-registry-root-without-real-steam' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Registry Steam'
        $detected = New-SteamGame $steam
        $installTestState.RegistrySteamRoot = $steam
        $savedX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        $savedProgramFiles = $env:ProgramFiles
        try {
            [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', (Join-Path $caseRoot 'Empty x86'))
            $env:ProgramFiles = Join-Path $caseRoot 'Empty ProgramFiles'
            & $installer | Out-Null
            Assert-Content (Join-Path $detected 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
            if ($installTestState.RegistryCalls -ne 3) { throw 'Registry Steam root was not queried.' }
        }
        finally {
            [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', $savedX86)
            $env:ProgramFiles = $savedProgramFiles
        }
    }
    Invoke-InstallCase 'duplicate-library-is-one-match' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $detected = New-SteamGame $steam
        Write-LibraryFolders $steam $steam.ToUpperInvariant()
        & $installer -SteamRoot $steam -Interactive | Out-Null
        Assert-Content (Join-Path $detected 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
        if ($installTestState.PromptCount) { throw 'Duplicate library produced an unnecessary prompt.' }
    }
    Invoke-InstallCase 'wrong-app-id-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $wrong = New-SteamGame $steam 'Astral Party' '999'
        Invoke-ExpectFailure $installer '' '자동으로 찾지 못했습니다' @{ SteamRoot = $steam }
        Assert-NoBackups $fixtureRepo
        if (Test-Path -LiteralPath (Join-Path $wrong 'BepInEx\plugins')) { throw 'Wrong AppID was installed.' }
    }
    Invoke-InstallCase 'unsafe-manifest-install-dirs-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $outsideGame = Join-Path $steam 'steamapps\outside\8vJXnINT'
        Write-GameFixture $outsideGame
        foreach ($unsafeName in @('..\outside', '../outside', $gameRoot, '\outside', 'C:outside', '..', '', 'Astral Party.')) {
            $manifest = '"AppState" { "appid" "2622000" "installdir" "' + (ConvertTo-VdfValue $unsafeName) + '" }'
            Write-FixtureFile (Join-Path $steam 'steamapps\appmanifest_2622000.acf') $manifest
            Invoke-ExpectFailure $installer '' '자동으로 찾지 못했습니다' @{ SteamRoot = $steam }
            Assert-NoBackups $fixtureRepo
        }
        if (Test-Path -LiteralPath (Join-Path $outsideGame 'BepInEx\plugins')) { throw 'Manifest traversed outside common.' }
    }
    Invoke-InstallCase 'malformed-manifest-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $null = New-SteamGame $steam
        foreach ($manifest in @(
            '"AppState" { "appid" "2622000" "installdir" "Astral Party"',
            '"AppState" { "appid" "2622000" "appid" "999" "installdir" "Astral Party" }'
        )) {
            Write-FixtureFile (Join-Path $steam 'steamapps\appmanifest_2622000.acf') $manifest
            Invoke-ExpectFailure $installer '' '자동으로 찾지 못했습니다' @{ SteamRoot = $steam }
        }
        Assert-NoBackups $fixtureRepo
    }
    Invoke-InstallCase 'no-auto-game-refused-without-prompt' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Empty Steam'
        [IO.Directory]::CreateDirectory($steam) | Out-Null
        Invoke-ExpectFailure $installer '' '자동으로 찾지 못했습니다' @{ SteamRoot = $steam }
        Assert-NoBackups $fixtureRepo
        if ($installTestState.PromptCount -or $installTestState.RegistryCalls) { throw 'Quiet discovery escaped its fixture or prompted.' }
    }
    Invoke-InstallCase 'ambiguous-auto-game-refused-without-prompt' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $secondary = Join-Path $caseRoot 'Secondary'
        $first = New-SteamGame $steam
        $second = New-SteamGame $secondary
        Write-LibraryFolders $steam $secondary
        Invoke-ExpectFailure $installer '' '설치가 여러 개' @{ SteamRoot = $steam }
        Assert-NoBackups $fixtureRepo
        if ($installTestState.PromptCount) { throw 'Quiet ambiguous discovery prompted.' }
        foreach ($root in @($first, $second)) {
            if (Test-Path -LiteralPath (Join-Path $root 'BepInEx\plugins')) { throw 'Ambiguous discovery wrote a plugin.' }
        }
    }
    Invoke-InstallCase 'interactive-no-match-manual-parent' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Empty Steam'
        [IO.Directory]::CreateDirectory($steam) | Out-Null
        $parent = Join-Path $caseRoot '수동 게임 Astral Party'
        $manual = Join-Path $parent '8vJXnINT'
        Write-GameFixture $manual
        $installTestState.PromptAnswer = '"' + $parent + '"'
        & $installer -SteamRoot $steam -Interactive | Out-Null
        Assert-Content (Join-Path $manual 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
        if ($installTestState.PromptCount -ne 1) { throw 'Manual fallback must prompt exactly once.' }
    }
    Invoke-InstallCase 'interactive-ambiguous-choice' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $secondary = Join-Path $caseRoot 'Secondary'
        $first = New-SteamGame $steam
        $second = New-SteamGame $secondary
        Write-LibraryFolders $steam $secondary
        $installTestState.PromptAnswer = $second
        & $installer -SteamRoot $steam -Interactive | Out-Null
        Assert-Content (Join-Path $second 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
        if ($installTestState.PromptCount -ne 1 -or (Test-Path -LiteralPath (Join-Path $first 'BepInEx\plugins'))) {
            throw 'Ambiguous interactive selection wrote the wrong game or prompted twice.'
        }
    }
    Invoke-InstallCase 'explicit-parent-overrides-discovery' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $parent = Join-Path $caseRoot 'Astral Party'
        $selected = Join-Path $parent '8vJXnINT'
        Write-GameFixture $selected
        & $installer -GameRoot $parent -SteamRoot (Join-Path $caseRoot 'Unused Steam') -Interactive | Out-Null
        Assert-Content (Join-Path $selected 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'new-plugin'
        if ($installTestState.PromptCount -or $installTestState.RegistryCalls) { throw 'Explicit GameRoot triggered discovery or confirmation.' }
    }
    Invoke-InstallCase 'embedded-scriptblock-custom-unicode-paths' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $selected = Join-Path $caseRoot '한글 게임 [설치] 폴더\8vJXnINT'
        Write-GameFixture $selected
        $dll = Join-Path $caseRoot '배포 DLL [새 파일]\payload.dll'
        $backups = Join-Path $caseRoot '백업 부모 [보관] 폴더'
        Write-FixtureFile $dll 'custom-plugin'
        $old = Join-Path $selected 'BepInEx\plugins\AstralParty.Chat.dll'
        Write-FixtureFile $old 'custom-old'
        $embedded = [scriptblock]::Create([IO.File]::ReadAllText($installer, [Text.Encoding]::UTF8))
        # Invoke from an empty-PSScriptRoot scope, just like powershell -Command.
        & {
            $PSScriptRoot = ''
            & $embedded -GameRoot $selected -DllPath $dll -BackupRoot $backups -Interactive | Out-Null
            & $embedded -GameRoot $selected -DllPath $dll -BackupRoot $backups | Out-Null
        }
        Assert-Content (Join-Path $selected 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll') 'custom-plugin'
        Assert-Content $dll 'custom-plugin'
        if (Test-Path -LiteralPath $old) { throw 'Embedded install left a legacy DLL.' }
        if (@(Get-ChildItem -LiteralPath $backups -Directory).Count -ne 2) { throw 'Custom backups were not unique per run.' }
        $files = @(Get-ChildItem -LiteralPath $backups -File -Recurse)
        if ($files.Count -ne 2) { throw 'Custom backup parent lost an original or retained a staged DLL.' }
        Assert-NoBackups $fixtureRepo
        if ($installTestState.PromptCount) { throw 'Explicit embedded install prompted.' }
    }
    Invoke-InstallCase 'source-is-already-installed-dll' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        Write-FixtureFile $canonical 'same-file-source'
        $backups = Join-Path $caseRoot 'external-backups'
        & $installer -GameRoot $gameRoot -DllPath $canonical -BackupRoot $backups | Out-Null
        Assert-Content $canonical 'same-file-source'
        $files = @(Get-ChildItem -LiteralPath $backups -File -Recurse)
        if ($files.Count -ne 1) { throw 'Same-file source backup is missing.' }
        Assert-Content $files[0].FullName 'same-file-source'
        Assert-OneDll $plugins
    }
    Invoke-InstallCase 'canonical-dll-path-is-directory-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        [IO.Directory]::CreateDirectory($canonical) | Out-Null
        Invoke-ExpectFailure $installer $gameRoot 'DLL 경로가 폴더'
        Assert-NoBackups $fixtureRepo
        if (@(Get-ChildItem -LiteralPath $canonical -Force).Count) { throw 'DLL was moved into an existing directory.' }
    }
    Invoke-InstallCase 'missing-source-dll-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        Invoke-ExpectFailure $installer $gameRoot 'DLL 파일이 없습니다' @{ DllPath = (Join-Path $caseRoot 'missing.dll') }
        Assert-NoBackups $fixtureRepo
    }
    Invoke-InstallCase 'game-folder-without-executable-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Remove-Item -LiteralPath (Join-Path $gameRoot 'AstralParty_INT.exe') -Force
        Invoke-ExpectFailure $installer $gameRoot 'AstralParty_INT.exe가 있는'
        Assert-NoBackups $fixtureRepo
    }
    Invoke-InstallCase 'missing-game-directory-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        Invoke-ExpectFailure $installer (Join-Path $caseRoot 'missing-game') '존재하는 폴더'
        Assert-NoBackups $fixtureRepo
    }
    Invoke-InstallCase 'missing-bepinex-core-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Remove-Item -LiteralPath (Join-Path $gameRoot 'BepInEx\core\BepInEx.Core.dll') -Force
        Invoke-ExpectFailure $installer $gameRoot '초기화된 BepInEx IL2CPP'
        Assert-NoBackups $fixtureRepo
    }
    Invoke-InstallCase 'missing-bepinex-il2cpp-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Remove-Item -LiteralPath (Join-Path $gameRoot 'BepInEx\core\BepInEx.Unity.IL2CPP.dll') -Force
        Invoke-ExpectFailure $installer $gameRoot '초기화된 BepInEx IL2CPP'
        Assert-NoBackups $fixtureRepo
    }
    Invoke-InstallCase 'detected-game-without-bepinex-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $detected = New-SteamGame $steam
        Remove-Item -LiteralPath (Join-Path $detected 'BepInEx\core\BepInEx.Unity.IL2CPP.dll') -Force
        Invoke-ExpectFailure $installer '' '초기화된 BepInEx IL2CPP' @{ SteamRoot = $steam; Interactive = $true }
        Assert-NoBackups $fixtureRepo
        if ($installTestState.PromptCount) { throw 'Single uninitialized game prompted unnecessarily.' }
    }
    Invoke-InstallCase 'backups-inside-game-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Write-FixtureFile $canonical 'old-plugin'
        foreach ($backup in @($gameRoot, (Join-Path $gameRoot 'backups'), (Join-Path $plugins 'backups'))) {
            Invoke-ExpectFailure $installer $gameRoot '게임 폴더 밖' @{ BackupRoot = $backup }
            Assert-Content $canonical 'old-plugin'
        }
        Assert-NoBackups $fixtureRepo
    }
    Invoke-InstallCase 'linked-game-ancestor-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $link = Join-Path $caseRoot 'linked-parent'
        New-FixtureJunction $link $caseRoot
        try {
            Invoke-ExpectFailure $installer (Join-Path $link 'game') '정션 또는 심볼릭 링크'
            Assert-NoBackups $fixtureRepo
        }
        finally { Remove-FixtureJunction $link }
    }
    Invoke-InstallCase 'linked-backup-ancestor-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $outside = Join-Path $caseRoot 'outside-backups'
        [IO.Directory]::CreateDirectory($outside) | Out-Null
        $link = Join-Path $caseRoot 'linked-backups'
        New-FixtureJunction $link $outside
        try {
            Invoke-ExpectFailure $installer $gameRoot '정션 또는 심볼릭 링크' @{ BackupRoot = (Join-Path $link 'new\parent') }
            if (@(Get-ChildItem -LiteralPath $outside -Force).Count) { throw 'Backup escaped through a junction.' }
            Assert-NoBackups $fixtureRepo
        }
        finally { Remove-FixtureJunction $link }
    }
    Invoke-InstallCase 'linked-dll-ancestor-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $link = Join-Path $caseRoot 'linked-payload'
        New-FixtureJunction $link (Join-Path $fixtureRepo 'dist')
        try {
            Invoke-ExpectFailure $installer $gameRoot '정션 또는 심볼릭 링크' @{ DllPath = (Join-Path $link 'AstralParty.Chat.dll') }
            Assert-NoBackups $fixtureRepo
        }
        finally { Remove-FixtureJunction $link }
    }
    Invoke-InstallCase 'linked-steam-library-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $target = Join-Path $caseRoot 'Outside Library'
        $outsideGame = New-SteamGame $target
        $link = Join-Path $caseRoot 'Linked Library'
        New-FixtureJunction $link $target
        try {
            Write-LibraryFolders $steam $link
            Invoke-ExpectFailure $installer '' '자동으로 찾지 못했습니다' @{ SteamRoot = $steam }
            Assert-NoBackups $fixtureRepo
            if (Test-Path -LiteralPath (Join-Path $outsideGame 'BepInEx\plugins')) { throw 'Discovery followed a library junction.' }
        }
        finally { Remove-FixtureJunction $link }
    }
    Invoke-InstallCase 'linked-manifest-game-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo, $caseRoot)
        $steam = Join-Path $caseRoot 'Steam'
        $parent = Join-Path $steam 'steamapps\common\Astral Party'
        $link = Join-Path $steam 'steamapps\common\Linked Game'
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        Write-GameFixture (Join-Path $parent '8vJXnINT')
        Write-FixtureFile (Join-Path $steam 'steamapps\appmanifest_2622000.acf') '"AppState" { "appid" "2622000" "installdir" "Linked Game" }'
        New-FixtureJunction $link $parent
        try {
            Invoke-ExpectFailure $installer '' '자동으로 찾지 못했습니다' @{ SteamRoot = $steam }
            Assert-NoBackups $fixtureRepo
        }
        finally { Remove-FixtureJunction $link }
    }
    Invoke-InstallCase 'game-started-during-staging-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Write-FixtureFile $canonical 'old-plugin'
        $installTestState.StartOnCheck = 2
        Invoke-ExpectFailure $installer $gameRoot '게임을 종료'
        Assert-Content $canonical 'old-plugin'
        if (@(Get-Backups $fixtureRepo).Count) { throw 'Recheck refusal moved an original or left a staged file.' }
    }
    Invoke-InstallCase 'staging-hash-mismatch-refused' {
        param($installer, $gameRoot, $plugins, $canonical, $fixtureRepo)
        Write-FixtureFile $canonical 'old-plugin'
        $installTestState.CorruptStage = $true
        Invoke-ExpectFailure $installer $gameRoot '임시 복사본 해시'
        Assert-Content $canonical 'old-plugin'
        if (@(Get-Backups $fixtureRepo).Count) { throw 'Corrupt stage was retained or original was moved.' }
    }
    Invoke-InstallCase 'partial-new-write-restores-originals' {
        param($installer, $gameRoot, $plugins, $canonical)
        $legacy = Join-Path $plugins 'AstralParty.Chat.dll'
        Write-FixtureFile $legacy 'old-root'
        Write-FixtureFile $canonical 'old-canonical'
        $installTestState.FailInstall = $true
        $installTestState.PartialInstall = $true
        Invoke-ExpectFailure $installer $gameRoot 'Simulated final installation failure'
        Assert-Content $legacy 'old-root'
        Assert-Content $canonical 'old-canonical'
    }
    Invoke-InstallCase 'partial-new-write-clean-install-removes-dll' {
        param($installer, $gameRoot, $plugins, $canonical)
        $installTestState.FailInstall = $true
        $installTestState.PartialInstall = $true
        Invoke-ExpectFailure $installer $gameRoot 'Simulated final installation failure'
        if (Test-Path -LiteralPath $canonical) { throw 'Failed clean install retained a partially written DLL.' }
    }
    Invoke-InstallCase 'installed-hash-mismatch-restores-original' {
        param($installer, $gameRoot, $plugins, $canonical)
        Write-FixtureFile $canonical 'old-canonical'
        $installTestState.CorruptInstall = $true
        Invoke-ExpectFailure $installer $gameRoot '설치된 DLL의 해시'
        Assert-Content $canonical 'old-canonical'
    }
    Invoke-InstallCase 'failed-migration-restores-earlier-moves' {
        param($installer, $gameRoot, $plugins, $canonical)
        $legacy = Join-Path $plugins 'AstralParty.Chat.dll'
        Write-FixtureFile $legacy 'old-root'
        Write-FixtureFile $canonical 'old-canonical'
        $installTestState.FailOldMoveAt = 2
        Invoke-ExpectFailure $installer $gameRoot 'Simulated old DLL migration failure'
        Assert-Content $legacy 'old-root'
        Assert-Content $canonical 'old-canonical'
    }
    Invoke-InstallCase 'utf8-bom-required-for-windows-powershell' {
        param($installer)
        foreach ($path in @($installer, $PSCommandPath)) {
            $bytes = [IO.File]::ReadAllBytes($path)
            if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
                throw "Windows PowerShell script lacks a UTF-8 BOM: $path"
            }
        }
    }
    Write-Output "Install checks passed: $script:passed"
}
finally {
    foreach ($link in $fixtureLinks) { Remove-FixtureJunction $link }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $boundary = [IO.Path]::GetFullPath($testParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe install-test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

# Set this AFTER cleanup; native tools in CI may have left an unrelated code.
$global:LASTEXITCODE = 0
