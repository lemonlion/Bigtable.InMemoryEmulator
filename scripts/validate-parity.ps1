<#
.SYNOPSIS
    One-command end-to-end parity validation: build, run in-memory tests, start Go emulator,
    run emulator tests, compare results.
.PARAMETER Filter
    Optional dotnet test filter expression.
.PARAMETER SkipBuild
    Skip the build step (assumes already built).
.PARAMETER SkipEmulatorStop
    Don't stop the emulator container afterwards.
.EXAMPLE
    .\scripts\validate-parity.ps1
    .\scripts\validate-parity.ps1 -Filter "FullyQualifiedName~Crud"
    .\scripts\validate-parity.ps1 -SkipBuild -SkipEmulatorStop
#>
param(
    [string]$Filter,
    [switch]$SkipBuild,
    [switch]$SkipEmulatorStop
)

$ErrorActionPreference = 'Stop'
$resultsDir = './test-results'

# 1. Clean previous results
if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

# 2. Build
if (-not $SkipBuild) {
    Write-Host "`n=== Building ===" -ForegroundColor Cyan
    dotnet build InMemoryEmulator.Bigtable.sln --configuration Release
    if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }
}

# 3. Run in-memory baseline
Write-Host "`n=== Running in-memory tests ===" -ForegroundColor Cyan
$filterArg = if ($Filter) { @('-Filter', $Filter) } else { @() }
& "$PSScriptRoot/run-tests.ps1" -Target inmemory -Project integration @filterArg -OutputDir $resultsDir
$inmemoryExit = $LASTEXITCODE

# 4. Start Go emulator if not already running
Write-Host "`n=== Starting Go Bigtable Emulator ===" -ForegroundColor Cyan
& "$PSScriptRoot/start-emulator.ps1"
if ($LASTEXITCODE -ne 0) { Write-Error "Emulator failed to start"; exit 1 }

# 5. Run emulator tests
Write-Host "`n=== Running Go emulator tests ===" -ForegroundColor Cyan
& "$PSScriptRoot/run-tests.ps1" -Target emulator-go -Project integration @filterArg -OutputDir $resultsDir
$emulatorExit = $LASTEXITCODE

# 6. Compare
Write-Host "`n=== Generating Parity Report ===" -ForegroundColor Cyan
& "$PSScriptRoot/compare-trx.ps1" -ResultsDir $resultsDir

# 7. Stop emulator
if (-not $SkipEmulatorStop) {
    Write-Host "`n=== Stopping Go Bigtable Emulator ===" -ForegroundColor Cyan
    docker stop bigtable-emulator 2>$null
    docker rm bigtable-emulator 2>$null
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
Write-Host "In-memory exit code: $inmemoryExit"
Write-Host "Emulator exit code:  $emulatorExit"

if ($inmemoryExit -ne 0 -or $emulatorExit -ne 0) { exit 1 }
