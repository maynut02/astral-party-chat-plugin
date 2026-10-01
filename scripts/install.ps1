param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT'
)

$ErrorActionPreference = 'Stop'

$pluginFileName = 'AstralParty.Chat.dll'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dll = Join-Path $repoRoot "dist\$pluginFileName"

if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    throw "Missing build output: $dll. Run scripts\build.ps1 first."
}

function Assert-GameStopped {
    if (Get-Process -Name 'AstralParty_INT', '8vJXnINT' -ErrorAction SilentlyContinue) {
        throw 'Astral Party is running. Close the game before installing the plugin.'
    }
}

function Assert-NoReparsePoint([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Installation path is a junction or symbolic link: $Path"
        }
    }
}

Assert-GameStopped
$resolvedGameRoot = (Resolve-Path -LiteralPath $GameRoot -ErrorAction Stop).ProviderPath
if (-not (Test-Path -LiteralPath $resolvedGameRoot -PathType Container)) { throw 'GameRoot must be a directory.' }
$bepInExRoot = [IO.Path]::GetFullPath((Join-Path $resolvedGameRoot 'BepInEx'))
$pluginsRoot = [IO.Path]::GetFullPath((Join-Path $bepInExRoot 'plugins'))
$pluginsBoundary = $pluginsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $pluginsRoot 'AstralPartyChat'))
$installedDll = Join-Path $pluginRoot $pluginFileName

function Assert-PluginPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($pluginsBoundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Plugin path is outside the selected game's plugin directory: $fullPath"
    }
    $current = $fullPath
    while ($current.Length -ge $pluginsRoot.Length) {
        Assert-NoReparsePoint $current
        if ($current -eq $pluginsRoot) { break }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

foreach ($path in @($resolvedGameRoot, $bepInExRoot, $pluginsRoot)) { Assert-NoReparsePoint $path }
Assert-PluginPath $installedDll

# Walk without following links. Every old copy must leave BepInEx's recursive
# plugin search tree, including the former plugins/AstralParty.Chat.dll path.
$oldDlls = [Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $pluginsRoot -PathType Container) {
    $directories = [Collections.Generic.Stack[string]]::new()
    $directories.Push($pluginsRoot)
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            Assert-PluginPath $item.FullName
            if ($item.PSIsContainer) { $directories.Push($item.FullName) }
            elseif ($item.Name -ieq $pluginFileName) { $oldDlls.Add($item.FullName) }
        }
    }
}

$backupParent = [IO.Path]::GetFullPath((Join-Path $repoRoot '.work\plugin-backups'))
$backupRoot = Join-Path $backupParent ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N'))
if ($backupParent -eq $pluginsRoot -or $backupParent.StartsWith($pluginsBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Plugin backups must be outside the game plugin search tree.'
}
foreach ($path in @($repoRoot, (Join-Path $repoRoot '.work'), $backupParent)) { Assert-NoReparsePoint $path }
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$stagedDll = Join-Path $backupRoot "new-$pluginFileName"
Copy-Item -LiteralPath $dll -Destination $stagedDll
New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null

$moved = [Collections.Generic.List[object]]::new()
try {
    Assert-GameStopped
    Assert-PluginPath $installedDll
    foreach ($oldDll in $oldDlls) {
        Assert-PluginPath $oldDll
        $relative = $oldDll.Substring($pluginsBoundary.Length)
        $backup = [IO.Path]::GetFullPath((Join-Path $backupRoot $relative))
        if (-not $backup.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe plugin backup path: $backup"
        }
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($backup)) | Out-Null
        Move-Item -LiteralPath $oldDll -Destination $backup
        $moved.Add(@{ Original = $oldDll; Backup = $backup })
    }
    Move-Item -LiteralPath $stagedDll -Destination $installedDll
}
catch {
    # Restore copies already moved if a later migration or installation fails.
    foreach ($entry in $moved) {
        Assert-PluginPath $entry.Original
        Move-Item -LiteralPath $entry.Backup -Destination $entry.Original
    }
    throw
}

Write-Output "chat=$installedDll"
if ($moved.Count -gt 0) { Write-Output "backup=$backupRoot ($($moved.Count) previous DLL copies)" }
