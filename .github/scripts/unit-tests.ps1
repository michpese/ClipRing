# Runs the unit tests and reports the result (and every failed test) as GitHub annotations.
$output = & dotnet test tests/ClipRing.Tests -c Release --nologo 2>&1 | Tee-Object -Variable lines
$code = $LASTEXITCODE
$text = $lines | ForEach-Object { "$_" }
$summary = ($text | Where-Object { $_ -match '(Passed|Failed)!\s+-\s+Failed:' } | Select-Object -Last 1)
if (-not $summary) { $summary = 'keine Zusammenfassung (Build-Fehler?)' }
$failed = $text | Where-Object { $_ -match '^\s+Failed\s+\S' } | ForEach-Object { $_.Trim() } | Select-Object -First 15
$details = if ($failed) { ' | ' + ($failed -join ' | ') } else { '' }
$errors = $text | Where-Object { $_ -match ': error ' } | Select-Object -First 5
if ($errors) { $details += ' | ' + ($errors -join ' | ') }
if ($code -eq 0) { "::notice title=Unit-Tests::$($summary.Trim())" }
else { "::error title=Unit-Tests::$($summary.Trim())$details"; exit 1 }
