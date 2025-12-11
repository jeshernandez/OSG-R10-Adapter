#!/usr/bin/env pwsh
# Build script for OSG-R10-Adapter
# Builds self-contained executable for Windows

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = 'win-x64',

    [Parameter(Mandatory=$false)]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [Parameter(Mandatory=$false)]
    [switch]$SelfContained = $true,

    [Parameter(Mandatory=$false)]
    [switch]$SingleFile = $true
)

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "OSG-R10-Adapter Build Script" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host "Runtime: $Runtime" -ForegroundColor Yellow
Write-Host "Self-Contained: $SelfContained" -ForegroundColor Yellow
Write-Host "Single File: $SingleFile" -ForegroundColor Yellow
Write-Host ""

# Get project details
$projectFile = "gspro-r10.csproj"
$outputDir = "bin/$Configuration/publish/$Runtime"

# Clean previous build
Write-Host "Cleaning previous build..." -ForegroundColor Green
if (Test-Path $outputDir) {
    Remove-Item -Path $outputDir -Recurse -Force
}

# Build arguments
$buildArgs = @(
    'publish',
    $projectFile,
    '-c', $Configuration,
    '-r', $Runtime
)

if ($SelfContained) {
    $buildArgs += '--self-contained', 'true'
} else {
    $buildArgs += '--self-contained', 'false'
}

if ($SingleFile) {
    $buildArgs += '-p:PublishSingleFile=true'
    $buildArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
}

# Additional optimizations for Release builds
if ($Configuration -eq 'Release') {
    $buildArgs += '-p:PublishTrimmed=false'  # Disable trimming to avoid runtime issues
    $buildArgs += '-p:DebugType=none'
    $buildArgs += '-p:DebugSymbols=false'
}

# Execute build
Write-Host "Building project..." -ForegroundColor Green
Write-Host "Command: dotnet $($buildArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ""

& dotnet $buildArgs

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "======================================" -ForegroundColor Green
    Write-Host "Build completed successfully!" -ForegroundColor Green
    Write-Host "======================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Output location: $outputDir" -ForegroundColor Yellow
    Write-Host ""

    # List output files
    if (Test-Path $outputDir) {
        Write-Host "Output files:" -ForegroundColor Yellow
        Get-ChildItem -Path $outputDir -File | ForEach-Object {
            $size = "{0:N2} MB" -f ($_.Length / 1MB)
            Write-Host "  - $($_.Name) ($size)" -ForegroundColor White
        }
    }
} else {
    Write-Host ""
    Write-Host "======================================" -ForegroundColor Red
    Write-Host "Build failed!" -ForegroundColor Red
    Write-Host "======================================" -ForegroundColor Red
    exit 1
}
