$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
$failed = @()
try {
    & "$PSScriptRoot\build-js.ps1"
    $jobs = @(
        @{ Name = "windows-modern-x64"; Args = @("publish", "SoftPrint.Host.Windows/SoftPrint.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", "dist/windows-modern-x64") },
        @{ Name = "windows-modern-x86"; Args = @("publish", "SoftPrint.Host.Windows/SoftPrint.csproj", "-c", "Release", "-r", "win-x86", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", "dist/windows-modern-x86") },
        @{ Name = "windows-legacy-x64"; Args = @("publish", "SoftPrint.Host.Windows.Legacy/SoftPrint.Host.Windows.Legacy.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", "dist/windows-legacy-x64") },
        @{ Name = "windows-legacy-x86"; Args = @("publish", "SoftPrint.Host.Windows.Legacy/SoftPrint.Host.Windows.Legacy.csproj", "-c", "Release", "-r", "win-x86", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", "dist/windows-legacy-x86") },
        @{ Name = "linux-x64"; Args = @("publish", "SoftPrint.Host.Linux/SoftPrint.Host.Linux.csproj", "-c", "Release", "-r", "linux-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", "dist/linux-x64") }
    )
    foreach ($job in $jobs) {
        Write-Host "Publicando $($job.Name)..."
        & dotnet @($job.Args)
        if ($LASTEXITCODE -ne 0) { $failed += $job.Name }
    }
    if ($failed.Count -gt 0) {
        throw "Falha ao publicar: $($failed -join ', ')"
    }
}
finally {
    Pop-Location
}
