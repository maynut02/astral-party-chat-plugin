param(
    [string]$GameRoot = '',
    [string]$DllPath = '',
    [string]$BackupRoot = '',
    [string]$SteamRoot = '',
    [switch]$Interactive
)

$ErrorActionPreference = 'Stop'
$pluginFileName = 'AstralParty.Chat.dll'

# An embedded ScriptBlock has no script directory. Only repository defaults
# require it; explicit payload/backup paths are fully independent.
if (-not $DllPath -or -not $BackupRoot) {
    if (-not $PSScriptRoot) {
        throw '배포 설치에서는 DllPath와 BackupRoot를 모두 지정해야 합니다.'
    }
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if (-not $DllPath) { $DllPath = Join-Path $repoRoot "dist\$pluginFileName" }
    if (-not $BackupRoot) { $BackupRoot = Join-Path $repoRoot '.work\plugin-backups' }
}

function Assert-GameStopped {
    if (Get-Process -Name 'AstralParty_INT', '8vJXnINT' -ErrorAction SilentlyContinue) {
        throw 'Astral Party가 실행 중입니다. 게임을 종료한 뒤 설치해 주세요.'
    }
}

function Get-FullPath([string]$Path) {
    # Reject NTFS alternate streams and device paths, too.
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.StartsWith('\\?\') -or $full.StartsWith('\\.\') -or $full.Substring(2).Contains(':')) {
        throw "지원하지 않는 파일 경로입니다: $Path"
    }
    return $full
}

function Test-WithinPath([string]$Path, [string]$Parent) {
    $full = (Get-FullPath $Path).TrimEnd('\', '/')
    $root = (Get-FullPath $Parent).TrimEnd('\', '/')
    return ($full.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
}

function Assert-SafePath([string]$Path) {
    # Check every ancestor, including ancestors above GameRoot/BackupRoot.
    # Never resolve through a junction and subsequently accept its target.
    $current = Get-FullPath $Path
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "설치 경로에 정션 또는 심볼릭 링크가 있습니다: $current"
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-GameDirectory([string]$Path) {
    $root = Get-FullPath $Path
    Assert-SafePath $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "게임 경로는 존재하는 폴더여야 합니다: $root"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'AstralParty_INT.exe') -PathType Leaf)) {
        $root = Join-Path $root '8vJXnINT'
    }
    Assert-SafePath (Join-Path $root 'AstralParty_INT.exe')
    if (-not (Test-Path -LiteralPath (Join-Path $root 'AstralParty_INT.exe') -PathType Leaf)) {
        throw "AstralParty_INT.exe가 있는 글로벌 Steam 게임 폴더를 찾을 수 없습니다: $Path"
    }
    return (Get-FullPath $root)
}

function Assert-BepInEx([string]$Root) {
    foreach ($name in @('BepInEx.Core.dll', 'BepInEx.Unity.IL2CPP.dll')) {
        $core = Join-Path $Root "BepInEx\core\$name"
        Assert-SafePath $core
        if (-not (Test-Path -LiteralPath $core -PathType Leaf)) {
            throw "초기화된 BepInEx IL2CPP가 필요합니다. 게임을 한 번 실행하고 종료해 주세요. 누락: $core"
        }
    }
}

# Parse Steam KeyValues structure, not unrelated numeric keys inside "apps".
function Read-VdfBlock($State, [bool]$Nested) {
    $result = @{}
    while ($State.Index -lt $State.Tokens.Count) {
        $key = $State.Tokens[$State.Index++]
        if ($key -eq '}') {
            if (-not $Nested) { throw 'Steam VDF의 닫는 괄호가 잘못되었습니다.' }
            return $result
        }
        if ($key -eq '{' -or $State.Index -ge $State.Tokens.Count) { throw 'Steam VDF 형식이 잘못되었습니다.' }
        if ($result.ContainsKey($key)) { throw "Steam VDF에 중복된 항목이 있습니다: $key" }
        $value = $State.Tokens[$State.Index++]
        if ($value -eq '{') { $value = Read-VdfBlock $State $true }
        elseif ($value -eq '}') { throw 'Steam VDF 값이 누락되었습니다.' }
        $result[$key] = $value
    }
    if ($Nested) { throw 'Steam VDF의 닫는 괄호가 누락되었습니다.' }
    return $result
}

function ConvertFrom-SteamVdf([string]$Content) {
    $tokens = [Collections.Generic.List[string]]::new()
    $pattern = '\G\s*(?://[^\r\n]*|"((?:\\.|[^"\\])*)"|([{}])|([^\s{}"]+))'
    $scanner = [regex]::new($pattern, [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(2))
    $offset = 0
    while ($offset -lt $Content.Length) {
        $match = $scanner.Match($Content, $offset)
        if (-not $match.Success) {
            if ($Content.Substring($offset).Trim().Length -eq 0) { break }
            throw 'Steam VDF에 읽을 수 없는 항목이 있습니다.'
        }
        $offset = $match.Index + $match.Length
        if ($match.Groups[1].Success) {
            $tokens.Add([regex]::Replace($match.Groups[1].Value, '\\([\\"])', '$1'))
        }
        elseif ($match.Groups[2].Success) { $tokens.Add($match.Groups[2].Value) }
        elseif ($match.Groups[3].Success) { $tokens.Add($match.Groups[3].Value) }
    }
    return (Read-VdfBlock ([pscustomobject]@{ Tokens = $tokens; Index = 0 }) $false)
}

function Get-SteamInstallations {
    $steamRoots = [Collections.Generic.List[string]]::new()
    if ($SteamRoot) {
        # Explicit discovery is isolated from real registry/default installs.
        $steamRoots.Add((Get-FullPath $SteamRoot))
    }
    else {
        foreach ($entry in @(
            @{ Key = 'HKCU:\Software\Valve\Steam'; Value = 'SteamPath' },
            @{ Key = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Value = 'InstallPath' },
            @{ Key = 'HKLM:\SOFTWARE\Valve\Steam'; Value = 'InstallPath' }
        )) {
            $property = Get-ItemProperty -LiteralPath $entry.Key -ErrorAction SilentlyContinue
            if ($property -and $property.($entry.Value)) { $steamRoots.Add([string]$property.($entry.Value)) }
        }
        foreach ($programFiles in @([Environment]::GetEnvironmentVariable('ProgramFiles(x86)'), $env:ProgramFiles)) {
            if ($programFiles) { $steamRoots.Add((Join-Path $programFiles 'Steam')) }
        }
    }

    $libraries = [Collections.Generic.List[string]]::new()
    foreach ($steam in $steamRoots) {
        try {
            $steam = Get-FullPath $steam
            Assert-SafePath $steam
            if (-not (Test-Path -LiteralPath $steam -PathType Container)) { continue }
            if (-not $libraries.Contains($steam)) { $libraries.Add($steam) }
            foreach ($relative in @('steamapps\libraryfolders.vdf', 'config\libraryfolders.vdf')) {
                $vdfPath = Join-Path $steam $relative
                Assert-SafePath $vdfPath
                if (-not (Test-Path -LiteralPath $vdfPath -PathType Leaf)) { continue }
                $vdf = ConvertFrom-SteamVdf ([IO.File]::ReadAllText($vdfPath))
                $folders = $vdf['libraryfolders']
                if ($folders -isnot [hashtable]) { throw 'Steam 라이브러리 목록 형식이 잘못되었습니다.' }
                foreach ($key in $folders.Keys) {
                    if ($key -notmatch '^\d+$') { continue }
                    $value = $folders[$key]
                    if ($value -is [hashtable]) { $value = $value['path'] }
                    if ($value -isnot [string] -or -not [IO.Path]::IsPathRooted($value)) { continue }
                    $library = Get-FullPath $value
                    Assert-SafePath $library
                    if (-not $libraries.Contains($library)) { $libraries.Add($library) }
                }
            }
        }
        catch { Write-Warning "Steam 라이브러리를 읽지 못했습니다 ($steam): $($_.Exception.Message)" }
    }

    $games = [Collections.Generic.List[string]]::new()
    foreach ($library in $libraries) {
        $manifest = Join-Path $library 'steamapps\appmanifest_2622000.acf'
        try {
            Assert-SafePath $manifest
            if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
            $vdf = ConvertFrom-SteamVdf ([IO.File]::ReadAllText($manifest))
            $app = $vdf['AppState']
            if ($app -isnot [hashtable] -or $app['appid'] -cne '2622000') { throw 'Steam 매니페스트의 AppID가 2622000이 아닙니다.' }
            $name = $app['installdir']
            if ($name -isnot [string] -or -not $name.Trim() -or $name -in @('.', '..') -or
                $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                $name -cne $name.TrimEnd(' ', '.') -or
                $name -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
                throw 'Steam 매니페스트의 설치 폴더 이름이 안전하지 않습니다.'
            }
            $common = Join-Path $library 'steamapps\common'
            $candidate = Get-FullPath (Join-Path $common $name)
            if (-not (Test-WithinPath $candidate $common)) { throw 'Steam 설치 폴더가 라이브러리 밖을 가리킵니다.' }
            $candidate = Get-GameDirectory $candidate
            if (-not @($games | Where-Object { $_ -ieq $candidate }).Count) { $games.Add($candidate) }
        }
        catch { Write-Warning "Steam 설치 후보를 건너뜁니다 ($manifest): $($_.Exception.Message)" }
    }
    return $games.ToArray()
}

$dll = Get-FullPath $DllPath
Assert-SafePath $dll
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    throw "설치할 DLL 파일이 없습니다: $dll. 먼저 scripts\build.ps1을 실행해 주세요."
}
Assert-GameStopped
if (-not $GameRoot) {
    $candidates = @(Get-SteamInstallations)
    if ($candidates.Count -eq 1) { $GameRoot = $candidates[0] }
    elseif ($Interactive) {
        if ($candidates.Count -gt 1) {
            Write-Host 'Astral Party 설치가 여러 개 있습니다. 설치할 게임 폴더를 입력해 주세요.'
            foreach ($candidate in $candidates) { Write-Host "  $candidate" }
        }
        else { Write-Host 'Astral Party Steam 설치를 자동으로 찾지 못했습니다.' }
        $GameRoot = (Read-Host 'AstralParty_INT.exe가 있는 폴더 또는 Astral Party 폴더 경로').Trim().Trim('"')
        if (-not $GameRoot) { throw '게임 폴더 경로가 입력되지 않았습니다.' }
    }
    elseif ($candidates.Count -gt 1) { throw 'Astral Party 설치가 여러 개 있습니다. GameRoot로 설치할 게임 폴더를 지정해 주세요.' }
    else { throw 'Astral Party Steam 설치를 자동으로 찾지 못했습니다. GameRoot로 게임 폴더를 지정해 주세요.' }
}
$resolvedGameRoot = Get-GameDirectory $GameRoot
Assert-BepInEx $resolvedGameRoot
$pluginsRoot = Join-Path $resolvedGameRoot 'BepInEx\plugins'
$pluginsBoundary = $pluginsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$pluginRoot = Join-Path $pluginsRoot 'AstralPartyChat'
$installedDll = Join-Path $pluginRoot $pluginFileName

function Assert-PluginPath([string]$Path) {
    if (-not (Test-WithinPath $Path $pluginsRoot) -or (Get-FullPath $Path) -ieq $pluginsRoot) {
        throw "플러그인 경로가 선택한 게임의 플러그인 폴더 밖입니다: $Path"
    }
    Assert-SafePath $Path
}

function Get-OldPluginDlls {
    Assert-SafePath $installedDll
    if (Test-Path -LiteralPath $installedDll -PathType Container) {
        throw "설치할 DLL 경로가 폴더입니다: $installedDll"
    }
    foreach ($directory in @($pluginsRoot, $pluginRoot)) {
        if ((Test-Path -LiteralPath $directory) -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
            throw "플러그인 폴더 경로가 파일입니다: $directory"
        }
    }
    # Walk without following links, including links belonging to other plugins.
    if (Test-Path -LiteralPath $pluginsRoot -PathType Container) {
        $directories = [Collections.Generic.Stack[string]]::new()
        $directories.Push($pluginsRoot)
        while ($directories.Count -gt 0) {
            $directory = $directories.Pop()
            foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
                Assert-PluginPath $item.FullName
                if ($item.PSIsContainer) { $directories.Push($item.FullName) }
                elseif ($item.Name -ieq $pluginFileName) { $item.FullName }
            }
        }
    }
}

$oldDlls = @(Get-OldPluginDlls)
$backupParent = Get-FullPath $BackupRoot
Assert-SafePath $backupParent
if (Test-WithinPath $backupParent $resolvedGameRoot) { throw '백업 경로는 게임 폴더 밖이어야 합니다.' }
if ((Test-Path -LiteralPath $backupParent) -and -not (Test-Path -LiteralPath $backupParent -PathType Container)) {
    throw "백업 부모 경로는 폴더여야 합니다: $backupParent"
}
$runBackup = Join-Path $backupParent ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N'))
if (Test-WithinPath $runBackup $resolvedGameRoot) { throw '백업 경로는 게임 폴더 밖이어야 합니다.' }
$previousRoot = Join-Path $runBackup 'previous'
$stagedDll = Join-Path $runBackup "new-$pluginFileName"
$moved = [Collections.Generic.List[object]]::new()
$installAttempted = $false
# Freeze the source BEFORE moving any old plugin; DllPath may itself be the
# installed DLL. Only the verified staged copy is used from this point onward.
$sourceHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash

try {
    Assert-SafePath $runBackup
    [IO.Directory]::CreateDirectory($runBackup) | Out-Null
    Copy-Item -LiteralPath $dll -Destination $stagedDll
    Assert-SafePath $stagedDll
    if ((Get-FileHash -LiteralPath $stagedDll -Algorithm SHA256).Hash -cne $sourceHash) {
        throw '설치 DLL의 임시 복사본 해시가 일치하지 않습니다.'
    }
    Assert-GameStopped
    $null = Get-GameDirectory $resolvedGameRoot
    Assert-BepInEx $resolvedGameRoot
    # Re-scan after staging to catch newly introduced links/duplicates.
    $oldDlls = @(Get-OldPluginDlls)
    Assert-PluginPath $installedDll
    [IO.Directory]::CreateDirectory($pluginRoot) | Out-Null
    foreach ($oldDll in $oldDlls) {
        Assert-GameStopped
        Assert-PluginPath $oldDll
        $relative = $oldDll.Substring($pluginsBoundary.Length)
        $backup = Get-FullPath (Join-Path $previousRoot $relative)
        if (-not (Test-WithinPath $backup $previousRoot)) { throw "안전하지 않은 백업 경로입니다: $backup" }
        Assert-SafePath $backup
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
        # Record before moving: a failed cross-volume move can leave a partial
        # backup alongside the intact original.
        $entry = @{ Original = $oldDll; Backup = $backup; Hash = (Get-FileHash -LiteralPath $oldDll -Algorithm SHA256).Hash }
        $moved.Add($entry)
        Move-Item -LiteralPath $oldDll -Destination $backup
        if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -cne $entry.Hash) {
            throw "기존 DLL의 백업 해시가 일치하지 않습니다: $backup"
        }
    }
    Assert-GameStopped
    Assert-PluginPath $installedDll
    Assert-SafePath $stagedDll
    $installAttempted = $true
    Move-Item -LiteralPath $stagedDll -Destination $installedDll
    Assert-PluginPath $installedDll
    if ((Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash -cne $sourceHash) {
        throw '설치된 DLL의 해시가 일치하지 않습니다.'
    }
}
catch {
    $failure = $_
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    if ($installAttempted) {
        try {
            Assert-PluginPath $installedDll
            if (Test-Path -LiteralPath $installedDll -PathType Leaf) { Remove-Item -LiteralPath $installedDll -Force }
        }
        catch { $rollbackErrors.Add($_.Exception.Message) }
    }
    for ($index = $moved.Count - 1; $index -ge 0; $index--) {
        $entry = $moved[$index]
        try {
            Assert-PluginPath $entry.Original
            if ((Test-Path -LiteralPath $entry.Original -PathType Leaf) -and
                (Get-FileHash -LiteralPath $entry.Original -Algorithm SHA256).Hash -ceq $entry.Hash) { continue }
            Assert-SafePath $entry.Backup
            if ((Get-FileHash -LiteralPath $entry.Backup -Algorithm SHA256).Hash -cne $entry.Hash) {
                throw "복구할 백업 해시가 일치하지 않습니다: $($entry.Backup)"
            }
            # Keep the verified backup outside the game even after rollback.
            Copy-Item -LiteralPath $entry.Backup -Destination $entry.Original -Force
            if ((Get-FileHash -LiteralPath $entry.Original -Algorithm SHA256).Hash -cne $entry.Hash) {
                throw "기존 DLL 복구 해시가 일치하지 않습니다: $($entry.Original)"
            }
        }
        catch { $rollbackErrors.Add($_.Exception.Message) }
    }
    if ($rollbackErrors.Count) {
        throw "설치 실패: $($failure.Exception.Message) 복구 실패: $($rollbackErrors -join '; ') 백업: $runBackup"
    }
    throw $failure
}
finally {
    Assert-SafePath $stagedDll
    if (Test-Path -LiteralPath $stagedDll -PathType Leaf) { Remove-Item -LiteralPath $stagedDll -Force }
}

Write-Output "설치 완료: $installedDll"
if ($moved.Count -gt 0) { Write-Output "기존 DLL $($moved.Count)개 백업: $runBackup" }
$global:LASTEXITCODE = 0
