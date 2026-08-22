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
$latestManifestFile = Join-Path $Root 'version.json'
$historyManifestFile = Join-Path $Root 'versions.json'

if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Khong tim thay VERSION.txt: $versionFile"
}

$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION.txt phai co dang X.Y.Z, vi du 13.6.4. Gia tri hien tai: '$version'"
}

$setupFileName = "ToolTikTok_V${version}_Setup.exe"
# QUAN TRONG: link co dinh theo tag, khong dung releases/latest/download.
$setupUrl = "https://github.com/AnhLeeeeee/TOOL-V13/releases/download/v${version}/${setupFileName}"
$today = Get-Date -Format 'yyyy-MM-dd'

function Read-JsonSafe([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
        while ($raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF) {
            $raw = $raw.Substring(1)
        }
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    }
    catch {
        Write-Host "[VERSION] JSON loi tai $path; se tao lai file sach." -ForegroundColor Yellow
        return $null
    }
}

function Ensure-JsonProperty($obj, [string]$name, $defaultValue) {
    if (-not ($obj.PSObject.Properties.Name -contains $name)) {
        $obj | Add-Member -NotePropertyName $name -NotePropertyValue $defaultValue
    }
}

function Write-JsonBom([string]$path, $obj) {
    $jsonText = $obj | ConvertTo-Json -Depth 20
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($path, $jsonText + [Environment]::NewLine, $utf8Bom)
}

function Normalize-Version([string]$value) {
    if ($null -eq $value) { return '' }
    return $value.Trim().TrimStart('v','V')
}

# ------------------------------------------------------------
# 1) Doc versions.json truoc de bao toan status cua ban hien tai
# ------------------------------------------------------------
$history = Read-JsonSafe $historyManifestFile
if ($null -eq $history) {
    $history = [PSCustomObject]@{
        schemaVersion = 1
        versions = @()
    }
}
else {
    Ensure-JsonProperty $history 'schemaVersion' 1
    Ensure-JsonProperty $history 'versions' @()
}

$historyItems = @($history.versions)
$currentHistory = $historyItems | Where-Object { (Normalize-Version ([string]$_.version)) -eq $version } | Select-Object -First 1

$preservedStatus = 'stable'
$preservedChannel = 'stable'
$preservedReleaseDate = $today
$preservedAllowInstall = $true
$preservedNotes = "Cập nhật Tool TikTok V$version."

if ($null -ne $currentHistory) {
    if (-not [string]::IsNullOrWhiteSpace([string]$currentHistory.status)) { $preservedStatus = [string]$currentHistory.status }
    if (-not [string]::IsNullOrWhiteSpace([string]$currentHistory.channel)) { $preservedChannel = [string]$currentHistory.channel }
    if (-not [string]::IsNullOrWhiteSpace([string]$currentHistory.releaseDate)) { $preservedReleaseDate = [string]$currentHistory.releaseDate }
    if ($currentHistory.PSObject.Properties.Name -contains 'allowInstall') { $preservedAllowInstall = [bool]$currentHistory.allowInstall }
    if (-not [string]::IsNullOrWhiteSpace([string]$currentHistory.notes)) { $preservedNotes = [string]$currentHistory.notes }
}

# ------------------------------------------------------------
# 2) Dong bo version.json (ban moi nhat)
# ------------------------------------------------------------
$latest = Read-JsonSafe $latestManifestFile
if ($null -eq $latest) {
    $latest = [PSCustomObject]@{}
}

Ensure-JsonProperty $latest 'version' ''
Ensure-JsonProperty $latest 'setupUrl' ''
Ensure-JsonProperty $latest 'sha256' ''
Ensure-JsonProperty $latest 'notes' ''
Ensure-JsonProperty $latest 'channel' 'stable'
Ensure-JsonProperty $latest 'status' 'stable'
Ensure-JsonProperty $latest 'releaseDate' $today
Ensure-JsonProperty $latest 'allowInstall' $true

$oldVersion = Normalize-Version ([string]$latest.version)
if ($oldVersion -ne $version) {
    $latest.sha256 = ''
    $latest.releaseDate = $preservedReleaseDate
}

$latest.version = $version
$latest.setupUrl = $setupUrl
$latest.notes = $preservedNotes
$latest.channel = $preservedChannel
$latest.status = $preservedStatus
$latest.releaseDate = $preservedReleaseDate
$latest.allowInstall = $preservedAllowInstall

$setupSha = ''
if (-not [string]::IsNullOrWhiteSpace($SetupPath)) {
    $resolvedSetup = if ([System.IO.Path]::IsPathRooted($SetupPath)) {
        $SetupPath
    } else {
        Join-Path $Root $SetupPath
    }

    if (-not (Test-Path -LiteralPath $resolvedSetup)) {
        throw "Khong tim thay Setup de tinh SHA-256: $resolvedSetup"
    }

    $setupSha = (Get-FileHash -LiteralPath $resolvedSetup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($setupSha -notmatch '^[0-9a-f]{64}$') {
        throw "SHA-256 khong hop le: $setupSha"
    }
    $latest.sha256 = $setupSha
}

Write-JsonBom $latestManifestFile $latest

# ------------------------------------------------------------
# 3) Khi da co Setup + SHA thi upsert vao versions.json
#    Khong tao entry nua vo khi moi chi dang publish.
# ------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($setupSha)) {
    $others = @($historyItems | Where-Object { (Normalize-Version ([string]$_.version)) -ne $version })

    $entry = [PSCustomObject]@{
        version = $version
        setupUrl = $setupUrl
        sha256 = $setupSha
        notes = $preservedNotes
        channel = $preservedChannel
        status = $preservedStatus
        releaseDate = $preservedReleaseDate
        allowInstall = $preservedAllowInstall
    }

    $all = @($entry) + $others
    $sorted = @($all | Sort-Object -Property @{ Expression = {
        try { [version](Normalize-Version ([string]$_.version)) }
        catch { [version]'0.0.0' }
    }; Descending = $true }, @{ Expression = { [string]$_.version }; Descending = $true })

    $history.schemaVersion = 1
    $history.versions = $sorted
    Write-JsonBom $historyManifestFile $history
}

Write-Host "[VERSION] Da dong bo version = $version"
Write-Host "[VERSION] setupUrl = $setupUrl"
Write-Host "[VERSION] version.json = $latestManifestFile"
if (-not [string]::IsNullOrWhiteSpace($setupSha)) {
    Write-Host "[VERSION] SHA-256 = $setupSha"
    Write-Host "[VERSION] Da upsert vao versions.json = $historyManifestFile"
}
else {
    Write-Host "[VERSION] Chua co Setup: versions.json duoc giu nguyen, se cap nhat sau khi build Setup."
}
