param(
    [string]$Root = ""
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$Root = [System.IO.Path]::GetFullPath($Root)
$versionFile = Join-Path $Root 'VERSION.txt'
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Khong tim thay VERSION.txt: $versionFile"
}
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION.txt khong hop le: $version"
}

$outDir = Join-Path $Root 'SOURCE_OUTPUT'
$stage = Join-Path $env:TEMP ("ToolTikTok_Source_{0}_{1}" -f $version, [Guid]::NewGuid().ToString('N'))
$outZip = Join-Path $outDir ("ToolTikTok_V{0}_SOURCE_CLEAN.zip" -f $version)

$excludeTop = @(
    '.git', '.vs',
    'bin', 'obj',
    'dist_v13', 'dist_v125',
    'publish_v13_5_vm', 'SETUP_OUTPUT', 'RELEASE_OUTPUT', 'SOURCE_OUTPUT',
    'TikTokProfiles', 'profiles', 'logs', 'config_backups', 'live_cu_tam',
    'build_verify'
)

try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    Get-ChildItem -LiteralPath $Root -Force | ForEach-Object {
        if ($excludeTop -contains $_.Name) { return }
        if (-not $_.PSIsContainer -and ($_.Extension -in @('.zip', '.exe', '.pdb', '.log'))) { return }
        Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force
    }

    Get-ChildItem -LiteralPath $stage -Directory -Recurse -Force |
        Where-Object { $_.Name -in @('bin','obj','.vs','logs','config_backups','live_cu_tam') } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Get-ChildItem -LiteralPath $stage -File -Recurse -Force |
        Where-Object { $_.Extension -in @('.pdb','.log','.zip','.exe') } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    if (Test-Path -LiteralPath $outZip) { Remove-Item -LiteralPath $outZip -Force }
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if (-not $tar) { throw 'Khong tim thay tar.exe tren Windows.' }

    & $tar.Source -a -c -f $outZip -C $stage .
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outZip)) {
        throw 'Tao SOURCE ZIP that bai.'
    }

    $sizeMb = [Math]::Round((Get-Item -LiteralPath $outZip).Length / 1MB, 2)
    Write-Host ""
    Write-Host "========================================"
    Write-Host "DA TAO SOURCE ZIP SACH V$version"
    Write-Host $outZip
    Write-Host "Dung luong: $sizeMb MB"
    Write-Host "Da loai: bin/obj/publish/setup/dist/profile/log/zip/exe"
    Write-Host "========================================"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
