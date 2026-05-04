param(
    [ValidateSet("OneFile", "OneDir")]
    [string]$Mode = "OneFile"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$icon = Join-Path $root "assets\linecast.ico"
$entry = Join-Path $root "main.py"

if (-not (Test-Path $icon)) {
    python (Join-Path $root "tools\generate_icon.py")
}

$pyInstallerArgs = @(
    "--noconfirm",
    "--clean",
    "--windowed",
    "--name", "LineCast",
    "--icon", $icon,
    "--add-data", "$icon;assets"
)

if ($Mode -eq "OneFile") {
    $pyInstallerArgs += "--onefile"
}

$pyInstallerArgs += $entry

python -m PyInstaller @pyInstallerArgs

if ($Mode -eq "OneFile") {
    Write-Host "Built dist\LineCast.exe"
} else {
    Write-Host "Built dist\LineCast\LineCast.exe"
}
