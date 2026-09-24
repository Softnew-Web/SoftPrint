param(
    [Parameter(Mandatory = $true)]
    [string[]] $Files,

    # Se true e os secrets de assinatura faltarem, falha o job (útil quando a política exige assinatura).
    [bool] $FailIfSecretsMissing = $false
)

$ErrorActionPreference = "Stop"

$pfxB64 = $env:CODE_SIGNING_PFX_BASE64
$password = $env:CODE_SIGNING_PASSWORD
if ([string]::IsNullOrWhiteSpace($pfxB64) -or [string]::IsNullOrWhiteSpace($password)) {
    $msg = "Assinatura Authenticode ignorada (secrets CODE_SIGNING_PFX_BASE64 / CODE_SIGNING_PASSWORD ausentes)."
    if ($FailIfSecretsMissing) { throw $msg }
    Write-Host $msg
    exit 0
}

$tempDir = Join-Path $env:TEMP "softprint-codesign"
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
$pfxPath = Join-Path $tempDir "softprint.pfx"
try {
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($pfxB64.Trim()))

    $signtool = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\signtool.exe"
    ) | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName

    if (-not $signtool) {
        throw "signtool.exe não encontrado. Instale Windows SDK no runner ou use windows-latest."
    }

    $signed = 0
    foreach ($file in $Files) {
        if (-not (Test-Path $file)) {
            Write-Host "Arquivo ausente, pulando: $file"
            continue
        }
        Write-Host "Assinando $file…"
        & $signtool sign /f $pfxPath /p $password /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $file
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $signed++
    }

    if ($signed -lt 1) {
        throw "Nenhum arquivo foi assinado."
    }
}
finally {
    if (Test-Path $pfxPath) { Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue }
}
