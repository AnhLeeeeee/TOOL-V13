param(
    [string]$Root = "",
    [string]$SetupPath = ""
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$Root = [System.IO.Path]::GetFullPath($Root)

$versionFile = Join-Path $Root 'VERSION.txt'
$manifestFile = Join-Path $Root 'version.json'

if (-not (Test-Path $versionFile)) {
    throw "Khong tim thay VERSION.txt: $versionFile"
}

$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION.txt phai co dang X.Y.Z, vi du 13.5.4. Gia tri hien tai: '$version'"
}

if (Test-Path $manifestFile) {
    $json = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    $oldVersion = [string]$json.version

    if ($oldVersion -ne $version) {
        $json.sha256 = ""
    }

    $json.version = $version

    if (-not [string]::IsNullOrWhiteSpace($SetupPath)) {
        $resolvedSetup = if ([System.IO.Path]::IsPathRooted($SetupPath)) {
            $SetupPath
        } else {
            Join-Path $Root $SetupPath
        }

        if (-not (Test-Path $resolvedSetup)) {
            throw "Khong tim thay Setup de tinh SHA-256: $resolvedSetup"
        }

        $json.sha256 = (Get-FileHash -LiteralPath $resolvedSetup -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestFile -Encoding UTF8
}

Write-Host "[VERSION] Da dong bo version = $version"
if (-not [string]::IsNullOrWhiteSpace($SetupPath)) {
    Write-Host "[VERSION] Da cap nhat SHA-256 cua Setup vao version.json"
}
