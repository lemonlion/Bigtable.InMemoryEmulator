<#
.SYNOPSIS
    Runs the test suite against a specified target backend.
.PARAMETER Target
    Backend to test against: inmemory, emulator-go, or gcp.
.PARAMETER Project
    Which test project(s) to run: unit, integration, or both. Default: both.
.PARAMETER Framework
    Target framework. Default net8.0.
.PARAMETER Filter
    Additional dotnet test filter expression.
.PARAMETER OutputDir
    Directory for TRX output files. Default ./test-results.
.EXAMPLE
    .\scripts\run-tests.ps1 -Target inmemory
    .\scripts\run-tests.ps1 -Target emulator-go -Project integration
    .\scripts\run-tests.ps1 -Target inmemory -Project unit -Filter "FullyQualifiedName~Crud"
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('inmemory', 'emulator-go', 'gcp')]
    [string]$Target,

    [ValidateSet('unit', 'integration', 'both')]
    [string]$Project = 'both',

    [string]$Framework = 'net8.0',
    [string]$Filter,
    [string]$OutputDir = './test-results'
)

$ErrorActionPreference = 'Stop'
$env:BIGTABLE_TEST_TARGET = $Target

# Set emulator host when targeting the Go emulator
if ($Target -eq 'emulator-go') {
    if (-not $env:BIGTABLE_EMULATOR_HOST) {
        $env:BIGTABLE_EMULATOR_HOST = 'localhost:8086'
    }
    $env:BIGTABLE_PROJECT = if ($env:BIGTABLE_PROJECT) { $env:BIGTABLE_PROJECT } else { 'fake-project' }
    $env:BIGTABLE_INSTANCE = if ($env:BIGTABLE_INSTANCE) { $env:BIGTABLE_INSTANCE } else { 'fake-instance' }
} elseif ($Target -eq 'inmemory') {
    Remove-Item Env:BIGTABLE_EMULATOR_HOST -ErrorAction SilentlyContinue
    $env:BIGTABLE_PROJECT = 'fake-project'
    $env:BIGTABLE_INSTANCE = 'fake-instance'
}
# For 'gcp', BIGTABLE_PROJECT and BIGTABLE_INSTANCE should be set externally

# Build filter: exclude InMemoryOnly tests when targeting emulator; exclude GcpOnly for Go emulator
$filterExpr = ''
if ($Target -eq 'emulator-go') {
    $filterExpr = 'Target!=InMemoryOnly&Target!=GcpOnly'
} elseif ($Target -eq 'gcp') {
    $filterExpr = 'Target!=InMemoryOnly'
}

if ($Filter) {
    if ($filterExpr -and $Filter -match '\|') {
        # Distribute the base filter across each OR segment to maintain correct precedence.
        $filterExpr = ($Filter -split '\|' | ForEach-Object { "$filterExpr&$_" }) -join '|'
    } elseif ($filterExpr) {
        $filterExpr = "$filterExpr&$Filter"
    } else {
        $filterExpr = $Filter
    }
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# Determine which projects to run
$projects = switch ($Project) {
    'unit'        { @(@{ Path = 'tests/Bigtable.InMemoryEmulator.Tests.Unit';        Label = 'unit' }) }
    'integration' { @(@{ Path = 'tests/Bigtable.InMemoryEmulator.Tests.Integration'; Label = 'integration' }) }
    'both'        { @(
        @{ Path = 'tests/Bigtable.InMemoryEmulator.Tests.Unit';        Label = 'unit' }
        @{ Path = 'tests/Bigtable.InMemoryEmulator.Tests.Integration'; Label = 'integration' }
    )}
}

$overallExit = 0
foreach ($proj in $projects) {
    $trxFile = "$Target-$($proj.Label)-results.trx"

    Write-Host "`nRunning $($proj.Label) tests against '$Target' (framework: $Framework)..." -ForegroundColor Cyan
    if ($filterExpr) { Write-Host "  Filter: $filterExpr" -ForegroundColor DarkGray }

    $testArgs = @(
        'test', $proj.Path,
        '--configuration', 'Release',
        '--framework', $Framework,
        '--no-build',
        '--logger', "trx;LogFileName=$trxFile",
        '--results-directory', $OutputDir
    )
    if ($filterExpr) {
        $testArgs += '--filter'
        $testArgs += $filterExpr
    }

    # Disable parallel test collections for emulator targets to avoid overwhelming the emulator
    if ($Target -ne 'inmemory') {
        $testArgs += '--'
        $testArgs += 'xunit.parallelizeTestCollections=false'
    }

    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { $overallExit = $LASTEXITCODE }

    Write-Host "Results: $OutputDir/$trxFile" -ForegroundColor Cyan
}

exit $overallExit
