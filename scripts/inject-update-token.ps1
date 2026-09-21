param(
    [string] $Token = $env:SOFTPRINT_UPDATE_TOKEN
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ([string]::IsNullOrWhiteSpace($Token)) {
    Write-Host "SOFTPRINT_UPDATE_TOKEN ausente — builds sem token (ok se o repo for público)."
    exit 0
}

$dirs = @(
    "dist\windows-modern-x64",
    "dist\windows-modern-x86",
    "dist\windows-legacy-x64",
    "dist\windows-legacy-x86",
    "dist\linux-x64"
)

foreach ($rel in $dirs) {
    $dir = Join-Path $root $rel
    if (-not (Test-Path $dir)) { continue }
    $path = Join-Path $dir "update-github.token"
    [System.IO.File]::WriteAllText($path, $Token.Trim() + "`n")
    Write-Host "Token de update injetado em $rel"
}
