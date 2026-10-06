#requires -Version 7.0

function Get-AstralSdkVersion([string]$Root) {
    $Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
    $version = ([IO.File]::ReadAllText((Join-Path $Root 'global.json')) | ConvertFrom-Json).sdk.version
    if ($version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'global.json must specify a complete SDK version.' }
    return $version
}

function Test-AstralDotnetSdk([string]$Path, [string]$Root) {
    $PSNativeCommandUseErrorActionPreference = $false
    $pushed = $false
    try {
        if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
        # Resolve a relative host before changing directory to resolve global.json.
        $Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
        Push-Location -LiteralPath $Root -ErrorAction Stop
        $pushed = $true
        # --version resolves global.json; a runtime-only host cannot satisfy it.
        $output = @(& $Path --version 2>&1)
        return $LASTEXITCODE -eq 0 -and ($output -join '').Trim() -match '\A[0-9]+\.[0-9]+\.[0-9]+\z'
    }
    catch { return $false }
    finally { if ($pushed) { Pop-Location } }
}

function Get-AstralDotnet([string]$Root, [string]$WorkRoot, [string]$DotNetPath = '') {
    $Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
    $WorkRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
    $sdkVersion = Get-AstralSdkVersion $Root
    if ($DotNetPath) {
        if (Test-Path -LiteralPath $DotNetPath -PathType Leaf) {
            $DotNetPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DotNetPath)
        }
        else {
            $command = Get-Command $DotNetPath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($command) { $DotNetPath = $command.Source }
        }
        if (Test-AstralDotnetSdk $DotNetPath $Root) { return $DotNetPath }
        throw "The specified dotnet cannot resolve SDK $sdkVersion from global.json: $DotNetPath"
    }

    $candidates = @((Join-Path $WorkRoot 'dotnet/dotnet.exe')) + @(
        Get-Command dotnet -CommandType Application -All -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    )
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-AstralDotnetSdk $candidate $Root) { return $candidate }
    }
    throw "No compatible .NET SDK ($sdkVersion) found. Run scripts/setup.ps1 to install it locally, then rerun this command."
}

function Install-AstralLocalSdk([string]$Root, [string]$WorkRoot) {
    $Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
    $WorkRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
    $sdkVersion = Get-AstralSdkVersion $Root
    $dotnetRoot = Join-Path $WorkRoot 'dotnet'
    $dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
    if (Test-AstralDotnetSdk $dotnetExe $Root) { return $dotnetExe }

    New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
    $installer = Join-Path $WorkRoot 'dotnet-install.ps1'
    $ProgressPreference = 'SilentlyContinue'
    $PSNativeCommandUseErrorActionPreference = $false
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    $powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Version $sdkVersion -Architecture x64 -InstallDir $dotnetRoot -NoPath | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-AstralDotnetSdk $dotnetExe $Root)) {
        throw "Failed to prepare local .NET SDK $sdkVersion. Check the network connection and rerun scripts/setup.ps1."
    }
    return $dotnetExe
}
