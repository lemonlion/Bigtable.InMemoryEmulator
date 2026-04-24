<#
.SYNOPSIS
    Aggregate load test results from multiple benchmark runs into averaged markdown tables.
.PARAMETER ResultsDir
    Directory containing bench-* subdirectories with test-output.txt files.
.EXAMPLE
    .\scripts\Summarize-Benchmarks.ps1 -ResultsDir results
#>
param(
    [Parameter(Mandatory)]
    [string]$ResultsDir
)

$ErrorActionPreference = 'Stop'

$dirs = @(Get-ChildItem $ResultsDir -Directory -Filter 'bench-*' -ErrorAction SilentlyContinue)
if ($dirs.Count -eq 0) {
    Write-Output "No bench-* directories found in $ResultsDir"
    exit 0
}

Write-Output "# Benchmark Summary"
Write-Output ""
Write-Output "Runs found: $($dirs.Count)"
Write-Output ""

foreach ($dir in $dirs | Sort-Object Name) {
    $outputFile = Join-Path $dir.FullName 'test-output.txt'
    if (Test-Path $outputFile) {
        Write-Output "## $($dir.Name)"
        Write-Output ""
        Write-Output '```'
        Get-Content $outputFile | Select-Object -Last 30
        Write-Output '```'
        Write-Output ""
    }
}
