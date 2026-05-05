<#
.SYNOPSIS
    Build LineCast C# as a self-contained single-file EXE for Windows x64.
.PARAMETER Mode
    OneFile (default) – produces dist\LineCast.exe
    OneDir            – produces dist\LineCast\LineCast.exe
#>
param(
    [ValidateSet("OneFile", "OneDir")]
    [string]$Mode = "OneFile"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    if ($Mode -eq "OneFile") {
        dotnet publish LineCast.csproj `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -o dist
        Write-Host "Built dist\LineCast.exe"
    } else {
        dotnet publish LineCast.csproj `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -o dist\LineCast
        Write-Host "Built dist\LineCast\LineCast.exe"
    }
} finally {
    Pop-Location
}
