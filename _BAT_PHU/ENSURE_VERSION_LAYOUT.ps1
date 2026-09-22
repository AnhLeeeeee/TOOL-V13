param(
    [string]$Root = ""
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$Root = [System.IO.Path]::GetFullPath($Root)
$versionDir = Join-Path $Root '_version'
New-Item -ItemType Directory -Force -Path $versionDir | Out-Null
$layoutMarker = Join-Path $versionDir '.layout_v1_migrated'
$firstMigration = -not (Test-Path -LiteralPath $layoutMarker)

function Test-VersionFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try {
        $value = (Get-Content -LiteralPath $path -Raw).Trim()
        return ($value -match '^\d+\.\d+\.\d+$')
    }
    catch { return $false }
}

function Test-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try {
        $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
        if ([string]::IsNullOrWhiteSpace($raw) -or $raw.IndexOf([char]0) -ge 0) { return $false }
        $null = $raw | ConvertFrom-Json
        return $true
    }
    catch { return $false }
}

function Migrate-LegacyFile([string]$name, [bool]$isJson, [bool]$removeLegacy) {
    $legacy = Join-Path $Root $name
    $target = Join-Path $versionDir $name
    if (-not (Test-Path -LiteralPath $legacy)) { return }

    $legacyValid = if ($isJson) { Test-JsonFile $legacy } else { Test-VersionFile $legacy }
    if (-not $legacyValid) {
        Write-Host "[VERSION] Bo qua file root khong hop le: $legacy" -ForegroundColor Yellow
        return
    }

    # Lan dau ap dung patch: file root cua nguoi dung la nguon dang dung thuc te,
    # nen uu tien no de khong ghi de version/SHA moi hon bang file trong patch.
    if ($firstMigration -or -not (Test-Path -LiteralPath $target)) {
        [System.IO.File]::Copy($legacy, $target, $true)
        Write-Host "[VERSION] Da chuyen vao _version: $name"
    }

    if ($removeLegacy) {
        Remove-Item -LiteralPath $legacy -Force -ErrorAction SilentlyContinue
    }
}

function Try-RecoverVersionTxtFromManifest {
    $target = Join-Path $versionDir 'VERSION.txt'
    if (Test-VersionFile $target) { return $true }

    $candidates = @(
        (Join-Path $versionDir 'version.json'),
        (Join-Path $Root 'version.json')
    )

    foreach ($manifest in $candidates) {
        if (-not (Test-JsonFile $manifest)) { continue }
        try {
            $obj = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
            $value = ([string]$obj.version).Trim().TrimStart('v','V')
            if ($value -match '^\d+\.\d+\.\d+$') {
                [System.IO.File]::WriteAllText(
                    $target,
                    $value + [Environment]::NewLine,
                    (New-Object System.Text.UTF8Encoding($false))
                )
                Write-Host "[VERSION] Da tu phuc hoi _version/VERSION.txt = $value tu $manifest" -ForegroundColor Yellow
                return $true
            }
        }
        catch { }
    }

    $historyCandidates = @(
        (Join-Path $versionDir 'versions.json'),
        (Join-Path $Root 'versions.json')
    )

    foreach ($manifest in $historyCandidates) {
        if (-not (Test-JsonFile $manifest)) { continue }
        try {
            $obj = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
            $items = @($obj.versions)
            if ($items.Count -le 0) { continue }

            $value = ([string]$items[0].version).Trim().TrimStart('v','V')
            if ($value -match '^\d+\.\d+\.\d+$') {
                [System.IO.File]::WriteAllText(
                    $target,
                    $value + [Environment]::NewLine,
                    (New-Object System.Text.UTF8Encoding($false))
                )
                Write-Host "[VERSION] Da tu phuc hoi _version/VERSION.txt = $value tu $manifest" -ForegroundColor Yellow
                return $true
            }
        }
        catch { }
    }

    return $false
}

# VERSION.txt va 2 file lastgood cu khong can nam o root nua.
Migrate-LegacyFile 'VERSION.txt' $false $true
Migrate-LegacyFile 'version.json.lastgood' $true $true
Migrate-LegacyFile 'versions.json.lastgood' $true $true

# Hai manifest root phai giu tam thoi de cac client cu van doc URL cu.
# _version moi la nguon chinh cho source/build moi.
Migrate-LegacyFile 'version.json' $true $false
Migrate-LegacyFile 'versions.json' $true $false

$requiredVersion = Join-Path $versionDir 'VERSION.txt'

# Safety-net: neu VERSION.txt bi mat sau khi copy patch/merge Git,
# tu khoi phuc version tu manifest hop le thay vi lam build chet.
if (-not (Test-VersionFile $requiredVersion)) {
    $null = Try-RecoverVersionTxtFromManifest
}

if (-not (Test-VersionFile $requiredVersion)) {
    throw "Khong tim thay _version/VERSION.txt hop le va khong the phuc hoi tu version.json/versions.json: $requiredVersion"
}

if ($firstMigration) {
    [System.IO.File]::WriteAllText(
        $layoutMarker,
        'layout=v1' + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false))
    )
}
