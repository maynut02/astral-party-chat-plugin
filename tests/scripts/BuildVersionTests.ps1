#requires -Version 7.0
param([string]$DotNetPath = '')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repoRoot 'scripts/dotnet-sdk.ps1')
$dotnet = Get-AstralDotnet -Root $repoRoot -WorkRoot (Join-Path $repoRoot '.work') -DotNetPath $DotNetPath
$testRoot = Join-Path $repoRoot ('.work/build-version-tests/' + [Guid]::NewGuid().ToString('N'))
$utf8 = [Text.UTF8Encoding]::new($false)
$passed = 0

function Invoke-FixtureBuild([string]$Project, [string[]]$Arguments = @(), [string]$ExpectedError = '') {
    Push-Location $testRoot
    try { $output = @(& $dotnet build $Project --configuration Release --nologo @Arguments 2>&1) }
    finally { Pop-Location }
    $text = $output -join "`n"
    if ($ExpectedError) {
        if ($LASTEXITCODE -eq 0 -or -not $text.Contains($ExpectedError)) { throw "Invalid version was not rejected: $text" }
    } elseif ($LASTEXITCODE -ne 0) { throw "Version fixture build failed: $text" }
    $script:passed++
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Directory.Build.props'), (Join-Path $repoRoot 'Directory.Build.targets'), (Join-Path $repoRoot 'global.json') -Destination $testRoot
    [IO.File]::WriteAllText((Join-Path $testRoot 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>', $utf8)
    $versionPath = Join-Path $testRoot 'VERSION'
    $projects = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter '*.csproj' -Recurse)
    foreach ($source in $projects) {
        $fixture = Join-Path $testRoot $source.BaseName
        [IO.Directory]::CreateDirectory($fixture) | Out-Null
        [xml]$project = [IO.File]::ReadAllText($source.FullName)
        foreach ($reference in @($project.SelectNodes('//Reference'))) { $reference.ParentNode.RemoveChild($reference) | Out-Null }
        $path = Join-Path $fixture $source.Name
        $project.Save($path)
        # Use the production project and target without requiring game assemblies.
        $code = if ($source.BaseName -eq 'AstralPartyChatPlugin') {
            @'
using System.Reflection;
[assembly: AssemblyVersion(global::AstralPartyChatPlugin.AstralPartyChatPlugin.PluginVersion)]
[assembly: AssemblyFileVersion(global::AstralPartyChatPlugin.AstralPartyChatPlugin.PluginVersion)]
[assembly: AssemblyInformationalVersion(global::AstralPartyChatPlugin.AstralPartyChatPlugin.PluginVersion)]
namespace AstralPartyChatPlugin { public sealed partial class AstralPartyChatPlugin { public const string PluginVersion = global::AstralBuildVersion.Value; } }
'@
        } else { 'public static class Fixture { public const string PluginVersion = AstralBuildVersion.Value; }' }
        [IO.File]::WriteAllText((Join-Path $fixture 'Fixture.cs'), $code, $utf8)
        foreach ($version in @('1.2.3', '1.2.4')) {
            [IO.File]::WriteAllText($versionPath, "$version`n", $utf8)
            Invoke-FixtureBuild $path
            $dll = Join-Path $fixture "bin/Release/net6.0/$($source.BaseName).dll"
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
            if ($info.ProductVersion -cne $version -or $info.FileVersion -cnotin @($version, "$version.0") -or [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString() -cne "$version.0") {
                throw 'Built assembly metadata does not match VERSION.'
            }
            $generated = [IO.File]::ReadAllText((Join-Path $fixture 'obj/Release/net6.0/AstralBuildVersion.g.cs'))
            if (-not $generated.Contains("Value = `"$version`"")) { throw 'Plugin constant did not update with VERSION.' }
        }
        Invoke-FixtureBuild $path @('-p:Version=9.0.0') 'Build Version must match'
        [IO.File]::WriteAllText($versionPath, "01.2.3`n", $utf8)
        Invoke-FixtureBuild $path @() 'VERSION must contain canonical'
        [IO.File]::WriteAllText($versionPath, "65535.0.0`n", $utf8)
        Invoke-FixtureBuild $path @() 'Version components must be between'
        Remove-Item -LiteralPath $versionPath
        Invoke-FixtureBuild $path @() 'Missing VERSION file'
    }
    Write-Output "Build version tests passed: $passed builds using production projects and MSBuild targets."
    $global:LASTEXITCODE = 0
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $parent = [IO.Path]::GetFullPath((Join-Path $repoRoot '.work/build-version-tests')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected version test fixture location.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
