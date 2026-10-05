# Shared by local build, packaging and release preparation.
function Get-AstralProjectVersion {
    param([Parameter(Mandatory = $true)][string]$Root)

    $path = Join-Path $Root 'VERSION'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Missing VERSION file.' }
    $value = [IO.File]::ReadAllText($path).Trim()
    $resolved = & (Join-Path $PSScriptRoot 'release-version.ps1') -InitialVersion $value -ExplicitTag "v$value"
    $numeric = [Version]$resolved.Version
    if ($numeric.Major -gt 65534 -or $numeric.Minor -gt 65534 -or $numeric.Build -gt 65534) {
        throw 'VERSION components must be between 0 and 65534 for .NET assembly metadata.'
    }
    return $resolved.Version
}
