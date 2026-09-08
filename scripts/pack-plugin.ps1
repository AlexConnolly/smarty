<#
.SYNOPSIS
    Packages a Smarty plugin project into the .zip the control centre accepts.

.DESCRIPTION
    Publishes the project (so any NuGet libraries it depends on come with it) and zips the result. The
    contract assembly, Smarty.Plugins.dll, is deliberately left out: the host loads its own copy, and a
    second one in the package would be a different IPlugin type than the one Smarty looks for.

.EXAMPLE
    .\scripts\pack-plugin.ps1 -Project plugins\Smarty.Plugin.Weather\Smarty.Plugin.Weather.csproj
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'dist' }

$projectPath = Resolve-Path $Project
$name = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("smarty-plugin-" + [System.Guid]::NewGuid().ToString('n'))

Write-Host "Publishing $name ($Configuration)..."
dotnet publish $projectPath -c $Configuration -o $staging | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $projectPath" }

# The host's copy wins at load time, so shipping ours only makes the package bigger and the failure stranger.
Get-ChildItem -Path $staging -Filter 'Smarty.Plugins.*' | Remove-Item -Force

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$zip = Join-Path $OutputDirectory "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Remove-Item $staging -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host "Packaged $zip ($size KB)"
Write-Host 'Upload it from Smarty.Control -> Plugins -> Upload plugin.'
