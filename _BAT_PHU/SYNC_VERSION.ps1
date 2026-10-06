param(
    [string]$Root = "",
    [string]$SetupPath = "",
    [string]$ManagerExePath = "",
    [switch]$GenerateBuildIdentity,
    [switch]$RequireBuildIdentity
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$Root = [System.IO.Path]::GetFullPath($Root)

$layoutHelper = Join-Path $PSScriptRoot 'ENSURE_VERSION_LAYOUT.ps1'
if (Test-Path -LiteralPath $layoutHelper) {
    & $layoutHelper -Root $Root
}

$versionDir = Join-Path $Root '_version'
New-Item -ItemType Directory -Force -Path $versionDir | Out-Null

$versionFile = Join-Path $versionDir 'VERSION.txt'
$latestManifestFile = Join-Path $versionDir 'version.json'
$historyManifestFile = Join-Path $versionDir 'versions.json'

# Hai file o root chi la ban tuong thich tam thoi cho cac client cu
# dang doc /version.json va /versions.json tren GitHub. Nguon chinh nam trong _version.
$legacyLatestManifestFile = Join-Path $Root 'version.json'
$legacyHistoryManifestFile = Join-Path $Root 'versions.json'

$publishDir = Join-Path $Root 'publish_v13_5_vm'
$buildIdentityFile = Join-Path $publishDir 'build_identity.json'

if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Khong tim thay _version/VERSION.txt: $versionFile"
}

$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "_version/VERSION.txt phai co dang X.Y.Z, vi du 13.6.4. Gia tri hien tai: '$version'"
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
        Write-Host "[VERSION] JSON loi tai $path; thu phuc hoi lastgood." -ForegroundColor Yellow
        $backup = $path + '.lastgood'
        if (Test-Path -LiteralPath $backup) {
            try {
                $backupRaw = [System.IO.File]::ReadAllText($backup, [System.Text.Encoding]::UTF8)
                while ($backupRaw.Length -gt 0 -and $backupRaw[0] -eq [char]0xFEFF) {
                    $backupRaw = $backupRaw.Substring(1)
                }
                if ($backupRaw.IndexOf([char]0) -lt 0 -and -not [string]::IsNullOrWhiteSpace($backupRaw)) {
                    Write-Host "[VERSION] Da doc duoc ban lastgood: $backup" -ForegroundColor Yellow
                    return ($backupRaw | ConvertFrom-Json)
                }
            }
            catch { }
        }
        return $null
    }
}

function Ensure-JsonProperty($obj, [string]$name, $defaultValue) {
    if (-not ($obj.PSObject.Properties.Name -contains $name)) {
        $obj | Add-Member -NotePropertyName $name -NotePropertyValue $defaultValue
    }
}

function Test-JsonText([string]$jsonText, [string]$label) {
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        throw "$label rong; dung publish."
    }
    if ($jsonText.IndexOf([char]0) -ge 0) {
        throw "$label chua byte NUL/0x00; dung publish."
    }
    try {
        $null = $jsonText | ConvertFrom-Json
    }
    catch {
        throw "$label khong parse duoc JSON: $($_.Exception.Message)"
    }
}

function Write-JsonUtf8NoBom([string]$path, $obj) {
    $jsonText = $obj | ConvertTo-Json -Depth 20
    Test-JsonText $jsonText ([System.IO.Path]::GetFileName($path))

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $dir = [System.IO.Path]::GetDirectoryName($path)
    if ([string]::IsNullOrWhiteSpace($dir)) { $dir = (Get-Location).Path }
    $tmp = Join-Path $dir ('.' + [System.IO.Path]::GetFileName($path) + '.tmp.' + [Guid]::NewGuid().ToString('N'))

    try {
        [System.IO.File]::WriteAllText($tmp, $jsonText + [Environment]::NewLine, $utf8NoBom)

        # Doc lai file tam tu dia va parse lai truoc khi thay file chinh.
        $verify = [System.IO.File]::ReadAllText($tmp, [System.Text.Encoding]::UTF8)
        Test-JsonText $verify ([System.IO.Path]::GetFileName($path) + ' temp')

        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Length -gt 0)) {
            $backup = $path + '.lastgood'
            try {
                $current = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
                Test-JsonText $current ([System.IO.Path]::GetFileName($path) + ' current')
                [System.IO.File]::Copy($path, $backup, $true)
            }
            catch {
                Write-Host "[VERSION] File hien tai khong hop le, khong ghi de lastgood: $path" -ForegroundColor Yellow
            }
        }

        # Khong dung File.Replace(..., $null, ...) tren Windows PowerShell 5.1:
        # mot so may/.NET Framework nem ArgumentException "The path is not of a legal form"
        # cho tham so backup null. File tam da duoc ghi + parse xong; copy de len file
        # chinh, sau do doc/parse lai. Neu co loi thi phuc hoi tu .lastgood.
        $fullPath = [System.IO.Path]::GetFullPath($path)
        try {
            [System.IO.File]::Copy($tmp, $fullPath, $true)

            $final = [System.IO.File]::ReadAllText($fullPath, [System.Text.Encoding]::UTF8)
            Test-JsonText $final ([System.IO.Path]::GetFileName($fullPath) + ' final')
        }
        catch {
            $writeError = $_
            $backup = $fullPath + '.lastgood'
            if (Test-Path -LiteralPath $backup) {
                try {
                    $backupText = [System.IO.File]::ReadAllText($backup, [System.Text.Encoding]::UTF8)
                    Test-JsonText $backupText ([System.IO.Path]::GetFileName($backup))
                    [System.IO.File]::Copy($backup, $fullPath, $true)
                    Write-Host "[VERSION] Ghi manifest loi; da phuc hoi lastgood: $fullPath" -ForegroundColor Yellow
                }
                catch {
                    Write-Host "[VERSION] Khong phuc hoi duoc lastgood: $backup" -ForegroundColor Red
                }
            }
            throw $writeError
        }
    }
    finally {
        if (Test-Path -LiteralPath $tmp) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    }
}

function Sync-LegacyManifestCopy([string]$source, [string]$destination) {
    if (-not (Test-Path -LiteralPath $source)) { return }
    try {
        [System.IO.File]::Copy($source, $destination, $true)
    }
    catch {
        throw "Khong dong bo duoc manifest tuong thich: $destination. $($_.Exception.Message)"
    }
}

function New-DefaultNotes([string]$value) {
    # ASCII-only source text de Windows PowerShell 5.1 khong lam hong tieng Viet
    # khi file .ps1 duoc doc voi code page legacy.
    $prefix = [System.Text.Encoding]::UTF8.GetString(
        [System.Convert]::FromBase64String('Q+G6rXAgbmjhuq10IFRvb2wgVGlrVG9rIFY=')
    )
    return $prefix + $value + '.'
}

function Normalize-Version([string]$value) {
    if ($null -eq $value) { return '' }
    return $value.Trim().TrimStart('v','V')
}

function Resolve-PathFromRoot([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return '' }
    if ([System.IO.Path]::IsPathRooted($value)) {
        return [System.IO.Path]::GetFullPath($value)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $Root $value))
}

function Get-Sha256Lower([string]$path, [string]$label) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Khong tim thay $label de tinh SHA-256: $path"
    }
    $sha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha -notmatch '^[0-9a-f]{64}$') {
        throw "SHA-256 $label khong hop le: $sha"
    }
    return $sha
}

function New-BuildId([string]$versionValue) {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd_HHmmss')
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 6).ToUpperInvariant()
    return "${versionValue}_${stamp}_${suffix}"
}

# ------------------------------------------------------------
# Build identity (Shadow Mode)
# ------------------------------------------------------------
$resolvedManagerExe = ''
if (-not [string]::IsNullOrWhiteSpace($ManagerExePath)) {
    $resolvedManagerExe = Resolve-PathFromRoot $ManagerExePath
}
else {
    $defaultManagerExe = Join-Path $publishDir 'ToolTikTokManagerV13.exe'
    if (Test-Path -LiteralPath $defaultManagerExe) {
        $resolvedManagerExe = $defaultManagerExe
    }
}

if ($GenerateBuildIdentity) {
    if ([string]::IsNullOrWhiteSpace($resolvedManagerExe)) {
        throw "GenerateBuildIdentity yeu cau Manager EXE da publish."
    }

    $managerHashForNewBuild = Get-Sha256Lower $resolvedManagerExe 'Manager EXE'
    $identity = [PSCustomObject]@{
        schemaVersion = 1
        version = $version
        buildId = New-BuildId $version
        managerExeSha256 = $managerHashForNewBuild
        createdAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    }

    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    Write-JsonUtf8NoBom $buildIdentityFile $identity
    Write-Host "[BUILD] Da tao build_identity.json" -ForegroundColor Green
    Write-Host "[BUILD] buildId = $($identity.buildId)"
    Write-Host "[BUILD] managerExeSha256 = $($identity.managerExeSha256)"
}

$buildIdentity = Read-JsonSafe $buildIdentityFile
$buildId = ''
$managerExeSha256 = ''
$buildCreatedAtUtc = ''

if ($null -ne $buildIdentity) {
    $identityVersion = Normalize-Version ([string]$buildIdentity.version)
    $identityBuildId = ([string]$buildIdentity.buildId).Trim()
    $identityManagerSha = ([string]$buildIdentity.managerExeSha256).Trim().ToLowerInvariant()

    $identityValid =
        ($identityVersion -eq $version) -and
        ($identityBuildId -match '^[A-Za-z0-9._-]{1,150}$') -and
        ($identityManagerSha -match '^[0-9a-f]{64}$')

    if ($identityValid) {
        $buildId = $identityBuildId
        $managerExeSha256 = $identityManagerSha
        if ($buildIdentity.PSObject.Properties.Name -contains 'createdAtUtc') {
            $buildCreatedAtUtc = ([string]$buildIdentity.createdAtUtc).Trim()
        }

        $mustVerifyManagerHash =
            $GenerateBuildIdentity -or
            $RequireBuildIdentity -or
            (-not [string]::IsNullOrWhiteSpace($ManagerExePath)) -or
            (-not [string]::IsNullOrWhiteSpace($SetupPath))

        if ($mustVerifyManagerHash) {
            if ([string]::IsNullOrWhiteSpace($resolvedManagerExe)) {
                throw "Khong tim thay Manager EXE de doi chieu build identity."
            }

            $actualManagerSha = Get-Sha256Lower $resolvedManagerExe 'Manager EXE'
            if ($actualManagerSha -ne $managerExeSha256) {
                throw "Manager EXE da thay doi sau khi tao build identity. Expected=$managerExeSha256 Actual=$actualManagerSha"
            }
        }
    }
    elseif ($RequireBuildIdentity) {
        throw "build_identity.json khong hop le hoac khong khop version $version."
    }
}
elseif ($RequireBuildIdentity) {
    throw "Khong tim thay build_identity.json. Hay publish lai bang TAO_BAN_CAI_V13_5.bat."
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
$preservedNotes = New-DefaultNotes $version

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
Ensure-JsonProperty $latest 'buildId' ''
Ensure-JsonProperty $latest 'managerExeSha256' ''

$oldVersion = Normalize-Version ([string]$latest.version)
if ($oldVersion -ne $version) {
    $latest.sha256 = ''
    $latest.buildId = ''
    $latest.managerExeSha256 = ''
    $latest.releaseDate = $preservedReleaseDate
}

$latest.version = $version
$latest.setupUrl = $setupUrl
$latest.notes = $preservedNotes
$latest.channel = $preservedChannel
$latest.status = $preservedStatus
$latest.releaseDate = $preservedReleaseDate
$latest.allowInstall = $preservedAllowInstall
if (-not [string]::IsNullOrWhiteSpace($buildId)) {
    $latest.buildId = $buildId
    $latest.managerExeSha256 = $managerExeSha256
}

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

    $setupSha = Get-Sha256Lower $resolvedSetup 'Setup'
    $latest.sha256 = $setupSha
}

Write-JsonUtf8NoBom $latestManifestFile $latest

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
        buildId = $buildId
        managerExeSha256 = $managerExeSha256
    }

    $all = @($entry) + $others
    $sorted = @($all | Sort-Object -Property @{ Expression = {
        try { [version](Normalize-Version ([string]$_.version)) }
        catch { [version]'0.0.0' }
    }; Descending = $true }, @{ Expression = { [string]$_.version }; Descending = $true })

    $history.schemaVersion = 1
    $history.versions = $sorted
    Write-JsonUtf8NoBom $historyManifestFile $history
}

# Luon giu 2 manifest root dong bo de cac ban Tool cu van nhan duoc cap nhat.
# Khi tat ca client da chuyen sang _version, co the bo 2 ban tuong thich nay.
Sync-LegacyManifestCopy $latestManifestFile $legacyLatestManifestFile
Sync-LegacyManifestCopy $historyManifestFile $legacyHistoryManifestFile

Write-Host "[VERSION] Da dong bo version = $version"
Write-Host "[VERSION] setupUrl = $setupUrl"
Write-Host "[VERSION] _version/version.json = $latestManifestFile"
if (-not [string]::IsNullOrWhiteSpace($buildId)) {
    Write-Host "[BUILD] buildId = $buildId"
    Write-Host "[BUILD] managerExeSha256 = $managerExeSha256"
    Write-Host "[BUILD] identity = $buildIdentityFile"
}
if (-not [string]::IsNullOrWhiteSpace($setupSha)) {
    Write-Host "[VERSION] SHA-256 = $setupSha"
    Write-Host "[VERSION] Da upsert vao _version/versions.json = $historyManifestFile"
}
else {
    Write-Host "[VERSION] Chua co Setup: _version/versions.json duoc giu nguyen, se cap nhat sau khi build Setup."
}
