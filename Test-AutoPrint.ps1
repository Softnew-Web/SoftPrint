param([string]$Executable = "$PSScriptRoot\release\AutoPrint\AutoPrint.exe")
$ErrorActionPreference = 'Stop'
$testFolder = Join-Path $PSScriptRoot ('.tools\smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testFolder -Force | Out-Null
Copy-Item -LiteralPath $Executable -Destination $testFolder
Copy-Item -LiteralPath (Join-Path (Split-Path $Executable) 'appsettings.json') -Destination $testFolder
$testExe = Join-Path $testFolder 'AutoPrint.exe'
$testUrl = 'http://127.0.0.1:15178'
$process = $null
function Start-TestApp {
    $script:process = Start-Process -FilePath $testExe -ArgumentList '--urls', $testUrl, '--headless', 'true' -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testFolder 'stdout.log') -RedirectStandardError (Join-Path $testFolder 'stderr.log')
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($script:process.HasExited) { throw "Aplicativo encerrou: $(Get-Content (Join-Path $testFolder 'stderr.log') -Raw)" }
        try {
            $key = (Get-Content (Join-Path $testFolder 'data\api-key.txt') -Raw).Trim()
            $script:headers = @{ 'X-AutoPrint-Key' = $key }
            $null = Invoke-RestMethod "$testUrl/api/status" -Headers $script:headers
            return
        } catch { Start-Sleep -Milliseconds 500 }
    }
    throw 'Aplicativo não iniciou dentro do prazo.'
}
try {
    Start-TestApp
    $status = Invoke-RestMethod "$testUrl/api/status" -Headers $headers
    if (-not $status.simulation) { throw 'Simulação deve estar habilitada.' }
    try { $null = Invoke-RestMethod "$testUrl/api/status"; throw 'Acesso sem chave foi aceito.' }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 401) { throw } }
    $body = @{ reference = 'smoke-001'; text = "Teste de impressão`nAcentos: ação" } | ConvertTo-Json
    $job = Invoke-RestMethod "$testUrl/api/jobs" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    $duplicate = Invoke-RestMethod "$testUrl/api/jobs" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if ($job.id -ne $duplicate.id) { throw 'Referência duplicada gerou outro trabalho.' }
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $result = Invoke-RestMethod "$testUrl/api/jobs/$($job.id)" -Headers $headers
        if ($result.status -eq 'simulated') { break }
        Start-Sleep -Milliseconds 250
    }
    if ($result.status -ne 'simulated') { throw 'Fila não processou o trabalho.' }
    $installedPrinters = Invoke-RestMethod "$testUrl/api/printers" -Headers $headers
    $config = Invoke-RestMethod "$testUrl/api/settings" -Headers $headers
    $chosenPrinter = if ($installedPrinters.Count -gt 0) { $installedPrinters[0] } else { '' }
    $configBody = @{ printerName = $chosenPrinter; simulation = $true; paused = $true; expectedRevision = $config.revision } | ConvertTo-Json
    $updated = Invoke-RestMethod "$testUrl/api/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($configBody))
    try { $null = Invoke-RestMethod "$testUrl/api/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($configBody)); throw 'Configuração antiga sobrescreveu a nova.' }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 409) { throw } }
    $invalidBody = @{ printerName = 'AUTOPRINT-PRINTER-DOES-NOT-EXIST'; simulation = $false; paused = $true; expectedRevision = $updated.revision } | ConvertTo-Json
    try { $null = Invoke-RestMethod "$testUrl/api/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body $invalidBody; throw 'Impressora inválida foi aceita.' }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 400) { throw } }
    Start-Sleep -Milliseconds 700
    $pendingBody = @{ reference = 'config-live-test'; text = 'Teste de reconhecimento de configuração' } | ConvertTo-Json
    $pendingJob = Invoke-RestMethod "$testUrl/api/jobs" -Method Post -Headers $headers -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($pendingBody))
    Start-Sleep -Milliseconds 800
    $pendingResult = Invoke-RestMethod "$testUrl/api/jobs/$($pendingJob.id)" -Headers $headers
    if ($pendingResult.status -ne 'pending') { throw 'Pausa não foi aplicada em tempo real.' }
    Stop-Process -Id $process.Id
    $process.WaitForExit()
    Start-TestApp
    $persisted = Invoke-RestMethod "$testUrl/api/settings" -Headers $headers
    if (-not $persisted.paused -or $persisted.revision -ne $updated.revision -or $persisted.printerName -ne $chosenPrinter) { throw 'Configuração não persistiu após reinício.' }
    $resumeBody = @{ printerName = $chosenPrinter; simulation = $true; paused = $false; expectedRevision = $persisted.revision } | ConvertTo-Json
    $resumed = Invoke-RestMethod "$testUrl/api/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($resumeBody))
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $liveResult = Invoke-RestMethod "$testUrl/api/jobs/$($pendingJob.id)" -Headers $headers
        if ($liveResult.status -eq 'simulated') { break }
        Start-Sleep -Milliseconds 250
    }
    if ($liveResult.status -ne 'simulated' -or $liveResult.settingsRevision -ne $resumed.revision -or $liveResult.printerName -ne $chosenPrinter) { throw 'Processamento não reconheceu a configuração nova.' }
    $saved = Invoke-RestMethod "$testUrl/api/jobs/$($job.id)" -Headers $headers
    if ($saved.status -ne 'simulated' -or $saved.text -ne "Teste de impressão`nAcentos: ação") { throw 'Histórico não persistiu corretamente.' }
    Write-Output 'PASSOU: autenticação, fila, deduplicação, impressoras, configuração em tempo real, pausa, retomada, conflito, validação e persistência após reinício.'
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id }
}
