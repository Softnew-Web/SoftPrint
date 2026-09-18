$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    npx --yes esbuild AutoPrint/wwwroot/js/app.js `
        --bundle `
        --format=iife `
        --target=chrome109 `
        --outfile=AutoPrint/wwwroot/js/app.bundle.js
    if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar app.bundle.js." }
    Copy-Item node_modules/pdfjs-dist/legacy/build/pdf.worker.min.mjs `
        AutoPrint/wwwroot/js/pdf.worker.min.mjs -Force
}
finally {
    Pop-Location
}
