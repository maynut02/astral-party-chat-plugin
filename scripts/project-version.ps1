# Shared by local build, packaging and release preparation.
function ConvertTo-AstralVersion {
    param([string]$Value, [switch]$Tag)

    $prefix = if ($Tag) { 'v' } else { '' }
    $match = [regex]::Match($Value, '\A' + $prefix + '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')
    if (-not $match.Success) { throw "Version must contain canonical ${prefix}X.Y.Z." }
    $parts = [int[]]@(0, 0, 0)
    for ($index = 0; $index -lt 3; $index++) {
        $parsed = 0
        if (-not [int]::TryParse($match.Groups[$index + 1].Value, [ref]$parsed) -or $parsed -gt 65534) {
            throw 'Version components must be between 0 and 65534 for .NET assembly metadata.'
        }
        $parts[$index] = $parsed
    }
    return ('{0}.{1}.{2}' -f $parts[0], $parts[1], $parts[2])
}

function Get-AstralProjectVersion {
    param([Parameter(Mandatory = $true)][string]$Root)

    $path = Join-Path $Root 'VERSION'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Missing VERSION file.' }
    $value = [IO.File]::ReadAllText($path).Trim()
    return ConvertTo-AstralVersion $value
}

function Get-AstralNextVersion {
    param([string]$Version, [ValidateSet('patch', 'minor', 'major')][string]$Bump)

    $numeric = [Version](ConvertTo-AstralVersion $Version)
    $major = $numeric.Major
    $minor = $numeric.Minor
    $patch = $numeric.Build
    switch ($Bump) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    return ConvertTo-AstralVersion ('{0}.{1}.{2}' -f $major, $minor, $patch)
}
