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

function Test-LooksPortuguese([string]$text) {
    if ($text -match '[áàâãéêíóôõúçÁÀÂÃÉÊÍÓÔÕÚÇ]') { return $true }
    if ($text -match '(?i)\b(para|com|sem|mais|nova|novo|fila|painel|impressora|atualiza|chave|segurança|versão|configura)\b') {
        return $true
    }
    return $false
}

function Convert-ToPortugueseBullet([string]$msg) {
    $m = $msg.Trim().TrimEnd('.')
    if ([string]::IsNullOrWhiteSpace($m)) { return $null }

    $known = @{
        'lifecycle logs, setup wizard, and update/ui polish' =
            'Logs de ciclo de vida (ligar, desligar e falhas), assistente de configuração e melhorias no painel de atualização'
        'faster print path and optional tray notifications' =
            'Impressão mais rápida e notificações da bandeja opcionais'
        'always check github for updates on splash' =
            'Verificação de atualizações no GitHub também na tela inicial'
        'segurança da api, impressora offline e atualização mais confiável' =
            'Segurança da API, impressora offline e atualização mais confiável'
    }

    $key = $m.ToLowerInvariant()
    if ($known.ContainsKey($key)) { return $known[$key] }

    if (Test-LooksPortuguese $m) {
        return ($m.Substring(0, 1).ToUpper() + $m.Substring(1))
    }

    $t = $m
    $pairs = @(
        @('lifecycle logs', 'logs de ciclo de vida'),
        @('setup wizard', 'assistente de configuração'),
        @('update/ui polish', 'melhorias de atualização e interface'),
        @('ui polish', 'melhorias na interface'),
        @('tray notifications', 'notificações da bandeja'),
        @('print path', 'caminho de impressão'),
        @('github', 'GitHub'),
        @('splash', 'tela inicial'),
        @('api key', 'chave de API'),
        @('offline printer', 'impressora offline'),
        @('queue stall', 'fila parada'),
        @('loopback', 'localhost'),
        @('and ', 'e '),
        @(' with ', ' com '),
        @(' without ', ' sem '),
        @(' for ', ' para '),
        @(' on ', ' em '),
        @(' to ', ' para ')
    )
    foreach ($p in $pairs) {
        $t = [regex]::Replace($t, [regex]::Escape($p[0]), $p[1], 'IgnoreCase')
    }

    return ($t.Substring(0, 1).ToUpper() + $t.Substring(1))
}

function Add-UniqueBullet($list, [string]$bullet) {
    if ([string]::IsNullOrWhiteSpace($bullet)) { return }
    $normalized = $bullet.Trim()
    if (-not $list.Contains($normalized)) {
        $list.Add($normalized)
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

# Subject + corpo: bullets do corpo (- item) viram novidades mais claras.
$gitOut = & git log $range --pretty=format:"<<<COMMIT>>>%n%s%n%b" --no-merges 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Warning "git log falhou ($range); tentando só HEAD."
    $gitOut = & git log -20 --pretty=format:"<<<COMMIT>>>%n%s%n%b" --no-merges 2>&1
}

$raw = if ($gitOut) { ($gitOut | Out-String) } else { "" }
$commits = @($raw -split '<<<COMMIT>>>' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$buckets = @{
    feat     = New-Object System.Collections.Generic.List[string]
    fix      = New-Object System.Collections.Generic.List[string]
    perf     = New-Object System.Collections.Generic.List[string]
    refactor = New-Object System.Collections.Generic.List[string]
    docs     = New-Object System.Collections.Generic.List[string]
    other    = New-Object System.Collections.Generic.List[string]
}

foreach ($block in $commits) {
    $blockLines = @($block -split "`r?`n" | ForEach-Object { $_.TrimEnd() })
    if ($blockLines.Count -lt 1) { continue }

    $subject = $blockLines[0].Trim()
    if (-not $subject -or
        $subject -match '^chore\(release\):' -or
        $subject -match '^\[skip release\]' -or
        $subject -match '^fatal:') {
        continue
    }

    $parsed = Normalize-Subject $subject
    if ($parsed.Type -in @("chore", "ci", "build", "test", "style")) {
        continue
    }

    $key = if ($buckets.ContainsKey($parsed.Type)) { $parsed.Type } else { "other" }
    $bodyBullets = @()
    foreach ($line in $blockLines | Select-Object -Skip 1) {
        $t = $line.Trim()
        if ($t -match '^[-*•]\s+(.+)$') {
            $bodyBullets += $Matches[1].Trim()
        }
    }

    if ($bodyBullets.Count -gt 0) {
        foreach ($b in $bodyBullets) {
            $pt = Convert-ToPortugueseBullet $b
            Add-UniqueBullet $buckets[$key] $pt
        }
    }
    else {
        $pt = Convert-ToPortugueseBullet $parsed.Message
        Add-UniqueBullet $buckets[$key] $pt
    }
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("## SoftPrint $Version")
[void]$sb.AppendLine()

if ($Mandatory) {
    [void]$sb.AppendLine("[mandatory]")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("> **Atualização obrigatória** — o SoftPrint instala esta versão automaticamente na próxima abertura.")
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
    [void]$sb.AppendLine("Melhorias e correções gerais nesta versão.")
    [void]$sb.AppendLine()
}

[void]$sb.AppendLine("---")
[void]$sb.AppendLine()
[void]$sb.AppendLine("**Como atualizar (Windows)**")
[void]$sb.AppendLine()
[void]$sb.AppendLine("- No SoftPrint, use **Atualizar agora** quando aparecer a faixa de nova versão (o botão **Atualizar** do topo só recarrega o painel).")
[void]$sb.AppendLine("- Preferimos o pacote **delta** (``*-from-<versão>.zip``) quando existir; senão o zip completo. O ``SoftPrint-Setup.exe`` serve para instalação nova.")
[void]$sb.AppendLine("- Todos os arquivos incluem ``checksums.sha256`` — a integridade é verificada antes de instalar.")
[void]$sb.AppendLine()
[void]$sb.AppendLine("**Linux:** use ``softprint-linux-x64.zip``. A atualização automática completa continua focada no Windows.")

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
