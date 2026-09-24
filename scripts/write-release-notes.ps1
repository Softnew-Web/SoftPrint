param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutFile = "dist/RELEASE_NOTES.md",

    [switch]$Mandatory
)

$ErrorActionPreference = "Stop"

function Normalize-Subject([string]$subject) {
    $s = $subject.Trim()
    if ($s -match '^(?<type>feat|fix|perf|refactor|docs|chore|test|ci|build|style)(\([^)]+\))?!?:\s*(?<msg>.+)$') {
        return @{ Type = $Matches.type.ToLowerInvariant(); Message = $Matches.msg.Trim() }
    }
    return @{ Type = "other"; Message = $s }
}

function Get-TypeLabel([string]$type) {
    switch ($type) {
        "feat" { "Novidades" }
        "fix" { "Correções" }
        "perf" { "Desempenho" }
        "refactor" { "Ajustes internos" }
        "docs" { "Documentação" }
        default { "Outros" }
    }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Versão inválida: '$Version'"
}

$tag = "v$Version"
$prev = git tag -l "v*" --sort=-v:refname |
    Where-Object { $_ -match '^v\d+\.\d+\.\d+$' -and $_ -ne $tag } |
    Select-Object -First 1

# Prefer the new tag when it already exists; otherwise HEAD (pré-tag / teste local).
$endRef = "HEAD"
cmd /c "git rev-parse -q --verify refs/tags/$tag >nul 2>nul"
if ($LASTEXITCODE -eq 0) { $endRef = $tag }

$range = if ($prev) { "$prev..$endRef" } else { $endRef }
$desde = if ($prev) { $prev } else { "início" }
Write-Host "Notas da release: $tag (desde $desde, até $endRef)"

$gitOut = & git log $range --pretty=format:"%s" --no-merges 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Warning "git log falhou ($range); tentando só HEAD."
    $gitOut = & git log -20 --pretty=format:"%s" --no-merges 2>&1
}

$lines = @()
if ($gitOut) {
    $lines = @(($gitOut | Out-String) -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object {
        $_ -and
        $_ -notmatch '^fatal:' -and
        $_ -notmatch '^chore\(release\):' -and
        $_ -notmatch '^\[skip release\]'
    }
}

$buckets = @{
    feat     = New-Object System.Collections.Generic.List[string]
    fix      = New-Object System.Collections.Generic.List[string]
    perf     = New-Object System.Collections.Generic.List[string]
    refactor = New-Object System.Collections.Generic.List[string]
    docs     = New-Object System.Collections.Generic.List[string]
    other    = New-Object System.Collections.Generic.List[string]
}

foreach ($line in $lines) {
    $parsed = Normalize-Subject $line
    if ($parsed.Type -in @("chore", "ci", "build", "test", "style")) {
        continue
    }
    $key = if ($buckets.ContainsKey($parsed.Type)) { $parsed.Type } else { "other" }
    $msg = $parsed.Message
    if ([string]::IsNullOrWhiteSpace($msg)) { continue }
    $msg = $msg.Substring(0, 1).ToUpperInvariant() + $msg.Substring(1)
    if (-not $buckets[$key].Contains($msg)) {
        $buckets[$key].Add($msg)
    }
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("## SoftPrint $Version")
[void]$sb.AppendLine()

if ($Mandatory) {
    [void]$sb.AppendLine("[mandatory]")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("> **Atualização obrigatória** — o SoftPrint instala esta versão automaticamente no arranque.")
    [void]$sb.AppendLine()
}

$order = @("feat", "fix", "perf", "refactor", "docs", "other")
$any = $false
foreach ($key in $order) {
    $items = $buckets[$key]
    if ($null -eq $items -or $items.Count -eq 0) { continue }
    $any = $true
    [void]$sb.AppendLine("### $(Get-TypeLabel $key)")
    [void]$sb.AppendLine()
    foreach ($item in $items) {
        [void]$sb.AppendLine("- $item")
    }
    [void]$sb.AppendLine()
}

if (-not $any) {
    [void]$sb.AppendLine("Release automático gerado a partir de ``main``.")
    [void]$sb.AppendLine()
}

[void]$sb.AppendLine("---")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Clientes Windows preferem o zip **delta** (``*-from-<versão>.zip``) quando existir; senão o zip completo da arquitetura. O Setup completo continua disponível para instalação nova.")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Os assets incluem ``checksums.sha256`` — o SoftPrint verifica a integridade antes de instalar.")

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$fullOut = if ([IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path (Get-Location) $OutFile }
$notes = $sb.ToString().TrimEnd() + "`n"
[System.IO.File]::WriteAllText($fullOut, $notes, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Notas gravadas em $OutFile"
Write-Host "-----"
Write-Host $notes
