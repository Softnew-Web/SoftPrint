$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & "$PSScriptRoot\build-js.ps1"
    $jobs = @(
        @{ Name = "windows-modern-x64"; Project = "SoftPrint.Host.Windows/SoftPrint.csproj"; Rid = "win-x64"; Out = "dist/symbols/windows-modern-x64" },
        @{ Name = "windows-legacy-x64"; Project = "SoftPrint.Host.Windows.Legacy/SoftPrint.Host.Windows.Legacy.csproj"; Rid = "win-x64"; Out = "dist/symbols/windows-legacy-x64" }
    )
    foreach ($job in $jobs) {
        Write-Host "Publicando símbolos $($job.Name)..."
        & dotnet publish $job.Project `
            -c Release -r $job.Rid --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=portable `
            -p:DebugSymbols=true `
            -o $job.Out
        if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar símbolos: $($job.Name)" }
    }
    Write-Host "Símbolos em dist/symbols (artifact privado do CI — não vão no release público)."
}
finally {
    Pop-Location
}
