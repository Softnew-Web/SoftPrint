$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$map = [ordered]@{
    "dist\windows-modern-x64" = "dist\SoftPrint-win-x64.zip"
    "dist\windows-modern-x86" = "dist\SoftPrint-win-x86.zip"
    "dist\windows-legacy-x64" = "dist\SoftPrint-legacy-x64.zip"
    "dist\windows-legacy-x86" = "dist\SoftPrint-legacy-x86.zip"
    "dist\linux-x64"          = "dist\softprint-linux-x64.zip"
}

function Test-ExcludedPublishFile([string] $path) {
    $name = [IO.Path]::GetFileName($path)
    $ext = [IO.Path]::GetExtension($path)
    if ($ext -in @(".pdb", ".xml")) { return $true }
    if ($name -like "*.staticwebassets.endpoints.json") { return $true }
    return $false
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($entry in $map.GetEnumerator()) {
    $src = Join-Path $root $entry.Key
    $dst = Join-Path $root $entry.Value
    if (-not (Test-Path $src)) {
        Write-Host "Ignorado (não encontrado): $($entry.Key)"
        continue
    }
    if (Test-Path $dst) { Remove-Item $dst -Force }

    $stage = Join-Path $env:TEMP ("softprint-zip-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        Get-ChildItem -Path $src -Recurse -File | ForEach-Object {
            if (Test-ExcludedPublishFile $_.FullName) { return }
            $rel = $_.FullName.Substring($src.Length).TrimStart("\", "/")
            $target = Join-Path $stage $rel
            $dir = Split-Path $target -Parent
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Copy-Item $_.FullName $target -Force
        }
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $dst, [IO.Compression.CompressionLevel]::Optimal, $false)
        $mb = [math]::Round((Get-Item $dst).Length / 1MB, 1)
        Write-Host "Gerado: $($entry.Value) ($mb MB)"
    }
    finally {
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
