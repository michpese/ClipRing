<#
.SYNOPSIS
    Turns MSBuild/compiler diagnostics from a build log into GitHub annotations.

.DESCRIPTION
    Annotations show up on the run page and in the check-runs API, so build errors can be
    read without downloading the raw log. GitHub keeps at most 10 errors and 10 warnings
    per step, so errors come first and a notice summarizes the totals.
#>
param(
    [Parameter(Mandatory)] [string]$LogFile
)

if (-not (Test-Path $LogFile)) {
    Write-Output "::warning title=Build-Log fehlt::$LogFile wurde nicht erzeugt."
    exit 0
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pattern = '^\s*(?<file>[A-Za-z]:\\[^(]+|[^\s(][^(]*)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<level>error|warning)\s+(?<code>[A-Z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$'
$plain   = '^\s*(?<origin>[^:]+?)\s*:\s*(?<level>error|warning)\s+(?<code>[A-Z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$'

$seen = [System.Collections.Generic.HashSet[string]]::new()
$items = [System.Collections.Generic.List[object]]::new()

foreach ($line in Get-Content $LogFile) {
    $m = [regex]::Match($line, $pattern)
    if ($m.Success) {
        $file = $m.Groups['file'].Value.Trim()
        if ($file.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            $file = $file.Substring($root.Length).TrimStart('\', '/')
        }
        $file = $file -replace '\\', '/'
        $item = [pscustomobject]@{
            Level = $m.Groups['level'].Value; Code = $m.Groups['code'].Value; File = $file
            Line = $m.Groups['line'].Value; Col = $m.Groups['col'].Value; Msg = $m.Groups['msg'].Value
        }
    }
    else {
        $m = [regex]::Match($line, $plain)
        if (-not $m.Success) { continue }
        $item = [pscustomobject]@{
            Level = $m.Groups['level'].Value; Code = $m.Groups['code'].Value; File = ''
            Line = ''; Col = ''; Msg = "$($m.Groups['origin'].Value.Trim()): $($m.Groups['msg'].Value)"
        }
    }
    # The WPF markup pass compiles a temporary project, so every diagnostic can appear twice.
    $key = "$($item.Level)|$($item.Code)|$($item.File)|$($item.Line)|$($item.Col)|$($item.Msg)"
    if ($seen.Add($key)) { $items.Add($item) }
}

function Escape([string]$s) { $s -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A' }

$errors = @($items | Where-Object Level -eq 'error')
$warnings = @($items | Where-Object Level -eq 'warning')

foreach ($d in @($errors) + @($warnings)) {
    $props = "title=$($d.Code)"
    if ($d.File) { $props = "file=$($d.File),line=$($d.Line),col=$($d.Col),$props" }
    Write-Output "::$($d.Level) $props::$(Escape $d.Msg)"
}

$byCode = ($warnings | Group-Object Code | Sort-Object Count -Descending | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '
Write-Output "::notice title=Compiler-Meldungen::$($errors.Count) Fehler, $($warnings.Count) Warnungen$(if ($byCode) { " ($byCode)" })"
