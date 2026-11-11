param(
    # Pastas de publish do SoftPrint.Legacy.exe (ex.: dist/windows-legacy-x64).
    [Parameter(Mandatory = $true)]
    [string[]] $PublishDirs,
    [int] $Port = 5178,
    [int] $TimeoutSeconds = 60
)

# Smoke do Legacy: o exe precisa abrir (deps.json íntegro), servir o painel com
# wwwroot embutido e responder à API. Pega o que o --diagnose sozinho não pega.

$ErrorActionPreference = "Stop"

function Assert-Status([string] $url, [int] $expected = 200) {
    try {
        $res = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
        $code = [int]$res.StatusCode
    }
    catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
    }
    if ($code -ne $expected) { throw "$url respondeu $code (esperado $expected)." }
    return $res
}

# "-File" passa "a,b" como um texto só.
$PublishDirs = @($PublishDirs | ForEach-Object { $_ -split "," } | Where-Object { $_ })

foreach ($dir in $PublishDirs) {
    $exe = Join-Path $dir "SoftPrint.Legacy.exe"
    if (-not (Test-Path $exe)) { throw "SoftPrint.Legacy.exe ausente em $dir." }
    Write-Host "== $exe"

    # Stderr de exe nativo não pode virar exceção (Windows PowerShell 5.1): queremos o texto.
    $ErrorActionPreference = "Continue"
    $out = (& $exe --diagnose 2>&1 | ForEach-Object { "$_" } | Out-String)
    $diagExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    Write-Host $out
    if ($diagExit -ne 0) { throw "--diagnose saiu com código $diagExit." }
    if ($out -notmatch "SoftPrint edition: Windows Legacy") { throw "--diagnose sem o marcador do Legacy." }

    $data = Join-Path ([IO.Path]::GetTempPath()) ("softprint-smoke-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    $env:SOFTPRINT_DATA_ROOT = Join-Path $data "data"
    $env:SOFTPRINT_CONFIG_ROOT = Join-Path $data "config"
    $env:SOFTPRINT_HEADLESS = "1"

    # Pasta atual diferente da do exe, como no autostart (System32).
    $proc = Start-Process -FilePath (Resolve-Path $exe) -ArgumentList "--headless" `
        -WorkingDirectory $env:SystemRoot -PassThru -WindowStyle Hidden
    try {
        $base = "http://127.0.0.1:$Port"
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $ready = $false
        while ((Get-Date) -lt $deadline) {
            if ($proc.HasExited) { throw "SoftPrint.Legacy.exe encerrou (código $($proc.ExitCode)) antes de abrir a porta." }
            try {
                Invoke-WebRequest -Uri "$base/dashboard" -UseBasicParsing -TimeoutSec 5 | Out-Null
                $ready = $true
                break
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { throw "Painel não respondeu em $TimeoutSeconds s." }

        $page = Assert-Status "$base/dashboard"
        if ($page.Content -notmatch "<title>SoftPrint") { throw "/dashboard não devolveu o painel: $($page.Content.Substring(0, [Math]::Min(120, $page.Content.Length)))" }
        Assert-Status "$base/js/app.bundle.js" | Out-Null
        Assert-Status "$base/css/app.css" | Out-Null
        Assert-Status "$base/js/pdf.worker.min.mjs" | Out-Null

        $key = (Get-Content (Join-Path $env:SOFTPRINT_DATA_ROOT "api-key.txt") -Raw).Trim()
        $status = Invoke-RestMethod -Uri "$base/api/status" -Headers @{ "X-SoftPrint-Key" = $key } -TimeoutSec 10
        if ($status.application -notmatch "Legacy") { throw "/api/status inesperado: $($status.application)" }
        Write-Host "OK: painel, estáticos e API ($($status.application) $($status.version))."
    }
    finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        $proc.WaitForExit(5000) | Out-Null
        Remove-Item Env:SOFTPRINT_DATA_ROOT, Env:SOFTPRINT_CONFIG_ROOT, Env:SOFTPRINT_HEADLESS -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
    }
}
