$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$icon = Join-Path $root "assets\linecast.ico"
$entry = Join-Path $root "main.py"

if (-not (Test-Path $icon)) {
    python (Join-Path $root "tools\generate_icon.py")
}

python -m PyInstaller `
    --noconfirm `
    --clean `
    --windowed `
    --onefile `
    --name LineCast `
    --icon $icon `
    --add-data "$icon;assets" `
    $entry

Write-Host "Built dist\LineCast.exe"
