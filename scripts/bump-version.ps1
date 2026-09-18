param(
    [ValidateSet("patch", "minor", "major")]
    [string] $Bump = "patch",
    [string] $SetVersion = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Write-Utf8NoBom([string] $path, [string] $content) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($path, $content, $utf8)
}

function Get-CurrentVersion {
    $raw = [System.IO.File]::ReadAllText((Join-Path $root "VERSION")).Trim().TrimStart([char]0xFEFF)
    if ($raw -notmatch '^\d+\.\d+\.\d+$') {
        throw "VERSION inválida: '$raw' (use X.Y.Z)"
    }
    return $raw
}

function Step-Version([string] $current, [string] $kind) {
    $parts = @($current.Split('.') | ForEach-Object { [int]$_ })
    switch ($kind) {
        "major" { $parts[0]++; $parts[1] = 0; $parts[2] = 0 }
        "minor" { $parts[1]++; $parts[2] = 0 }
        default { $parts[2]++ }
    }
    return "{0}.{1}.{2}" -f $parts[0], $parts[1], $parts[2]
}

function Set-VersionFiles([string] $version) {
    Write-Utf8NoBom (Join-Path $root "VERSION") "$version`n"

    $versionCs = Join-Path $root "SoftPrint.Domain\SoftPrintVersion.cs"
    $cs = [System.IO.File]::ReadAllText($versionCs)
    $cs2 = [regex]::Replace($cs, 'public const string Current = "[^"]+";', "public const string Current = `"$version`";")
    if ($cs2 -notmatch [regex]::Escape("Current = `"$version`"")) {
        throw "Não foi possível atualizar SoftPrintVersion.cs"
    }
    Write-Utf8NoBom $versionCs $cs2

    $iss = Join-Path $root "installer\SoftPrint.iss"
    $issText = [System.IO.File]::ReadAllText($iss)
    $iss2 = [regex]::Replace($issText, '#define AppVersion "[^"]+"', "#define AppVersion `"$version`"")
    if ($iss2 -notmatch [regex]::Escape("#define AppVersion `"$version`"")) {
        throw "Não foi possível atualizar SoftPrint.iss"
    }
    Write-Utf8NoBom $iss $iss2
}

$next = if ($SetVersion) {
    if ($SetVersion -notmatch '^\d+\.\d+\.\d+$') { throw "SetVersion inválida: $SetVersion" }
    $SetVersion
} else {
    Step-Version (Get-CurrentVersion) $Bump
}

Set-VersionFiles $next
Write-Output $next
