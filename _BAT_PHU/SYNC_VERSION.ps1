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

if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Khong tim thay VERSION.txt: $versionFile"
}

$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION.txt phai co dang X.Y.Z, vi du 13.6.1. Gia tri hien tai: '$version'"
}

$setupUrl = "https://github.com/AnhLeeeeee/TOOL-V13/releases/latest/download/ToolTikTok_V${version}_Setup.exe"

function Ensure-JsonProperty($obj, [string]$name, $defaultValue) {
    if (-not ($obj.PSObject.Properties.Name -contains $name)) {
        $obj | Add-Member -NotePropertyName $name -NotePropertyValue $defaultValue
    }
}

$json = $null
if (Test-Path -LiteralPath $manifestFile) {
    try {
        # Đọc UTF-8 trực tiếp và loại mọi BOM lặp ở đầu file.
        # Cách này tương thích cả Windows PowerShell 5.1 và PowerShell mới.
        $raw = [System.IO.File]::ReadAllText($manifestFile, [System.Text.Encoding]::UTF8)
        while ($raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF) {
            $raw = $raw.Substring(1)
        }

        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $json = $raw | ConvertFrom-Json
        }
    }
    catch {
        Write-Host "[VERSION] version.json dang loi JSON; se tu tao lai file sach." -ForegroundColor Yellow
        $json = $null
    }
}

if ($null -eq $json) {
    $json = [PSCustomObject]@{
        version = $version
        setupUrl = $setupUrl
        sha256 = ""
        notes = "Cập nhật Tool TikTok V$version."
        channel = "stable"
    }
    $oldVersion = ""
}
else {
    Ensure-JsonProperty $json 'version' ""
    Ensure-JsonProperty $json 'setupUrl' ""
    Ensure-JsonProperty $json 'sha256' ""
    Ensure-JsonProperty $json 'notes' ""
    Ensure-JsonProperty $json 'channel' "stable"

    $oldVersion = [string]$json.version

    if ($oldVersion -ne $version) {
        $json.sha256 = ""
    }

    $json.version = $version
    $json.setupUrl = $setupUrl
    $json.notes = "Cập nhật Tool TikTok V$version."
    if ([string]::IsNullOrWhiteSpace([string]$json.channel)) {
        $json.channel = "stable"
    }
}

if (-not [string]::IsNullOrWhiteSpace($SetupPath)) {
    $resolvedSetup = if ([System.IO.Path]::IsPathRooted($SetupPath)) {
        $SetupPath
    } else {
        Join-Path $Root $SetupPath
    }

    if (-not (Test-Path -LiteralPath $resolvedSetup)) {
        throw "Khong tim thay Setup de tinh SHA-256: $resolvedSetup"
    }

    $json.sha256 = (Get-FileHash -LiteralPath $resolvedSetup -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Luôn ghi lại UTF-8 với ĐÚNG 1 BOM để Windows PowerShell 5.1 đọc ổn định.
$jsonText = $json | ConvertTo-Json -Depth 10
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText(
    $manifestFile,
    $jsonText + [Environment]::NewLine,
    $utf8Bom
)

Write-Host "[VERSION] Da dong bo version = $version"
Write-Host "[VERSION] setupUrl = $setupUrl"
if (-not [string]::IsNullOrWhiteSpace($SetupPath)) {
    Write-Host "[VERSION] Da cap nhat SHA-256 cua Setup vao version.json"
}
