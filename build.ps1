<#
.SYNOPSIS
    Baut ClipRing als einzelne ClipRing.exe, FFmpeg ist darin eingebaut.

.DESCRIPTION
    FFmpeg (BtbN-Build, GPL) wird einmal nach .cache\ffmpeg geladen, komprimiert und als Ressource in
    ClipRing.exe eingebettet. ClipRing entpackt es beim ersten Start nach %LOCALAPPDATA%\ClipRing\ffmpeg
    und prüft es bei jedem Start per SHA-256.

.EXAMPLE
    .\build.ps1                    # Release, braucht installierte .NET 10 Desktop Runtime
    .\build.ps1 -SelfContained     # bringt die .NET-Runtime mit (größer, läuft überall)
    .\build.ps1 -UpdateFfmpeg      # neueste FFmpeg-Version laden statt der zwischengespeicherten
    .\build.ps1 -SkipFfmpeg        # ohne eingebautes FFmpeg (eigene ffmpeg.exe verwenden)
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SelfContained,
    [switch]$SkipFfmpeg,
    [switch]$UpdateFfmpeg,
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

# ------------------------------------------------------------------ 2. FFmpeg
$ffCache = Join-Path $PSScriptRoot '.cache\ffmpeg'
$ffExe = Join-Path $ffCache 'ffmpeg.exe'
$ffLicense = Join-Path $ffCache 'FFMPEG-LICENSE.txt'
$ffSourceInfo = Join-Path $ffCache 'source.json'
$ffVersion = $null
$embedArgs = @()

if ($SkipFfmpeg) {
    Step 'FFmpeg übersprungen (-SkipFfmpeg)'
    Write-Host "ClipRing sucht dann eine ffmpeg.exe neben dem Programm, im PATH oder unter dem Pfad aus 'Allgemein'."
}
else {
    if ($UpdateFfmpeg -and (Test-Path $ffCache)) { Remove-Item $ffCache -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ffCache | Out-Null

    # Earlier versions put FFmpeg next to the exe: reuse that copy instead of downloading again.
    $oldCopy = Join-Path $Output 'ffmpeg\ffmpeg.exe'
    if (-not (Test-Path $ffExe) -and -not $UpdateFfmpeg -and (Test-Path $oldCopy)) {
        Step 'Vorhandenes FFmpeg aus dist\ClipRing\ffmpeg übernehmen'
        Copy-Item $oldCopy $ffExe
        $oldLicense = Join-Path $Output 'ffmpeg\FFMPEG-LICENSE.txt'
        if (Test-Path $oldLicense) { Copy-Item $oldLicense $ffLicense }
    }

    if (Test-Path $ffExe) {
        Step 'FFmpeg ist bereits vorhanden (neu laden mit -UpdateFfmpeg)'
    }
    else {
        Step 'FFmpeg herunterladen (BtbN/FFmpeg-Builds, GPL, mit NVENC/AMF/QSV/D3D12/x264/x265/SVT-AV1)'
        $headers = @{ 'User-Agent' = 'ClipRing-build' }
        # In GitHub Actions the token avoids the anonymous API rate limit of shared runners.
        if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
        $release = Invoke-RestMethod 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest' -Headers $headers

        # Newest stable release branch, e.g. ffmpeg-n9.0-latest-win64-gpl-9.0.zip
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

        # Which BtbN build-script commit produced this binary: needed to point to the exact sources (GPL).
        $btbnCommit = $null
        try {
            $ref = Invoke-RestMethod "https://api.github.com/repos/BtbN/FFmpeg-Builds/git/ref/tags/$($release.tag_name)" -Headers $headers
            $btbnCommit = $ref.object.sha
            if ($ref.object.type -eq 'tag') {
                $btbnCommit = (Invoke-RestMethod $ref.object.url -Headers $headers).object.sha
            }
        }
        catch { Write-Warning "Commit des FFmpeg-Builds nicht ermittelbar: $_" }

        $temp = Join-Path ([IO.Path]::GetTempPath()) ("cliprings-ffmpeg-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $temp | Out-Null
        try {
            $zip = Join-Path $temp $asset.name
            Write-Host "Lade $($asset.name) ($([math]::Round($asset.size / 1MB)) MB) …"
            Invoke-WebRequest $asset.browser_download_url -OutFile $zip
            Expand-Archive $zip -DestinationPath $temp
            $bin = Get-ChildItem $temp -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
            if (-not $bin) { throw 'ffmpeg.exe nicht im Archiv gefunden.' }
            Copy-Item $bin.FullName $ffExe
            $license = Get-ChildItem $temp -Recurse -Filter 'LICENSE*' | Select-Object -First 1
            if ($license) { Copy-Item $license.FullName $ffLicense }
            @{ asset = $asset.name; btbnCommit = $btbnCommit; downloaded = (Get-Date).ToString('yyyy-MM-dd') } |
                ConvertTo-Json | Set-Content $ffSourceInfo -Encoding UTF8
        }
        finally {
            Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Step 'FFmpeg prüfen'
    $versionLine = & $ffExe -hide_banner -version | Select-Object -First 1
    Write-Host $versionLine
    $ffVersion = if ($versionLine -match 'version\s+(\S+)') { $Matches[1] } else { 'unbekannt' }
    $filters = & $ffExe -hide_banner -filters 2>$null
    foreach ($f in 'gfxcapture', 'ddagrab') {
        $state = if ($filters -match "\b$f\b") { 'enthalten' } else { '-' }
        '{0,-14} {1}' -f $f, $state
    }
    if (-not ($filters -match '\b(gfxcapture|ddagrab)\b')) {
        Write-Warning 'Dieses FFmpeg kann den Bildschirm nicht aufnehmen (weder gfxcapture noch ddagrab).'
    }
    $encoders = & $ffExe -hide_banner -encoders 2>$null
    foreach ($e in 'h264_nvenc', 'hevc_nvenc', 'av1_nvenc', 'h264_amf', 'hevc_amf', 'av1_amf', 'h264_qsv', 'hevc_qsv', 'av1_qsv',
                   'h264_d3d12va', 'hevc_d3d12va', 'av1_d3d12va', 'libx264', 'libx265', 'libsvtav1') {
        $state = if ($encoders -match "\b$e\b") { 'enthalten' } else { '-' }
        '{0,-14} {1}' -f $e, $state
    }

    Step 'FFmpeg für die Einbettung komprimieren'
    $ffGz = Join-Path $ffCache 'ffmpeg.exe.gz'
    if (-not (Test-Path $ffGz) -or (Get-Item $ffGz).LastWriteTimeUtc -lt (Get-Item $ffExe).LastWriteTimeUtc) {
        $in = [IO.File]::OpenRead($ffExe)
        try {
            $out = [IO.File]::Create($ffGz)
            try {
                $gzip = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
                try { $in.CopyTo($gzip) } finally { $gzip.Dispose() }
            }
            finally { $out.Dispose() }
        }
        finally { $in.Dispose() }
    }
    $ffHash = (Get-FileHash $ffExe -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("{0:N0} MB -> {1:N0} MB, SHA-256 {2}" -f ((Get-Item $ffExe).Length / 1MB), ((Get-Item $ffGz).Length / 1MB), $ffHash)

    $embedArgs = @("-p:EmbedFfmpeg=$ffGz", "-p:FfmpegSha256=$ffHash", "-p:FfmpegVersion=$ffVersion")
}

# ------------------------------------------------------------------ 3. Build
Step "ClipRing bauen ($Configuration, self-contained: $SelfContained, FFmpeg eingebaut: $(-not $SkipFfmpeg))"
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
) + $embedArgs
if ($env:GITHUB_ACTIONS -eq 'true') {
    # Classic one-line diagnostics with full paths, so the workflow can turn them into annotations.
    $publishArgs += @('-tl:off', '-p:GenerateFullPaths=true', '-clp:NoSummary')
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen (Code $LASTEXITCODE)" }

if (-not $SkipFfmpeg) {
    # FFmpeg now lives inside ClipRing.exe: drop the separate copy of earlier versions, keep the license.
    $oldDir = Join-Path $Output 'ffmpeg'
    if (Test-Path $oldDir) { Remove-Item $oldDir -Recurse -Force }
    if (Test-Path $ffLicense) { Copy-Item $ffLicense (Join-Path $Output 'FFMPEG-LICENSE.txt') -Force }

    # Where the exact FFmpeg sources are (GPL): FFmpeg commit from the version string, BtbN build scripts
    # (they pin the versions of all libraries in the build).
    $source = if (Test-Path $ffSourceInfo) { Get-Content $ffSourceInfo -Raw | ConvertFrom-Json } else { $null }
    $commit = if ($ffVersion -match '-g([0-9a-f]{7,40})') { $Matches[1] } else { $null }
    $lines = @(
        'ClipRing includes FFmpeg, unchanged, as a separate program under the GNU GPL v3.',
        'ClipRing enthält FFmpeg unverändert als eigenes Programm unter der GNU GPL v3.',
        '',
        "FFmpeg version: $ffVersion"
    )
    if ($commit) { $lines += "FFmpeg source code: https://github.com/FFmpeg/FFmpeg/tree/$commit" }
    else { $lines += 'FFmpeg source code: https://ffmpeg.org/download.html' }
    if ($source -and $source.btbnCommit) {
        $lines += "Build scripts with the exact versions of all included libraries: https://github.com/BtbN/FFmpeg-Builds/tree/$($source.btbnCommit)"
    }
    else { $lines += 'Build scripts with the versions of all included libraries: https://github.com/BtbN/FFmpeg-Builds' }
    if ($source -and $source.asset) { $lines += "Binary: $($source.asset) (BtbN/FFmpeg-Builds)" }
    $lines += 'License: FFMPEG-LICENSE.txt'
    $lines | Set-Content (Join-Path $Output 'FFMPEG-SOURCE.txt') -Encoding UTF8
}

$size = (Get-Item (Join-Path $Output 'ClipRing.exe')).Length / 1MB
Step ("Fertig: $Output\ClipRing.exe ({0:N0} MB)" -f $size)
