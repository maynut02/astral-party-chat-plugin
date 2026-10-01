#requires -Version 7.0
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testParent = Join-Path $repoRoot '.work/cmd-tests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$passed = 0
$localDotnet = Join-Path $repoRoot '.work/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source }
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-FixtureFile([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Invoke-InstallerProcess([string]$Installer, [string]$GameRoot, [string]$LocalData) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $env:SystemRoot 'System32/cmd.exe'
    $start.Arguments = '/d /s /c ""%ASTRAL_TEST_INSTALLER%" "%ASTRAL_TEST_GAME_ROOT%" /quiet"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.Environment['ASTRAL_TEST_INSTALLER'] = $Installer
    $start.Environment['ASTRAL_TEST_GAME_ROOT'] = $GameRoot
    $start.Environment['LOCALAPPDATA'] = $LocalData
    $start.Environment['ERRORLEVEL'] = '42'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-True ($process.Start()) 'Could not start the distributed CMD installer.'
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'CMD installer timed out.' }
        return [pscustomobject]@{ Code = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
function New-CommandFixture([string]$Name) {
    $root = Join-Path $testRoot $Name
    $korean = [string][char]0xd55c + [char]0xae00
    $package = Join-Path $root "package $korean & ! ^ % ' space"
    [IO.Directory]::CreateDirectory($package) | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $package
    $game = Join-Path $root "game $korean & ! ^ % ' space/8vJXnINT"
    Write-FixtureFile (Join-Path $game 'AstralParty_INT.exe') 'fixture executable'
    Write-FixtureFile (Join-Path $game 'BepInEx/core/BepInEx.Core.dll') 'fixture loader'
    Write-FixtureFile (Join-Path $game 'BepInEx/core/BepInEx.Unity.IL2CPP.dll') 'fixture IL2CPP loader'
    return [pscustomobject]@{
        Root = $root; Package = $package; Game = $game
        Installer = Join-Path $package 'Install.cmd'
        SourceDll = Join-Path $package 'BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll'
        InstalledDll = Join-Path $game 'BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll'
        LocalData = Join-Path $root 'local-data'
    }
}
function Invoke-CommandCase([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:passed++
    Write-Output "PASS distribution installer: $Name"
}

try {
    $project = Join-Path $testRoot 'assembly'
    Write-FixtureFile (Join-Path $project 'Fixture.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net6.0</TargetFramework><AssemblyName>AstralParty.Chat</AssemblyName><Version>1.0.7</Version><IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>'
    Write-FixtureFile (Join-Path $project 'Fixture.cs') 'public static class Fixture { }'
    Write-FixtureFile (Join-Path $project 'NuGet.Config') '<configuration><packageSources><clear /></packageSources></configuration>'
    $assemblyRoot = Join-Path $testRoot 'dll'
    $buildOutput = @(& $dotnet build (Join-Path $project 'Fixture.csproj') --configuration Release --output $assemblyRoot --nologo 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Installer fixture assembly build failed: $($buildOutput -join "`n")" }
    $assets = Join-Path $testRoot 'assets'
    & (Join-Path $repoRoot 'scripts/package-release.ps1') -Tag v1.0.7 -DllPath (Join-Path $assemblyRoot 'AstralParty.Chat.dll') -OutputRoot $assets
    $zipPath = Join-Path $assets 'AstralParty.Chat-v1.0.7.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    Invoke-CommandCase 'zip-contains-only-plugin-and-command' {
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $names = @($zip.Entries.FullName)
            Assert-True ($names.Count -eq 2 -and $names -ccontains 'Install.cmd' -and $names -ccontains 'BepInEx/plugins/AstralPartyChat/AstralParty.Chat.dll') 'Unexpected installation package members.'
            $reader = [IO.StreamReader]::new($zip.GetEntry('Install.cmd').Open(), $utf8)
            try { $command = $reader.ReadToEnd() } finally { $reader.Dispose() }
            Assert-True ($command.StartsWith("@echo off`r`n") -and $command -notmatch '(?<!\r)\n') 'CMD must use CRLF and start without a BOM.'
            $marker = "`r`n# ASTRAL_CHAT_INSTALLER_POWERSHELL`r`n"
            $source = $command.Substring($command.IndexOf($marker, [StringComparison]::Ordinal) + $marker.Length)
            $tokens = $null; $parseErrors = $null
            [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors) | Out-Null
            Assert-True ($parseErrors.Count -eq 0) 'The embedded PowerShell payload does not parse.'
        } finally { $zip.Dispose() }
    }
    Invoke-CommandCase 'native-cmd-installs-from-special-character-paths' {
        $f = New-CommandFixture 'clean'
        $result = Invoke-InstallerProcess $f.Installer $f.Game $f.LocalData
        Assert-True ($result.Code -eq 0) $result.Output
        Assert-True ((Get-FileHash -LiteralPath $f.SourceDll).Hash -ceq (Get-FileHash -LiteralPath $f.InstalledDll).Hash) 'Installed DLL differs from the distribution.'
        Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $f.Game 'BepInEx/plugins') -Filter AstralParty.Chat.dll -File -Recurse).Count -eq 1) 'CMD installation left duplicate plugins.'
    }
    Invoke-CommandCase 'native-cmd-migrates-duplicates-and-preserves-backups' {
        $f = New-CommandFixture 'upgrade'
        Write-FixtureFile (Join-Path $f.Game 'BepInEx/plugins/AstralParty.Chat.dll') 'legacy'
        Write-FixtureFile $f.InstalledDll 'previous'
        $result = Invoke-InstallerProcess $f.Installer $f.Game $f.LocalData
        Assert-True ($result.Code -eq 0) $result.Output
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $f.Game 'BepInEx/plugins/AstralParty.Chat.dll'))) 'Legacy DLL remained in the plugin tree.'
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $f.LocalData 'AstralPartyChat/plugin-backups') -Filter AstralParty.Chat.dll -Recurse -File)
        $contents = @($backups | ForEach-Object { [IO.File]::ReadAllText($_.FullName) })
        Assert-True ($backups.Count -eq 2 -and $contents -ccontains 'legacy' -and $contents -ccontains 'previous') 'Installer did not preserve the old DLLs outside the plugin tree.'
        $again = Invoke-InstallerProcess $f.Installer $f.Game $f.LocalData
        Assert-True ($again.Code -eq 0) $again.Output
        Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $f.LocalData 'AstralPartyChat/plugin-backups') -Filter AstralParty.Chat.dll -Recurse -File).Count -eq 3) 'Repeated CMD installation overwrote an earlier backup.'
    }
    Invoke-CommandCase 'native-cmd-rejects-missing-bepinex' {
        $f = New-CommandFixture 'missing-loader'
        Remove-Item -LiteralPath (Join-Path $f.Game 'BepInEx/core/BepInEx.Unity.IL2CPP.dll') -Force
        $result = Invoke-InstallerProcess $f.Installer $f.Game $f.LocalData
        Assert-True ($result.Code -ne 0 -and $result.Output -match 'BepInEx') 'Missing loader was not reported as a failure.'
        Assert-True (-not (Test-Path -LiteralPath $f.InstalledDll)) 'Missing-loader failure installed a DLL.'
    }
    Invoke-CommandCase 'native-cmd-rejects-missing-packaged-dll' {
        $f = New-CommandFixture 'missing-dll'
        Remove-Item -LiteralPath $f.SourceDll -Force
        $result = Invoke-InstallerProcess $f.Installer $f.Game $f.LocalData
        Assert-True ($result.Code -ne 0) 'Missing distribution DLL returned success.'
        Assert-True (-not (Test-Path -LiteralPath $f.InstalledDll)) 'Missing-source failure installed a DLL.'
    }
    Invoke-CommandCase 'native-cmd-rejects-wrong-game-directory' {
        $f = New-CommandFixture 'wrong-game'
        Remove-Item -LiteralPath (Join-Path $f.Game 'AstralParty_INT.exe') -Force
        $result = Invoke-InstallerProcess $f.Installer $f.Game $f.LocalData
        Assert-True ($result.Code -ne 0) 'Wrong-game directory returned success.'
        Assert-True (-not (Test-Path -LiteralPath $f.InstalledDll)) 'Wrong-game failure installed a DLL.'
    }
    Write-Output "Distribution installer tests passed: $script:passed cases."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $boundary = [IO.Path]::GetFullPath($testParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe distribution test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$global:LASTEXITCODE = 0
