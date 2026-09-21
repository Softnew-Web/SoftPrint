param(
    [string] $PreviousTag = "",
    [string] $CurrentVersion = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $CurrentVersion) {
    $CurrentVersion = [IO.File]::ReadAllText((Join-Path $root "VERSION")).Trim().TrimStart([char]0xFEFF)
}
if ($CurrentVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION inválida: $CurrentVersion"
}

if (-not $PreviousTag) {
    $PreviousTag = gh release list --limit 20 --json tagName,isDraft,isPrerelease `
        --jq "[.[] | select(.isDraft==false and .isPrerelease==false and .tagName != `"v$CurrentVersion`") | .tagName][0]"
}
if ([string]::IsNullOrWhiteSpace($PreviousTag)) {
    Write-Host "Sem release anterior — deltas omitidos."
    exit 0
}

$prevVer = $PreviousTag.TrimStart("v", "V")
if ($prevVer -notmatch '^\d+\.\d+\.\d+$') {
    Write-Host "Tag anterior inválida ($PreviousTag) — deltas omitidos."
    exit 0
}

Write-Host "Delta: $prevVer → $CurrentVersion"

$pairs = @(
    @{ Full = "dist\SoftPrint-win-x64.zip";     Delta = "dist\SoftPrint-win-x64-from-$prevVer.zip";     Exe = "SoftPrint.exe" },
    @{ Full = "dist\SoftPrint-win-x86.zip";     Delta = "dist\SoftPrint-win-x86-from-$prevVer.zip";     Exe = "SoftPrint.exe" },
    @{ Full = "dist\SoftPrint-legacy-x64.zip";  Delta = "dist\SoftPrint-legacy-x64-from-$prevVer.zip";  Exe = "SoftPrint.Legacy.exe" },
    @{ Full = "dist\SoftPrint-legacy-x86.zip";  Delta = "dist\SoftPrint-legacy-x86-from-$prevVer.zip";  Exe = "SoftPrint.Legacy.exe" },
    @{ Full = "dist\softprint-linux-x64.zip";   Delta = "dist\softprint-linux-x64-from-$prevVer.zip";   Exe = "softprint" }
)

$tools = Join-Path $env:TEMP "softprint-hdiff"
New-Item -ItemType Directory -Force -Path $tools | Out-Null
$hdiffZip = Join-Path $tools "hdiff.zip"
$isWin = $env:OS -match "Windows"
$asset = if ($isWin) { "hdiffpatch_v5.1.3_bin_windows64.zip" } else { "hdiffpatch_v5.1.3_bin_linux64.zip" }
$hdiffUrl = "https://github.com/sisong/HDiffPatch/releases/download/v5.1.3/$asset"
if (-not (Test-Path (Join-Path $tools "hdiffz.exe")) -and -not (Test-Path (Join-Path $tools "hdiffz"))) {
    Write-Host "Baixando HDiffPatch ($asset)…"
    Invoke-WebRequest -Uri $hdiffUrl -OutFile $hdiffZip -UseBasicParsing
    Expand-Archive -Path $hdiffZip -DestinationPath $tools -Force
}
$hdiffz = @(Get-ChildItem $tools -Recurse -Filter "hdiffz*" -File | Select-Object -First 1).FullName
$hpatchz = @(Get-ChildItem $tools -Recurse -Filter "hpatchz*" -File | Select-Object -First 1).FullName
if (-not $hdiffz -or -not $hpatchz) { throw "hdiffz/hpatchz não encontrados em $tools" }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-FileSha256([string] $path) {
    return (Get-FileHash -Algorithm SHA256 -Path $path).Hash
}

function Get-RelPath([string] $baseDir, [string] $targetFile) {
    $baseFull = [IO.Path]::GetFullPath($baseDir).TrimEnd([char]'\', [char]'/')
    $targetFull = [IO.Path]::GetFullPath($targetFile)
    if ($targetFull.Length -le $baseFull.Length -or
        -not $targetFull.StartsWith($baseFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Arquivo fora da pasta base: $targetFull"
    }
    return $targetFull.Substring($baseFull.Length).TrimStart([char]'\', [char]'/').Replace("/", "\")
}

function Expand-ZipTo([string] $zip, [string] $dir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)
}

$work = Join-Path $env:TEMP ("softprint-delta-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    foreach ($pair in $pairs) {
        $fullPath = Join-Path $root $pair.Full
        if (-not (Test-Path $fullPath)) {
            Write-Host "Sem pacote completo $($pair.Full) — pulando delta."
            continue
        }

        $prevZip = Join-Path $work ([IO.Path]::GetFileName($pair.Full))
        Write-Host "Baixando $($pair.Full) de $PreviousTag…"
        try {
            gh release download $PreviousTag -p ([IO.Path]::GetFileName($pair.Full)) -D $work --clobber
        }
        catch {
            Write-Host "Asset anterior ausente — pulando $($pair.Full)."
            continue
        }
        if (-not (Test-Path $prevZip)) {
            Write-Host "Download falhou — pulando $($pair.Full)."
            continue
        }

        $oldDir = Join-Path $work "old-$([IO.Path]::GetFileNameWithoutExtension($pair.Full))"
        $newDir = Join-Path $work "new-$([IO.Path]::GetFileNameWithoutExtension($pair.Full))"
        $deltaDir = Join-Path $work "delta-$([IO.Path]::GetFileNameWithoutExtension($pair.Full))"
        Expand-ZipTo $prevZip $oldDir
        Expand-ZipTo $fullPath $newDir
        if (Test-Path $deltaDir) { Remove-Item $deltaDir -Recurse -Force }
        New-Item -ItemType Directory -Path $deltaDir | Out-Null

        $patches = @()
        $copied = @()
        $newRoot = [IO.Path]::GetFullPath($newDir)
        $oldRoot = [IO.Path]::GetFullPath($oldDir)
        $newFiles = Get-ChildItem $newRoot -Recurse -File
        foreach ($nf in $newFiles) {
            $rel = Get-RelPath $newRoot $nf.FullName
            if ([string]::IsNullOrWhiteSpace($rel)) { continue }
            $oldFile = Join-Path $oldRoot $rel
            $dest = Join-Path $deltaDir $rel

            if ((Test-Path $oldFile) -and (Get-FileSha256 $oldFile) -eq (Get-FileSha256 $nf.FullName)) {
                continue
            }

            $dir = Split-Path $dest -Parent
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

            $isMainExe = [string]::Equals($rel, $pair.Exe, [StringComparison]::OrdinalIgnoreCase)
            if ($isMainExe -and (Test-Path $oldFile)) {
                $patchName = "$rel.hdiff"
                $patchPath = Join-Path $deltaDir $patchName
                $patchDir = Split-Path $patchPath -Parent
                if (-not (Test-Path $patchDir)) { New-Item -ItemType Directory -Path $patchDir -Force | Out-Null }
                # hdiffz v5: old new outDiff (sem -f-; falha → copia o exe completo)
                & $hdiffz $oldFile $nf.FullName $patchPath 2>$null | Out-Null
                $hdiffOk = ($LASTEXITCODE -eq 0) -and (Test-Path $patchPath) -and ((Get-Item $patchPath).Length -gt 0)
                $global:LASTEXITCODE = 0
                if (-not $hdiffOk) {
                    if (Test-Path $patchPath) { Remove-Item $patchPath -Force -ErrorAction SilentlyContinue }
                    Copy-Item $nf.FullName $dest -Force
                    $copied += $rel
                }
                else {
                    $patches += @{ target = $rel; patch = $patchName }
                }
            }
            else {
                Copy-Item $nf.FullName $dest -Force
                $copied += $rel
            }
        }

        if ($patches.Count -eq 0 -and $copied.Count -eq 0) {
            Write-Host "Nenhuma diferença em $($pair.Full)."
            continue
        }

        if ($patches.Count -gt 0) {
            Copy-Item $hpatchz (Join-Path $deltaDir ([IO.Path]::GetFileName($hpatchz))) -Force
        }

        $manifest = [ordered]@{
            schema      = 1
            from        = $prevVer
            to          = $CurrentVersion
            createdUtc  = [DateTime]::UtcNow.ToString("o")
            patches     = $patches
            files       = $copied
        }
        $manifestPath = Join-Path $deltaDir "softprint-delta.json"
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding utf8

        $deltaOut = Join-Path $root $pair.Delta
        if (Test-Path $deltaOut) { Remove-Item $deltaOut -Force }
        [IO.Compression.ZipFile]::CreateFromDirectory($deltaDir, $deltaOut, [IO.Compression.CompressionLevel]::Optimal, $false)
        $fullMb = [math]::Round((Get-Item $fullPath).Length / 1MB, 1)
        $deltaMb = [math]::Round((Get-Item $deltaOut).Length / 1MB, 1)
        Write-Host "Delta $($pair.Delta): $deltaMb MB (completo $fullMb MB)"
    }
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}

$global:LASTEXITCODE = 0
exit 0
