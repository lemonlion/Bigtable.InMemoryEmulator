<#
.SYNOPSIS
    Starts the Google Cloud Bigtable Go emulator in Docker and waits for gRPC readiness.
.PARAMETER ContainerName
    Docker container name. Default: bigtable-emulator.
.PARAMETER Port
    Host port mapping. Default: 8086.
.PARAMETER TimeoutSeconds
    Maximum seconds to wait for readiness. Default: 120.
.EXAMPLE
    .\scripts\start-emulator.ps1
    .\scripts\start-emulator.ps1 -Port 8087
#>
param(
    [string]$ContainerName = 'bigtable-emulator',
    [int]$Port = 8086,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

# Check if already running
$existing = docker ps -q --filter "name=$ContainerName" 2>$null
if ($existing) {
    Write-Host "Container '$ContainerName' is already running." -ForegroundColor Yellow
    exit 0
}

# Remove stopped container if present
docker rm $ContainerName 2>$null | Out-Null

Write-Host "Starting Bigtable Go emulator on port $Port..." -ForegroundColor Cyan

docker run -d `
    -p "${Port}:${Port}" `
    --name $ContainerName `
    google/cloud-sdk:latest `
    gcloud beta emulators bigtable start --host-port=0.0.0.0:$Port

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to start emulator container"
    exit 1
}

# Wait for gRPC readiness by polling the port
$elapsed = 0
while ($elapsed -lt $TimeoutSeconds) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect('localhost', $Port)
        $tcp.Close()
        Write-Host "Emulator ready after ${elapsed}s" -ForegroundColor Green
        exit 0
    } catch {
        Start-Sleep -Seconds 5
        $elapsed += 5
        Write-Host "Waiting for emulator... (${elapsed}s)" -ForegroundColor DarkGray
    }
}

Write-Error "Emulator did not start within ${TimeoutSeconds}s"
docker logs $ContainerName
exit 1
