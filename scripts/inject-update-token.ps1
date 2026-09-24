param(
    [string] $Token = $env:SOFTPRINT_UPDATE_TOKEN
)

# Política SoftPrint: NÃO embutir PAT nos installs/zips.
# Repos privados: use UPDATE_GITHUB_TOKEN no .env local do cliente, ou torne os releases públicos.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "inject-update-token.ps1: embutir token nos pacotes está desabilitado (segurança)."
if (-not [string]::IsNullOrWhiteSpace($Token)) {
    Write-Host "SOFTPRINT_UPDATE_TOKEN está definido no ambiente do CI, mas NÃO será escrito em dist/."
    Write-Host "Clientes em repo privado devem configurar UPDATE_GITHUB_TOKEN localmente."
}

# Remove tokens residuais de builds anteriores.
Get-ChildItem (Join-Path $root "dist") -Recurse -Filter "update-github.token" -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "Removendo token residual: $($_.FullName)"
        Remove-Item $_.FullName -Force
    }

exit 0
