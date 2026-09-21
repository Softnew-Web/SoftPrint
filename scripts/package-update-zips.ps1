$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$map = @{
    "dist\windows-modern-x64" = "dist\SoftPrint-win-x64.zip"
    "dist\windows-modern-x86" = "dist\SoftPrint-win-x86.zip"
    "dist\windows-legacy-x64" = "dist\SoftPrint-legacy-x64.zip"
    "dist\windows-legacy-x86" = "dist\SoftPrint-legacy-x86.zip"
    "dist\linux-x64"          = "dist\softprint-linux-x64.zip"
}

foreach ($entry in $map.GetEnumerator()) {
    $src = Join-Path $root $entry.Key
    $dst = Join-Path $root $entry.Value
    if (-not (Test-Path $src)) {
        Write-Host "Ignorado (não encontrado): $($entry.Key)"
        continue
    }
    if (Test-Path $dst) { Remove-Item $dst -Force }
    Compress-Archive -Path (Join-Path $src "*") -DestinationPath $dst -Force
    Write-Host "Gerado: $($entry.Value)"
}
