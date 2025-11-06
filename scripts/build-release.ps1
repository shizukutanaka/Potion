# Potion Service - Release Build Script
# Creates production-ready distribution packages

param(
    [ValidateSet("Community", "Enterprise", "Both")]
    [string]$Edition = "Both",
    [string]$Version = "1.0.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

Write-Step "Building Potion Service Release v$Version"

# Clean previous builds
Write-Step "Cleaning previous builds"
Remove-Item -Path ".\publish" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ".\dist" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path ".\dist" -Force | Out-Null

# Restore dependencies
Write-Step "Restoring dependencies"
dotnet restore Potion.sln

# Run tests
if (-not $SkipTests) {
    Write-Step "Running tests"
    dotnet test tests/Potion.Service.Tests/Potion.Service.Tests.csproj --configuration Release --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed"
        exit 1
    }
    Write-Success "All tests passed"
}

# Build Community Edition
if ($Edition -in @("Community", "Both")) {
    Write-Step "Building Community Edition"

    dotnet publish src/Potion.Service/Potion.Service.csproj `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=false `
        -p:Edition=Community `
        -p:Version=$Version `
        -o ".\publish\community"

    if ($LASTEXITCODE -eq 0) {
        Write-Success "Community Edition built successfully"

        # Create distribution package
        Write-Step "Creating Community Edition package"
        $communityDist = ".\dist\PotionService-Community-v$Version-win-x64"
        New-Item -ItemType Directory -Path $communityDist -Force | Out-Null

        Copy-Item -Path ".\publish\community\*" -Destination $communityDist -Recurse -Force
        Copy-Item -Path ".\README.md" -Destination $communityDist -Force
        Copy-Item -Path ".\LICENSE" -Destination $communityDist -Force
        Copy-Item -Path ".\EULA.md" -Destination $communityDist -Force
        Copy-Item -Path ".\PRIVACY_POLICY.md" -Destination $communityDist -Force
        Copy-Item -Path ".\QUICK_START.md" -Destination $communityDist -Force
        Copy-Item -Path ".\scripts\package-installer.ps1" -Destination $communityDist -Force

        # Create ZIP
        Compress-Archive -Path "$communityDist\*" -DestinationPath "$communityDist.zip" -Force
        Write-Success "Community Edition package: $communityDist.zip"
    }
}

# Build Enterprise Edition
if ($Edition -in @("Enterprise", "Both")) {
    Write-Step "Building Enterprise Edition"

    dotnet publish src/Potion.Service/Potion.Service.csproj `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=false `
        -p:Edition=Enterprise `
        -p:Version=$Version `
        -o ".\publish\enterprise"

    if ($LASTEXITCODE -eq 0) {
        Write-Success "Enterprise Edition built successfully"

        # Create distribution package
        Write-Step "Creating Enterprise Edition package"
        $enterpriseDist = ".\dist\PotionService-Enterprise-v$Version-win-x64"
        New-Item -ItemType Directory -Path $enterpriseDist -Force | Out-Null

        Copy-Item -Path ".\publish\enterprise\*" -Destination $enterpriseDist -Recurse -Force
        Copy-Item -Path ".\README.md" -Destination $enterpriseDist -Force
        Copy-Item -Path ".\LICENSE" -Destination $enterpriseDist -Force
        Copy-Item -Path ".\EULA.md" -Destination $enterpriseDist -Force
        Copy-Item -Path ".\PRIVACY_POLICY.md" -Destination $enterpriseDist -Force
        Copy-Item -Path ".\README_ENTERPRISE.md" -Destination $enterpriseDist -Force
        Copy-Item -Path ".\DEPLOYMENT.md" -Destination $enterpriseDist -Force
        Copy-Item -Path ".\scripts\package-installer.ps1" -Destination $enterpriseDist -Force
        Copy-Item -Path ".\kubernetes-enterprise.yml" -Destination "$enterpriseDist\deploy" -Force
        Copy-Item -Path ".\docker-compose.enterprise.yml" -Destination "$enterpriseDist\deploy" -Force

        # Create ZIP
        Compress-Archive -Path "$enterpriseDist\*" -DestinationPath "$enterpriseDist.zip" -Force
        Write-Success "Enterprise Edition package: $enterpriseDist.zip"
    }
}

# Generate checksums
Write-Step "Generating checksums"
Get-ChildItem -Path ".\dist\*.zip" | ForEach-Object {
    $hash = Get-FileHash -Path $_.FullName -Algorithm SHA256
    "$($hash.Hash)  $($_.Name)" | Out-File -FilePath "$($_.FullName).sha256" -Encoding UTF8
    Write-Success "SHA256: $($_.Name).sha256"
}

# Create release notes
Write-Step "Creating release notes"
$releaseNotes = @"
# Potion Self-Healing Service v$Version

## Release Date
$(Get-Date -Format "yyyy-MM-dd")

## Editions
- Community Edition: Free and open-source
- Enterprise Edition: Advanced features with commercial support

## What's New
- Multi-language support (50+ languages)
- Enhanced security features
- Performance optimizations
- Comprehensive monitoring and observability
- Professional installer and deployment tools

## System Requirements
- OS: Windows 10 21H2+ / Windows Server 2019/2022
- .NET Runtime: .NET 8.0
- Memory: 4 GB RAM minimum (8 GB recommended)
- Disk: 10 GB free space
- Permissions: Administrator rights

## Installation

### Quick Install
``````powershell
# Extract the package
Expand-Archive -Path PotionService-Community-v$Version-win-x64.zip -DestinationPath C:\Potion

# Run installer with administrator privileges
cd C:\Potion
.\package-installer.ps1 -Edition Community
``````

### Verify Installation
``````powershell
Get-Service "Otedama Self-Healing Service"
``````

## Upgrade Instructions
1. Stop the existing service
2. Backup configuration files
3. Run the new installer
4. Restore custom configurations
5. Start the service

## Documentation
- Quick Start Guide: QUICK_START.md
- Deployment Guide: DEPLOYMENT.md
- Security Guide: SECURITY.md
- API Documentation: https://api-docs.potion-service.com

## Support
- Community: https://github.com/your-org/potion-service/discussions
- Enterprise: support@potion-service.com
- Documentation: https://docs.potion-service.com

## Checksums
Verify package integrity using SHA256 checksums provided in .sha256 files.

## License
- Community Edition: MIT License
- Enterprise Edition: Commercial License (see EULA.md)

---
**Copyright © 2024 Potion Self-Healing Service. All rights reserved.**
"@

$releaseNotes | Out-File -FilePath ".\dist\RELEASE_NOTES.md" -Encoding UTF8
Write-Success "Release notes created"

Write-Step "Build Complete!"
Write-Host "`nDistribution packages:" -ForegroundColor Green
Get-ChildItem -Path ".\dist\*.zip" | ForEach-Object {
    $size = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  📦 $($_.Name) ($size MB)" -ForegroundColor White
}
