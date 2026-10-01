function Get-AstralDistributionInstaller {
    param([string]$InstallerScriptPath = '')

    if (-not $InstallerScriptPath) { $InstallerScriptPath = Join-Path $PSScriptRoot 'install.ps1' }
    $source = [IO.File]::ReadAllText($InstallerScriptPath)
    $marker = '# ASTRAL_CHAT_INSTALLER_POWERSHELL'
    if ($source.Contains($marker)) { throw 'Installer source contains the distribution marker.' }
    # Paths enter PowerShell through environment variables, never as command
    # text. Keep delayed expansion disabled for folders containing !.
    $header = @'
@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ERRORLEVEL="
chcp 65001 >nul
set "ASTRAL_CHAT_INSTALLER_FILE=%~f0"
set "ASTRAL_CHAT_PACKAGE_ROOT=%~dp0"
set "ASTRAL_CHAT_GAME_ROOT=%~1"
set "ASTRAL_CHAT_INSTALL_MODE=%~2"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $env:PSModulePath=(Join-Path $PSHOME 'Modules'); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); try { $text=[IO.File]::ReadAllText($env:ASTRAL_CHAT_INSTALLER_FILE,[Text.Encoding]::UTF8); $marker=[Environment]::NewLine+'# ASTRAL_CHAT_INSTALLER_POWERSHELL'+[Environment]::NewLine; $position=$text.IndexOf($marker,[StringComparison]::Ordinal); if ($position -lt 0) { throw 'Installer payload is missing. Extract the ZIP again.' }; $source=$text.Substring($position+$marker.Length); $local=$env:LOCALAPPDATA; if ([string]::IsNullOrWhiteSpace($local)) { $local=[Environment]::GetFolderPath('LocalApplicationData') }; if ([string]::IsNullOrWhiteSpace($local)) { throw 'Local application data directory is unavailable.' }; $parameters=@{ DllPath=(Join-Path $env:ASTRAL_CHAT_PACKAGE_ROOT 'BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll'); BackupRoot=(Join-Path $local 'AstralPartyChat\plugin-backups'); Interactive=($env:ASTRAL_CHAT_INSTALL_MODE -ine '/quiet') }; if ($env:ASTRAL_CHAT_GAME_ROOT) { $parameters.GameRoot=$env:ASTRAL_CHAT_GAME_ROOT }; & ([ScriptBlock]::Create($source)) @parameters; exit 0 } catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }"
set "ASTRAL_CHAT_INSTALL_EXIT=%errorlevel%"
echo.
if /i "%ASTRAL_CHAT_INSTALL_MODE%"=="/quiet" exit /b %ASTRAL_CHAT_INSTALL_EXIT%
pause
exit /b %ASTRAL_CHAT_INSTALL_EXIT%
# ASTRAL_CHAT_INSTALLER_POWERSHELL
'@
    # CMD starts with ASCII without a BOM. The embedded PowerShell is read as
    # UTF-8 explicitly, including Korean text, using Windows PowerShell 5.1.
    $text = $header + "`n" + $source.TrimStart([char]0xfeff)
    return (($text -replace "`r`n", "`n") -replace "`r", "`n") -replace "`n", "`r`n"
}
