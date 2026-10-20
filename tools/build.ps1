<#
.SYNOPSIS
    Builds or publishes the LineCast WPF app.

.EXAMPLE
    .\tools\build.ps1
    Builds a Release binary into src\LineCast\bin\x64\Release\net10.0-windows\.

.EXAMPLE
    .\tools\build.ps1 -Publish
    Publishes a single-file, ReadyToRun exe into dist\.

.EXAMPLE
    .\tools\build.ps1 -Publish -SelfContained
    Publishes an exe that carries the .NET runtime with it.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Publish,

    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\LineCast\LineCast.csproj'
$solution = Join-Path $root 'LineCast.sln'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

if ($Publish) {
    $out = Join-Path $root 'dist'

    $arguments = @(
        'publish', $project,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(),
        '-p:PublishSingleFile=true',
        '-p:ReadyToRun=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=embedded',
        '-o', $out
    )

    Write-Host "Publishing LineCast ($Configuration, self-contained=$($SelfContained.IsPresent))..." -ForegroundColor Cyan
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Write-Host "Done: $out\LineCast.exe" -ForegroundColor Green
}
else {
    Write-Host "Building LineCast ($Configuration)..." -ForegroundColor Cyan
    $target = if (Test-Path $solution) { $solution } else { $project }
    & dotnet build $target -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    Write-Host "Done: src\LineCast\bin\x64\$Configuration\net10.0-windows\LineCast.exe" -ForegroundColor Green
}
