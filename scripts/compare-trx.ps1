<#
.SYNOPSIS
    Compares all TRX files in the results directory and produces a unified cross-platform parity report.
.DESCRIPTION
    Auto-discovers every *-results.trx file in ResultsDir. The 'inmemory' file is the baseline;
    all others are emulator targets. Produces a single N-way comparison showing every test's
    outcome across all targets, plus per-target summary stats and a cross-platform divergence table.
.PARAMETER ResultsDir
    Directory containing TRX files. Default ./test-results.
.PARAMETER OutputFormat
    Output format: 'console' (default) or 'markdown' (for GitHub Step Summary).
.EXAMPLE
    .\scripts\compare-trx.ps1
    .\scripts\compare-trx.ps1 -ResultsDir ./test-results -OutputFormat markdown >> $env:GITHUB_STEP_SUMMARY
#>
param(
    [string]$ResultsDir = './test-results',
    [ValidateSet('console', 'markdown')]
    [string]$OutputFormat = 'console'
)

$ErrorActionPreference = 'Stop'

function Parse-TrxFile([string]$Path) {
    [xml]$xml = Get-Content $Path -Raw
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $results = @{}
    $xml | Select-Xml '//t:UnitTestResult' -Namespace $ns | ForEach-Object {
        $node = $_.Node
        $results[$node.testName] = @{
            Outcome      = $node.outcome
            ErrorMessage = $node.Output.ErrorInfo.Message
            StackTrace   = $node.Output.ErrorInfo.StackTrace
        }
    }
    return $results
}

function Truncate([string]$text, [int]$maxLength = 100) {
    if (-not $text) { return '' }
    $oneLine = ($text -split "`n")[0].Trim()
    if ($oneLine.Length -le $maxLength) { return $oneLine }
    return $oneLine.Substring(0, $maxLength) + '...'
}

# --- Discover all TRX files ---
$allTrxFiles = @(Get-ChildItem $ResultsDir -Filter '*-results.trx' -ErrorAction SilentlyContinue)
if ($allTrxFiles.Count -eq 0) { Write-Error "No *-results.trx files found in $ResultsDir"; exit 1 }

# Parse all files into a hashtable keyed by target name
$targets = [ordered]@{}
foreach ($f in $allTrxFiles | Sort-Object Name) {
    $name = $f.BaseName -replace '-results$', ''
    $targets[$name] = Parse-TrxFile $f.FullName
}

# Identify baseline (any target starting with 'inmemory') and emulator targets
$baselineName = $targets.Keys | Where-Object { $_ -like 'inmemory*' } | Select-Object -First 1
if (-not $baselineName) {
    Write-Error "No inmemory*-results.trx found in $ResultsDir (required as baseline)"
    exit 1
}
$baseline = $targets[$baselineName]
$emulatorNames = @($targets.Keys | Where-Object { $_ -ne $baselineName })

if ($emulatorNames.Count -eq 0) {
    Write-Error "No emulator TRX files found (only inmemory-results.trx present)"
    exit 1
}

# --- Build unified test matrix ---
$allTestNames = @()
foreach ($t in $targets.Values) { $allTestNames += $t.Keys }
$allTestNames = $allTestNames | Sort-Object -Unique

# Build per-test row: { Test, inmemory, emulator-go, gcp, ... }
$rows = @()
foreach ($test in $allTestNames) {
    $row = [ordered]@{ Test = $test }
    foreach ($name in $targets.Keys) {
        $row[$name] = if ($targets[$name].ContainsKey($test)) { $targets[$name][$test].Outcome } else { '-' }
    }
    $rows += [PSCustomObject]$row
}

# --- Classify each test ---
$fullParity = @()
$suspects = @()
$emulatorGaps = @()
$platformDiverge = @()
$skippedOnEmulators = @()
$other = @()

foreach ($row in $rows) {
    $im = $row.$baselineName
    $emOutcomes = @()
    foreach ($eName in $emulatorNames) { $emOutcomes += $row.$eName }

    $allSame = ($emOutcomes + $im | Sort-Object -Unique).Count -eq 1
    $anyEmFail = $emOutcomes | Where-Object { $_ -ne 'Passed' -and $_ -ne '-' }
    $anyEmPass = $emOutcomes | Where-Object { $_ -eq 'Passed' }
    $emulatorsDisagree = ($emOutcomes | Where-Object { $_ -ne '-' } | Sort-Object -Unique).Count -gt 1
    $allEmSkipped = @($emOutcomes | Where-Object { $_ -ne '-' }).Count -eq 0

    if ($allSame) {
        $fullParity += $row
    } elseif ($im -eq 'Passed' -and $anyEmFail) {
        $suspects += $row
    } elseif ($im -ne 'Passed' -and $im -ne '-' -and $anyEmPass) {
        $emulatorGaps += $row
    } elseif ($emulatorsDisagree) {
        $platformDiverge += $row
    } elseif ($allEmSkipped -and $im -ne '-') {
        $skippedOnEmulators += $row
    } else {
        $other += $row
    }
}

$totalTests = $allTestNames.Count
$applicableTests = $totalTests - $skippedOnEmulators.Count
$parityPct = if ($applicableTests -gt 0) { [math]::Round(($fullParity.Count / $applicableTests) * 100, 1) } else { 0 }

function Format-Outcome([string]$outcome) {
    switch ($outcome) {
        'Passed'      { 'Passed' }
        'Failed'      { 'FAILED' }
        'NotExecuted' { 'Skipped' }
        '-'           { '-' }
        default       { $outcome }
    }
}

# --- Output ---
if ($OutputFormat -eq 'markdown') {
    Write-Output "# Parity Report"
    Write-Output ""
    Write-Output "| Metric | Value |"
    Write-Output "|--------|-------|"
    Write-Output "| Total tests | $totalTests |"
    Write-Output "| Applicable (ran on emulator) | $applicableTests |"
    Write-Output "| Full parity | $($fullParity.Count) |"
    Write-Output "| **Parity %** | **${parityPct}%** |"
    Write-Output ""

    if ($suspects.Count -gt 0) {
        Write-Output "<details><summary>Suspects ($($suspects.Count) — inmemory passes, emulator fails)</summary>"
        Write-Output ""
        $header = "| Test |"
        $sep = "|------|"
        foreach ($eName in $emulatorNames) { $header += " $eName |"; $sep += "------|" }
        Write-Output $header
        Write-Output $sep
        foreach ($row in $suspects) {
            $line = "| $($row.Test) |"
            foreach ($eName in $emulatorNames) { $line += " $(Format-Outcome $row.$eName) |" }
            Write-Output $line
        }
        Write-Output ""
        Write-Output "</details>"
        Write-Output ""
    }

    if ($emulatorGaps.Count -gt 0) {
        Write-Output "<details><summary>Emulator Gaps ($($emulatorGaps.Count) — emulator passes, inmemory fails)</summary>"
        Write-Output ""
        $header = "| Test | inmemory |"
        $sep = "|------|----------|"
        foreach ($eName in $emulatorNames) { $header += " $eName |"; $sep += "------|" }
        Write-Output $header
        Write-Output $sep
        foreach ($row in $emulatorGaps) {
            $line = "| $($row.Test) | $(Format-Outcome $row.$baselineName) |"
            foreach ($eName in $emulatorNames) { $line += " $(Format-Outcome $row.$eName) |" }
            Write-Output $line
        }
        Write-Output ""
        Write-Output "</details>"
        Write-Output ""
    }

    Write-Output "| Category | Count |"
    Write-Output "|----------|-------|"
    Write-Output "| Full Parity | $($fullParity.Count) |"
    Write-Output "| Suspects | $($suspects.Count) |"
    Write-Output "| Emulator Gaps | $($emulatorGaps.Count) |"
    Write-Output "| Platform Divergence | $($platformDiverge.Count) |"
    Write-Output "| Skipped on Emulators | $($skippedOnEmulators.Count) |"
    Write-Output "| Other | $($other.Count) |"

} else {
    # Console output
    Write-Host "`n=== Parity Report ===" -ForegroundColor Cyan
    Write-Host "Total tests:     $totalTests"
    Write-Host "Applicable:      $applicableTests"
    Write-Host "Full parity:     $($fullParity.Count)" -ForegroundColor Green
    Write-Host "Parity:          ${parityPct}%" -ForegroundColor $(if ($parityPct -ge 95) { 'Green' } elseif ($parityPct -ge 80) { 'Yellow' } else { 'Red' })
    Write-Host ""

    if ($suspects.Count -gt 0) {
        Write-Host "SUSPECTS ($($suspects.Count) — inmemory passes, emulator fails):" -ForegroundColor Red
        foreach ($row in $suspects) {
            $outcomes = ($emulatorNames | ForEach-Object { "$_=$($row.$_)" }) -join ', '
            Write-Host "  $($row.Test): $outcomes" -ForegroundColor Red
        }
        Write-Host ""
    }

    if ($emulatorGaps.Count -gt 0) {
        Write-Host "EMULATOR GAPS ($($emulatorGaps.Count)):" -ForegroundColor Yellow
        foreach ($row in $emulatorGaps) {
            Write-Host "  $($row.Test): inmemory=$($row.$baselineName)" -ForegroundColor Yellow
        }
        Write-Host ""
    }

    Write-Host "Category breakdown:" -ForegroundColor Cyan
    Write-Host "  Full Parity:          $($fullParity.Count)"
    Write-Host "  Suspects:             $($suspects.Count)"
    Write-Host "  Emulator Gaps:        $($emulatorGaps.Count)"
    Write-Host "  Platform Divergence:  $($platformDiverge.Count)"
    Write-Host "  Skipped on Emulators: $($skippedOnEmulators.Count)"
    Write-Host "  Other:                $($other.Count)"
}
