# Runs one of ClipRing's built-in self-tests and reports the result as a GitHub annotation.
#   -Mode ui      every page in German and English, missing texts, binding errors
#   -Mode ffmpeg  extracting the built-in FFmpeg, checksum, start, capture filters
param(
    [Parameter(Mandatory)] [ValidateSet('ui', 'ffmpeg')] [string] $Mode,
    [string] $Exe = 'dist/ClipRing/ClipRing.exe'
)

$title = if ($Mode -eq 'ui') { 'Oberfläche' } else { 'Eingebautes FFmpeg' }
$report = Join-Path $env:RUNNER_TEMP "selftest-$Mode.txt"
Remove-Item $report -ErrorAction SilentlyContinue
$p = Start-Process $Exe -ArgumentList "--selftest-$Mode", "`"$report`"" -Wait -PassThru
$text = if (Test-Path $report) { Get-Content $report -Raw } else { '(kein Bericht)' }
Write-Host $text
$summary = (($text -split "`r?`n") | Where-Object { $_ }) -join ' | '
if ($summary.Length -gt 3500) { $summary = $summary.Substring(0, 3500) + ' …' }
if ($p.ExitCode -eq 0) { "::notice title=${title}::$summary" }
else { "::error title=${title}::Exit $($p.ExitCode): $summary"; exit 1 }
