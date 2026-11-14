#requires -Version 7.0
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$installer = Join-Path $repoRoot 'installer/install.ps1'
$testParent = Join-Path $repoRoot '.work/installer-tests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
$utf8 = [Text.UTF8Encoding]::new($false)
$freshConfig = "[Logging]`r`nUnityLogListening = false`r`n[Logging.Console]`r`nEnabled = false`r`n"
$passed = 0
$powershell = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

function Write-Fixture([string]$Path, [string]$Text = 'fixture') {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Text([string]$Path, [string]$Expected) {
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) "Missing fixture output: $Path"
    $actual = [IO.File]::ReadAllText($Path)
    if ($actual -cne $Expected) { throw "Unexpected content in ${Path}: <$actual>, expected <$Expected>" }
}

function Invoke-InstallerCase([string]$Name, [scriptblock]$Test) {
    & $Test
    $script:passed++
    Write-Output "PASS installer: $Name"
}

function New-Package([string]$Name) {
    $root = Join-Path $testRoot $Name
    foreach ($relative in @(
        'BepInEx/core/BepInEx.Core.dll', 'BepInEx/core/Il2CppInterop.Runtime.dll',
        'BepInEx/patchers/runtime-patcher.dll', 'dotnet/host/fxr/fixture.dll',
        'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt', 'LICENSE-BepInEx.txt',
        'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll'
    )) { Write-Fixture (Join-Path $root $relative) "package:$relative" }
    Write-Fixture (Join-Path $root 'BepInEx/config/BepInEx.cfg') $freshConfig
    Write-Fixture (Join-Path $root 'original-game.dat') 'must never be copied'
    Copy-Item -LiteralPath $installer -Destination (Join-Path $root 'install.ps1')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'installer/install.bat') -Destination (Join-Path $root 'install.bat')
    return $root
}

function New-Game([string]$Root) {
    Write-Fixture (Join-Path $Root 'AstralParty_INT.exe') 'original executable'
    Write-Fixture (Join-Path $Root 'AstralParty_Data/globalgamemanagers') 'original game data'
    return $Root
}

function New-Steam([string]$Name) {
    $root = Join-Path $testRoot "$Name/Steam"
    $library = Join-Path $testRoot "$Name/Second Steam Library"
    [IO.Directory]::CreateDirectory($library) | Out-Null
    $escaped = $library.Replace('\', '\\')
    Write-Fixture (Join-Path $root 'steamapps/libraryfolders.vdf') @"
"libraryfolders"
{
    "0" { "path" "$($root.Replace('\', '\\'))" }
    "1"
    {
        "path" "$escaped"
        "apps" { "2622000" "123" }
    }
}
"@
    return @{ Root = $root; Library = $library }
}

# The child really executes the installer in Windows PowerShell 5.1. Only process discovery, the Steam
# session registry entry and launching are replaced, so tests cannot inspect or launch real games or Steam.
$runner = Join-Path $testRoot 'run-installer.ps1'
Write-Fixture $runner @'
#requires -Version 5.1
param([string]$Installer, [string]$ArgumentsPath, [string]$StateRoot, [string]$Mode)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Expected Windows PowerShell 5.1.' }
[IO.File]::WriteAllText((Join-Path $StateRoot 'ansi-codepage.txt'), [string][Text.Encoding]::Default.CodePage)
# Mode nosteam: Steam is closed until the installer starts it.
function Test-FakeSteamRunning {
    if ($Mode -ne 'nosteam') { return $true }
    return [IO.File]::Exists((Join-Path $StateRoot 'steam.json'))
}
function Get-Process {
    param([string[]]$Name, [string]$ErrorAction)
    if ($Name -contains 'steam') {
        if (-not (Test-FakeSteamRunning)) { return }
        return [pscustomobject]@{ ProcessName = 'steam'; Id = 4242; MainWindowHandle = [IntPtr]::Zero }
    }
    if ($Mode -eq 'running') { return [pscustomobject]@{ ProcessName = 'AstralParty_INT' } }
    # After the fake launch, report the game with a window so the installer stops waiting.
    if ([IO.File]::Exists((Join-Path $StateRoot 'launch.json'))) { return [pscustomobject]@{ ProcessName = $Name[0]; MainWindowHandle = [IntPtr]1 } }
    $retried = Join-Path $StateRoot 'retried'
    if ($Mode -eq 'retry' -and -not [IO.File]::Exists($retried)) {
        [IO.File]::WriteAllText($retried, 'first probe')
        return [pscustomobject]@{ ProcessName = 'AstralParty_INT' }
    }
}
function Get-ItemProperty {
    param([string]$LiteralPath, [string]$ErrorAction)
    if ($LiteralPath -ne 'HKCU:\Software\Valve\Steam\ActiveProcess') { return }
    # Signed in only while the fake Steam runs; a closed Steam leaves a stale pid with no account.
    if (Test-FakeSteamRunning) { return [pscustomobject]@{ pid = 4242; ActiveUser = 77 } }
    return [pscustomobject]@{ pid = 4242; ActiveUser = 0 }
}
function Start-Process {
    param([string]$FilePath, [string]$WorkingDirectory, [string[]]$ArgumentList)
    if ([IO.Path]::GetFileName($FilePath) -eq 'steam.exe') {
        $gameStarted = [IO.File]::Exists((Join-Path $StateRoot 'launch.json'))
        @{ FilePath = $FilePath; ArgumentList = $ArgumentList; GameStartedFirst = $gameStarted } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StateRoot 'steam.json') -Encoding UTF8
        return
    }
    @{ FilePath = $FilePath; WorkingDirectory = $WorkingDirectory; SteamAppId = $env:SteamAppId; SteamGameId = $env:SteamGameId } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StateRoot 'launch.json') -Encoding UTF8
}
$arguments = @{}
$values = [IO.File]::ReadAllText($ArgumentsPath) | ConvertFrom-Json
foreach ($property in $values.PSObject.Properties) { $arguments[$property.Name] = $property.Value }
& $Installer @arguments
exit $LASTEXITCODE
'@

function Invoke-Install([hashtable]$Arguments, [string[]]$InputLines = @(), [string]$Mode = '') {
    $state = Join-Path $testRoot ('invocation-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($state) | Out-Null
    $argumentsPath = Join-Path $state 'arguments.json'
    Write-Fixture $argumentsPath ($Arguments | ConvertTo-Json)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $powershell
    $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Installer "{1}" -ArgumentsPath "{2}" -StateRoot "{3}"' -f $runner, $installer, $argumentsPath, $state
    if ($Mode) { $start.Arguments += ' -Mode "' + $Mode + '"' }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        $process.Start() | Out-Null
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        foreach ($line in $InputLines) { $process.StandardInput.WriteLine($line) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Installer child timed out (unexpected prompt or hang).' }
        $result = [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult(); State = $state }
        $script:windowsAnsiCodePage = [int][IO.File]::ReadAllText((Join-Path $state 'ansi-codepage.txt'))
        return $result
    } finally { $process.Dispose() }
}

function Assert-Success($Result) {
    if ($Result.ExitCode -ne 0) { throw "Installer failed: $($Result.Output)" }
}

function Assert-OriginalGame([string]$Root) {
    Assert-Text (Join-Path $Root 'AstralParty_INT.exe') 'original executable'
    Assert-Text (Join-Path $Root 'AstralParty_Data/globalgamemanagers') 'original game data'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $Root 'original-game.dat'))) 'Installer copied an unrelated root file.'
}

# Every copy of this plugin BepInEx could load from the game, wherever it is and whatever it is called.
function Get-LoadablePlugins([string]$Game) {
    @(Get-ChildItem -LiteralPath (Join-Path $Game 'BepInEx/plugins') -Recurse -File -Force |
        Where-Object { $_.Name -in @('AstralPartyChatPlugin.dll', 'AstralParty.Chat.dll') } |
        ForEach-Object { $_.FullName.Substring($Game.Length + 1).Replace('\', '/') } | Sort-Object)
}

try {
    $package = New-Package 'full package'
    $emptySteam = New-Steam 'empty'
    $plugin = 'BepInEx/plugins/AstralPartyChatPlugin'
    $pluginDll = "$plugin/AstralPartyChatPlugin.dll"
    $cfgRelative = 'BepInEx/config/BepInEx.cfg'
    $consoleLf = "[Logging.Console]`nEnabled = false`n"
    $consoleCrLf = "[Logging.Console]`r`nEnabled = false`r`n"

    Invoke-InstallerCase 'distribution encodings: ASCII batch and BOM UTF-8 PowerShell' {
        $bytes = [IO.File]::ReadAllBytes($installer)
        Assert-True ($bytes.Length -gt 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) 'Installer needs a UTF-8 BOM for Korean in Windows PowerShell.'
        $batchBytes = [IO.File]::ReadAllBytes((Join-Path $repoRoot 'installer/install.bat'))
        Assert-True (@($batchBytes | Where-Object { $_ -gt 127 }).Count -eq 0) 'Batch must be ASCII.'
        $batch = [Text.Encoding]::ASCII.GetString($batchBytes)
        Assert-True ($batch.Contains('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*') -and $batch.Contains('pause')) 'Batch must forward arguments and pause.'
    }
    Invoke-InstallerCase 'single installation discovered in escaped second Steam library; fresh runtime and NoLaunch' {
        $steam = New-Steam 'single'
        $game = New-Game (Join-Path $steam.Library 'steamapps/common/Astral Party/8vJXnINT')
        $result = Invoke-Install @{ PackageRoot = $package; SteamRoot = $steam.Root; Yes = $true; NoLaunch = $true }
        Assert-Success $result
        foreach ($relative in @('BepInEx/core/BepInEx.Core.dll', 'dotnet/host/fxr/fixture.dll', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt', 'LICENSE-BepInEx.txt', 'BepInEx/patchers/runtime-patcher.dll', $pluginDll)) {
            Assert-Text (Join-Path $game $relative) "package:$relative"
        }
        Assert-Text (Join-Path $game $cfgRelative) $freshConfig
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $game 'install.ps1'))) 'Installer scripts must not be copied.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $result.State 'launch.json'))) '-NoLaunch launched the game.'
        Assert-OriginalGame $game
    }
    Invoke-InstallerCase 'several installations require a choice; the chosen one alone is installed' {
        $steam = New-Steam 'multiple'
        $first = New-Game (Join-Path $steam.Root 'steamapps/common/Astral Party/8vJXnINT')
        $second = New-Game (Join-Path $steam.Library 'steamapps/common/Astral Party/8vJXnINT')
        $result = Invoke-Install @{ PackageRoot = $package; SteamRoot = $steam.Root; Yes = $true; NoLaunch = $true }
        Assert-True ($result.ExitCode -ne 0) 'Ambiguous non-interactive discovery must fail.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $first 'BepInEx')) -and -not (Test-Path -LiteralPath (Join-Path $second 'BepInEx'))) 'Ambiguous selection modified a game.'
        $result = Invoke-Install @{ PackageRoot = $package; SteamRoot = $steam.Root; NoLaunch = $true } @('9', '2')
        Assert-Success $result
        $listed = @([regex]::Matches($result.Output, '(?m)^  (\d)\. (.+?)\r?$') | ForEach-Object { $_.Groups[2].Value })
        Assert-True ($listed.Count -ge 2 -and (@($listed[0..1] | Sort-Object) -join '|') -ceq (@($first, $second | Sort-Object) -join '|')) "Both installations were not offered: $($result.Output)"
        $chosen = $listed[1]
        $other = @($first, $second | Where-Object { $_ -cne $chosen })[0]
        Assert-Text (Join-Path $chosen $pluginDll) "package:$pluginDll"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $other 'BepInEx'))) 'The installation that was not chosen was modified.'
        Assert-Success (Invoke-Install @{ PackageRoot = $package; SteamRoot = $steam.Root; GameRoot = $other; Yes = $true; NoLaunch = $true })
        Assert-Text (Join-Path $other $pluginDll) "package:$pluginDll"
    }
    Invoke-InstallerCase 'no discovered game rejects Yes, accepts explicit GameRoot, its parent or a prompted folder' {
        $result = Invoke-Install @{ PackageRoot = $package; SteamRoot = $emptySteam.Root; Yes = $true; NoLaunch = $true }
        Assert-True ($result.ExitCode -ne 0) 'Missing discovered game must fail without prompting under Yes.'
        $explicit = New-Game (Join-Path $testRoot 'explicit game')
        Assert-Success (Invoke-Install @{ PackageRoot = $package; SteamRoot = $emptySteam.Root; GameRoot = $explicit; Yes = $true; NoLaunch = $true })
        Assert-Text (Join-Path $explicit $pluginDll) "package:$pluginDll"
        # Players often point at "Astral Party" rather than the 8vJXnINT folder inside it.
        $parent = Join-Path $testRoot 'parent game/Astral Party'
        $nested = New-Game (Join-Path $parent '8vJXnINT')
        Assert-Success (Invoke-Install @{ PackageRoot = $package; SteamRoot = $emptySteam.Root; GameRoot = $parent; Yes = $true; NoLaunch = $true })
        Assert-Text (Join-Path $nested $pluginDll) "package:$pluginDll"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $parent 'BepInEx'))) 'Installer wrote beside the game folder instead of into it.'
        $prompted = New-Game (Join-Path $testRoot 'prompted game')
        Assert-Success (Invoke-Install @{ PackageRoot = $package; SteamRoot = $emptySteam.Root; NoLaunch = $true } @('"' + $prompted + '"'))
        Assert-Text (Join-Path $prompted $pluginDll) "package:$pluginDll"
        $result = Invoke-Install @{ PackageRoot = $package; SteamRoot = $emptySteam.Root; NoLaunch = $true } @('')
        Assert-True ($result.ExitCode -ne 0) 'Cancelling folder prompt should return a nonzero exit.'
    }
    Invoke-InstallerCase 'existing runtime stays intact; every previous copy of the plugin is removed and only the new DLL remains' {
        $game = New-Game (Join-Path $testRoot 'existing runtime')
        foreach ($relative in @('BepInEx/core/BepInEx.Core.dll', 'dotnet/host/fxr/fixture.dll', 'winhttp.dll', 'doorstop_config.ini')) {
            Write-Fixture (Join-Path $game $relative) "existing:$relative"
        }
        $previous = @(
            'BepInEx/plugins/AstralPartyChatPlugin.dll', 'BepInEx/plugins/AstralParty.Chat.dll',
            'BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll', "$plugin/AstralParty.Chat.dll",
            'BepInEx/plugins/AstralPartyChatPlugin-v0.2.0/BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll',
            'BepInEx/plugins/Shared Folder/astralpartychatplugin.DLL',
            "$plugin/AstralPartyChatPlugin.dll.new", "$plugin/AstralPartyChatPlugin.dll.new.tmp", "$plugin/AstralPartyChatPlugin.dll.old",
            'BepInEx/plugins/AstralPartyChatPlugin.dll.old'
        )
        foreach ($relative in $previous) { Write-Fixture (Join-Path $game $relative) 'previous version' }
        Write-Fixture (Join-Path $game $pluginDll) 'old plugin'
        $kept = @{
            'BepInEx/plugins/Shared Folder/other-plugin.dll' = 'another plugin beside an old copy'
            'BepInEx/plugins/AstralPartyKoreanPlugin/AstralPartyKoreanPlugin.dll' = 'korean plugin'
            'BepInEx/plugins/AstralPartyKoreanPlugin/AstralPartyKoreanPlugin.dll.old' = 'korean plugin backup'
            'BepInEx/plugins/Helper/AstralPartyChatPluginHelper.dll' = 'similar name'
            "$plugin/notes.txt" = 'player file in the plugin folder'
            'BepInEx/config/astral-party.chat.cfg' = "[Update]`r`nAutoUpdate = false`r`n"
            'BepInEx/patchers/AstralPartyChatPlugin.dll' = 'outside plugins'
        }
        foreach ($entry in $kept.GetEnumerator()) { Write-Fixture (Join-Path $game $entry.Key) $entry.Value }
        # A developer's plugins folder may link to a build output outside the game; the installer must not reach through it.
        $outside = Join-Path $testRoot 'build output outside the game'
        Write-Fixture (Join-Path $outside 'AstralPartyChatPlugin.dll') 'outside the game'
        New-Item -ItemType Junction -Path (Join-Path $game 'BepInEx/plugins/Linked Build') -Target $outside | Out-Null

        $result = Invoke-Install @{ PackageRoot = $package; GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true; NoLaunch = $true }
        Assert-Success $result
        foreach ($relative in @('BepInEx/core/BepInEx.Core.dll', 'dotnet/host/fxr/fixture.dll', 'winhttp.dll', 'doorstop_config.ini')) {
            Assert-Text (Join-Path $game $relative) "existing:$relative"
        }
        Assert-Text (Join-Path $game $pluginDll) "package:$pluginDll"
        foreach ($relative in $previous) {
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $game $relative))) "Previous plugin file survived: $relative"
        }
        $loadable = @(Get-LoadablePlugins $game | Where-Object { $_ -notlike 'BepInEx/plugins/Linked Build/*' })
        Assert-True (($loadable -join '|') -ceq $pluginDll) "BepInEx could still load another copy: $($loadable -join ', ')"
        Assert-Text (Join-Path $outside 'AstralPartyChatPlugin.dll') 'outside the game'
        foreach ($entry in $kept.GetEnumerator()) { Assert-Text (Join-Path $game $entry.Key) $entry.Value }
        foreach ($folder in @('BepInEx/plugins/AstralPartyChat', 'BepInEx/plugins/AstralPartyChatPlugin-v0.2.0')) {
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $game $folder))) "A folder emptied by the cleanup was left behind: $folder"
        }
        Assert-True ($result.Output.Contains('BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll')) "Removed files were not reported: $($result.Output)"
        Assert-OriginalGame $game
    }
    foreach ($missing in @('BepInEx/core/BepInEx.Core.dll', 'winhttp.dll')) {
        Invoke-InstallerCase "partial runtime repaired when $missing is missing" {
            $game = New-Game (Join-Path $testRoot ('partial-' + [Guid]::NewGuid().ToString('N')))
            foreach ($relative in @('BepInEx/core/BepInEx.Core.dll', 'winhttp.dll')) {
                if ($relative -ne $missing) { Write-Fixture (Join-Path $game $relative) 'partial runtime' }
            }
            Assert-Success (Invoke-Install @{ PackageRoot = $package; GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true; NoLaunch = $true })
            Assert-Text (Join-Path $game 'BepInEx/core/BepInEx.Core.dll') 'package:BepInEx/core/BepInEx.Core.dll'
            Assert-Text (Join-Path $game 'winhttp.dll') 'package:winhttp.dll'
        }
    }
    $configCases = @(
        # BepInEx parses the whole right-hand side, so inline notes move to their own "#" line above a bare value.
        @{ Name = 'inline notes moved off the value and UTF-8 BOM'; Encoding = [Text.UTF8Encoding]::new($true); Before = "# 설정`r`n[Logging]`r`n  UnityLogListening = true  # keep comment`r`nOther = unchanged`r`n[Logging.Console]`r`nEnabled = true ; console`r`nPreventClose = true`r`n[Else]`r`nUnityLogListening = true`r`nEnabled = true`r`n"; After = "# 설정`r`n[Logging]`r`n  # keep comment`r`n  UnityLogListening = false`r`nOther = unchanged`r`n[Logging.Console]`r`n# console`r`nEnabled = false`r`nPreventClose = true`r`n[Else]`r`nUnityLogListening = true`r`nEnabled = true`r`n" },
        @{ Name = 'insert into section with UTF-16 LE'; Encoding = [Text.Encoding]::Unicode; Before = "[Logging]`n# 설정`nOther = true`n[Logging.Console]`nLogLevels = All`n[Else]`nKeep = yes`n"; After = "[Logging]`nUnityLogListening = false`n# 설정`nOther = true`n[Logging.Console]`nEnabled = false`nLogLevels = All`n[Else]`nKeep = yes`n" },
        @{ Name = 'append section without final newline and UTF-8 no BOM'; Encoding = $utf8; Before = "[Other]`nUnityLogListening = true`nEnabled = true`nKeep = value"; After = "[Other]`nUnityLogListening = true`nEnabled = true`nKeep = value`n[Logging]`nUnityLogListening = false`n$consoleLf" },
        @{ Name = 'header at EOF'; Encoding = [Text.Encoding]::BigEndianUnicode; Before = '[Logging]'; After = "[Logging]`r`nUnityLogListening = false`r`n$consoleCrLf" },
        @{ Name = 'duplicate keys in repeated Logging sections'; Encoding = $utf8; Before = "[Logging]`nUnityLogListening=true`nUnityLogListening = TRUE ; note`n[Logging]`nUnityLogListening = true`n[Logging.Console]`nEnabled=true`n[Logging.Console]`nEnabled = TRUE`n"; After = "[Logging]`nUnityLogListening=false`n# note`nUnityLogListening = false`n[Logging]`nUnityLogListening = false`n[Logging.Console]`nEnabled=false`n[Logging.Console]`nEnabled = false`n" },
        @{ Name = 'ANSI codepage'; Encoding = $utf8; Before = ''; After = '' }
    )
    foreach ($case in $configCases) {
        Invoke-InstallerCase "config preserves everything except the target key: $($case.Name)" {
            # Use the local Windows ANSI codepage for the BOM-less legacy case.
            if ($case.Name -eq 'ANSI codepage') {
                $codepage = $script:windowsAnsiCodePage
                $case.Encoding = [Text.Encoding]::GetEncoding($codepage)
                $case.Before = "; legacy comment`r`n[Logging]`r`nUnityLogListening = true`r`n"
                $case.After = "; legacy comment`r`n[Logging]`r`nUnityLogListening = false`r`n$consoleCrLf"
                if ($codepage -eq 949) {
                    $case.Before = "; 한글 설정`r`n[Logging]`r`nUnityLogListening = true`r`n"
                    $case.After = "; 한글 설정`r`n[Logging]`r`nUnityLogListening = false`r`n$consoleCrLf"
                } elseif ($codepage -eq 1252) {
                    $case.Before = "; café`r`n[Logging]`r`nUnityLogListening = true`r`n"
                    $case.After = "; café`r`n[Logging]`r`nUnityLogListening = false`r`n$consoleCrLf"
                }
            }
            $game = New-Game (Join-Path $testRoot ('cfg-' + [Guid]::NewGuid().ToString('N')))
            $cfg = Join-Path $game $cfgRelative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($cfg)) | Out-Null
            [IO.File]::WriteAllText($cfg, $case.Before, $case.Encoding)
            Assert-Success (Invoke-Install @{ PackageRoot = $package; GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true; NoLaunch = $true })
            $expected = @($case.Encoding.GetPreamble()) + @($case.Encoding.GetBytes($case.After))
            Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($cfg)) -ceq [Convert]::ToBase64String([byte[]]$expected)) "Config content or encoding changed unexpectedly: $($case.Name)"
        }
    }
    Invoke-InstallerCase 'package extracted into the game folder keeps its own files, removes previous copies and fixes config' {
        $game = New-Package 'self extracted'
        New-Game $game | Out-Null
        # This root intentionally contains the unrelated package fixture file.
        Remove-Item -LiteralPath (Join-Path $game 'original-game.dat')
        Write-Fixture (Join-Path $game $cfgRelative) "[Logging]`r`nUnityLogListening = true`r`n"
        Write-Fixture (Join-Path $game 'BepInEx/plugins/AstralParty.Chat.dll') 'previous version'
        $installArguments = @{ GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true; NoLaunch = $true }
        # Exercise the shipped script's default PackageRoot as well as self-copy detection.
        $savedInstaller = $script:installer
        try {
            $script:installer = Join-Path $game 'install.ps1'
            Assert-Success (Invoke-Install $installArguments)
        } finally { $script:installer = $savedInstaller }
        Assert-Text (Join-Path $game $cfgRelative) "[Logging]`r`nUnityLogListening = false`r`n$consoleCrLf"
        Assert-True (((Get-LoadablePlugins $game) -join '|') -ceq $pluginDll) 'Install from inside the game folder left a previous copy or removed the new DLL.'
        Assert-Text (Join-Path $game $pluginDll) "package:$pluginDll"
        Assert-OriginalGame $game
    }
    Invoke-InstallerCase 'package extracted below the plugins folder is refused before anything is removed' {
        $game = New-Game (Join-Path $testRoot 'package in plugins')
        Write-Fixture (Join-Path $game 'BepInEx/plugins/AstralParty.Chat.dll') 'previous version'
        $nested = New-Package 'package in plugins/BepInEx/plugins/AstralPartyChatPlugin-v9.9.9'
        $result = Invoke-Install @{ PackageRoot = $nested; GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true; NoLaunch = $true }
        Assert-True ($result.ExitCode -ne 0) 'A package inside the plugins folder was installed; its own DLL is a cleanup target there.'
        Assert-True ($result.Output.Contains('BepInEx\plugins')) "The player was not told where the package must not be: $($result.Output)"
        Assert-Text (Join-Path $game 'BepInEx/plugins/AstralParty.Chat.dll') 'previous version'
        Assert-Text (Join-Path $nested $pluginDll) "package:$pluginDll"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $game 'BepInEx/core'))) 'A refused install still copied BepInEx.'
    }
    Invoke-InstallerCase 'direct -File launch like install.bat resolves the default PackageRoot' {
        # Windows PowerShell 5.1 leaves $PSScriptRoot empty in [CmdletBinding()] parameter defaults under -File.
        # A folder without the game stops right after package validation, before the real running-game check.
        $notGame = Join-Path $testRoot 'direct file not game'
        [IO.Directory]::CreateDirectory($notGame) | Out-Null
        $output = (@(& $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'install.ps1') -GameRoot $notGame -SteamRoot $emptySteam.Root -Yes -NoLaunch 2>&1) -join "`n")
        Assert-True ($LASTEXITCODE -ne 0) 'Installer accepted a folder without the game.'
        # The expected failure must not leak to callers that run this script in-process (scripts/check.ps1).
        $global:LASTEXITCODE = 0
        Assert-True ($output.Contains('AstralParty_INT.exe') -and -not $output.Contains('GetFullPath')) "Direct launch failed before game selection: $output"
        Assert-True (@(Get-ChildItem -LiteralPath $notGame -Force).Count -eq 0) 'Installer wrote into a folder without the game.'
    }
    Invoke-InstallerCase 'running game rejected without writes or removals; interactive close-and-retry works' {
        $game = New-Game (Join-Path $testRoot 'running game')
        Write-Fixture (Join-Path $game 'BepInEx/plugins/AstralPartyChatPlugin.dll') 'loaded previous version'
        $installArguments = @{ PackageRoot = $package; GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true; NoLaunch = $true }
        $result = Invoke-Install $installArguments @() 'running'
        Assert-True ($result.ExitCode -ne 0) 'Running game was not rejected.'
        Assert-True (((Get-LoadablePlugins $game) -join '|') -ceq 'BepInEx/plugins/AstralPartyChatPlugin.dll' -and -not (Test-Path -LiteralPath (Join-Path $game 'BepInEx/core'))) 'Installer changed files while the game was running.'
        $installArguments.Remove('Yes')
        Assert-Success (Invoke-Install $installArguments @('') 'retry')
        Assert-True (((Get-LoadablePlugins $game) -join '|') -ceq $pluginDll) 'Retry after closing the game did not finish the install.'
    }
    Invoke-InstallerCase 'Yes launches the game executable directly with Steam app variables and waits for its window' {
        $game = New-Game (Join-Path $testRoot 'launch game')
        $result = Invoke-Install @{ PackageRoot = $package; GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true }
        Assert-Success $result
        $launch = [IO.File]::ReadAllText((Join-Path $result.State 'launch.json')) | ConvertFrom-Json
        Assert-True ($launch.FilePath -ceq (Join-Path $game 'AstralParty_INT.exe') -and $launch.WorkingDirectory -ceq $game) 'Launch path or working directory differs.'
        Assert-True ($launch.SteamAppId -ceq '2622000' -and $launch.SteamGameId -ceq '2622000') 'Running Steam must receive a direct launch without the Steam relaunch.'
        Assert-True ($result.Output.Contains('게임 창이 열렸습니다')) "Installer did not report the game window: $($result.Output)"
        $result = Invoke-Install @{ PackageRoot = $package; GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true } @() 'nosteam'
        Assert-Success $result
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $result.State 'launch.json'))) 'Without a Steam client to start, the game must not be launched directly.'
        Assert-True ($result.Output.Contains('Steam을 찾지 못해')) "Installer did not explain the skipped launch: $($result.Output)"
    }
    Invoke-InstallerCase 'closed Steam is started and signed in before the game launches directly' {
        $game = New-Game (Join-Path $testRoot 'closed steam game')
        $steam = New-Steam 'closed'
        Write-Fixture (Join-Path $steam.Root 'steam.exe') 'steam client'
        $result = Invoke-Install @{ PackageRoot = $package; GameRoot = $game; SteamRoot = $steam.Root; Yes = $true } @() 'nosteam'
        Assert-Success $result
        $client = [IO.File]::ReadAllText((Join-Path $result.State 'steam.json')) | ConvertFrom-Json
        Assert-True ($client.FilePath -ceq (Join-Path $steam.Root 'steam.exe') -and (@($client.ArgumentList) -join ' ') -ceq '-silent') 'Steam was not started silently from its install folder.'
        Assert-True (-not $client.GameStartedFirst) 'The game started before Steam, which makes Steam launch a second game process.'
        $launch = [IO.File]::ReadAllText((Join-Path $result.State 'launch.json')) | ConvertFrom-Json
        Assert-True ($launch.FilePath -ceq (Join-Path $game 'AstralParty_INT.exe')) 'Game was not launched after Steam signed in.'
        Assert-True ($launch.SteamAppId -ceq '2622000' -and $launch.SteamGameId -ceq '2622000') 'Game launched after Steam start must skip the Steam relaunch.'
        $result = Invoke-Install @{ PackageRoot = $package; GameRoot = $game; SteamRoot = $emptySteam.Root } @('N')
        Assert-Success $result
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $result.State 'launch.json'))) 'Declined launch still launched.'
    }
    Invoke-InstallerCase 'incomplete payloads rejected before any game write or removal' {
        $game = New-Game (Join-Path $testRoot 'broken target')
        Write-Fixture (Join-Path $game 'BepInEx/plugins/AstralParty.Chat.dll') 'only working copy'
        $withoutPlugin = New-Package 'package without plugin'
        Remove-Item -LiteralPath (Join-Path $withoutPlugin $pluginDll)
        $withoutRuntime = New-Package 'package without runtime'
        Remove-Item -LiteralPath (Join-Path $withoutRuntime 'winhttp.dll')
        foreach ($broken in @($withoutPlugin, $withoutRuntime)) {
            $result = Invoke-Install @{ PackageRoot = $broken; GameRoot = $game; SteamRoot = $emptySteam.Root; Yes = $true; NoLaunch = $true }
            Assert-True ($result.ExitCode -ne 0) 'Incomplete payload was accepted.'
            Assert-True (((Get-LoadablePlugins $game) -join '|') -ceq 'BepInEx/plugins/AstralParty.Chat.dll' -and -not (Test-Path -LiteralPath (Join-Path $game 'BepInEx/core'))) 'Incomplete payload modified the game.'
        }
    }
    Write-Output "Installer checks passed: $passed"
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $parent = [IO.Path]::GetFullPath($testParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected installer test fixture location.' }
    if (Test-Path -LiteralPath $resolved) {
        # Unlink junctions first so cleanup can never follow one out of the fixture.
        Get-ChildItem -LiteralPath $resolved -Recurse -Force -Directory -Attributes ReparsePoint -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
