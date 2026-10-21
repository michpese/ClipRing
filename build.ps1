<#
.SYNOPSIS
    Baut ClipRing und legt eine passende FFmpeg-Version daneben.

.EXAMPLE
    .\build.ps1                    # Release, braucht installierte .NET 10 Desktop Runtime
    .\build.ps1 -SelfContained     # bringt die .NET-Runtime mit (größer, läuft überall)
    .\build.ps1 -SkipFfmpeg        # FFmpeg nicht herunterladen (eigene ffmpeg.exe verwenden)
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SelfContained,
    [switch]$SkipFfmpeg,
    [string]$Output = (Join-Path $PSScriptRoot 'dist\ClipRing')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is much faster without the progress bar

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# ------------------------------------------------------------------ 1. .NET SDK
Step '.NET SDK prüfen'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK nicht gefunden. Installieren mit:  winget install Microsoft.DotNet.SDK.10'
}
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^(1\d|[2-9]\d)\.' })) {
    throw ".NET 10 SDK (oder neuer) fehlt. Gefunden: $($sdks -join ', '). Installieren mit:  winget install Microsoft.DotNet.SDK.10"
}
Write-Host ($sdks -join "`n")

# ------------------------------------------------------------------ 2. Build
Step "ClipRing bauen ($Configuration, self-contained: $SelfContained)"
$project = Join-Path $PSScriptRoot 'src\ClipRing\ClipRing.csproj'
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', ($(if ($SelfContained) { 'true' } else { 'false' })),
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=embedded',
    '-o', $Output
)
if ($env:GITHUB_ACTIONS -eq 'true') {
    # Classic one-line diagnostics with full paths, so the workflow can turn them into annotations.
    $publishArgs += @('-tl:off', '-p:GenerateFullPaths=true', '-clp:NoSummary')
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen (Code $LASTEXITCODE)" }

# ------------------------------------------------------------------ 3. FFmpeg
if ($SkipFfmpeg) {
    Write-Host "`nFFmpeg übersprungen. Lege ffmpeg.exe nach $Output\ffmpeg\ oder wähle den Pfad in ClipRing unter 'Allgemein'."
}
else {
    $ffDir = Join-Path $Output 'ffmpeg'
    if (Test-Path (Join-Path $ffDir 'ffmpeg.exe')) {
        Step 'FFmpeg ist bereits vorhanden'
    }
    else {
        Step 'FFmpeg herunterladen (BtbN/FFmpeg-Builds, GPL, mit NVENC/AMF/QSV/D3D12/x264/x265/SVT-AV1)'
        $headers = @{ 'User-Agent' = 'ClipRing-build' }
        # In GitHub Actions the token avoids the anonymous API rate limit of shared runners.
        if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
        $release = Invoke-RestMethod 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest' -Headers $headers

        # Newest stable release branch, e.g. ffmpeg-n8.1-latest-win64-gpl-8.1.zip
        $candidates = foreach ($a in $release.assets) {
            if ($a.name -match '^ffmpeg-n(\d+)\.(\d+)-latest-win64-gpl-\d+\.\d+\.zip$') {
                [pscustomobject]@{ Asset = $a; Version = [version]"$($Matches[1]).$($Matches[2])" }
            }
        }
        $pick = $candidates | Sort-Object Version -Descending | Select-Object -First 1
        if ($pick) {
            $asset = $pick.Asset
        }
        else {
            $asset = $release.assets | Where-Object { $_.name -eq 'ffmpeg-master-latest-win64-gpl.zip' } | Select-Object -First 1
        }
        if (-not $asset) { throw 'Kein passender FFmpeg-Build gefunden. Bitte manuell herunterladen (siehe README).' }

        $temp = Join-Path ([IO.Path]::GetTempPath()) ("cliprings-ffmpeg-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $temp | Out-Null
        try {
            $zip = Join-Path $temp $asset.name
            Write-Host "Lade $($asset.name) ($([math]::Round($asset.size / 1MB)) MB) …"
            Invoke-WebRequest $asset.browser_download_url -OutFile $zip
            Expand-Archive $zip -DestinationPath $temp
            $bin = Get-ChildItem $temp -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
            if (-not $bin) { throw 'ffmpeg.exe nicht im Archiv gefunden.' }
            New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
            Copy-Item $bin.FullName $ffDir
            $license = Get-ChildItem $temp -Recurse -Filter 'LICENSE*' | Select-Object -First 1
            if ($license) { Copy-Item $license.FullName (Join-Path $ffDir 'FFMPEG-LICENSE.txt') }
        }
        finally {
            Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Step 'FFmpeg prüfen'
    $ffmpeg = Join-Path $ffDir 'ffmpeg.exe'
    & $ffmpeg -hide_banner -version | Select-Object -First 1
    $filters = & $ffmpeg -hide_banner -filters 2>$null
    if (-not ($filters -match '\bddagrab\b')) { Write-Warning 'Dieses FFmpeg hat kein ddagrab, die Bildschirmaufnahme wird nicht funktionieren.' }
    $encoders = & $ffmpeg -hide_banner -encoders 2>$null
    foreach ($e in 'h264_nvenc', 'hevc_nvenc', 'av1_nvenc', 'h264_amf', 'hevc_amf', 'av1_amf', 'h264_qsv', 'hevc_qsv', 'av1_qsv',
                   'h264_d3d12va', 'hevc_d3d12va', 'av1_d3d12va', 'libx264', 'libx265', 'libsvtav1') {
        $state = if ($encoders -match "\b$e\b") { 'enthalten' } else { '-' }
        '{0,-14} {1}' -f $e, $state
    }
}

Step "Fertig: $Output\ClipRing.exe"
