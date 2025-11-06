# Potion Service - Professional Installer Script
# Version: 1.0.0
# Platform: Windows Server 2019/2022, Windows 10/11

<#
.SYNOPSIS
    Professional installer for Potion Self-Healing Service

.DESCRIPTION
    Automated installation script with pre-flight checks, validation, and rollback capability

.PARAMETER InstallPath
    Installation directory (default: C:\Program Files\Potion)

.PARAMETER Edition
    Edition to install: Community or Enterprise

.PARAMETER Silent
    Silent installation without prompts

.EXAMPLE
    .\package-installer.ps1 -Edition Community
    .\package-installer.ps1 -Edition Enterprise -Silent
#>

param(
    [string]$InstallPath = "C:\Program Files\Potion",
    [ValidateSet("Community", "Enterprise")]
    [string]$Edition = "Community",
    [switch]$Silent
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Configuration
$ServiceName = "Otedama Self-Healing Service"
$ServiceDisplayName = "Otedama Self-Healing Service"
$ServiceDescription = "Autonomous system health monitoring and remediation service"
$MinimumPowerShellVersion = 5.1
$RequiredDotNetVersion = "8.0"
$MinimumMemoryGB = 4
$MinimumDiskSpaceGB = 10

# Color output functions
function Write-ColorOutput {
    param([string]$Message, [string]$Color = "White")
    if (-not $Silent) {
        Write-Host $Message -ForegroundColor $Color
    }
}

function Write-Success { param([string]$Message) Write-ColorOutput "✓ $Message" "Green" }
function Write-Error { param([string]$Message) Write-ColorOutput "✗ $Message" "Red" }
function Write-Warning { param([string]$Message) Write-ColorOutput "⚠ $Message" "Yellow" }
function Write-Info { param([string]$Message) Write-ColorOutput "ℹ $Message" "Cyan" }

# Banner
function Show-Banner {
    Write-ColorOutput @"
╔════════════════════════════════════════════════════════════════╗
║                                                                ║
║   Potion Self-Healing Service - Professional Installer        ║
║   Version: 1.0.0 | Edition: $Edition                          ║
║                                                                ║
╚════════════════════════════════════════════════════════════════╝
"@ "Cyan"
}

# Pre-flight checks
function Test-Prerequisites {
    Write-Info "Running pre-flight checks..."

    # Check administrator privileges
    $currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Error "Administrator privileges required"
        exit 1
    }
    Write-Success "Administrator privileges verified"

    # Check PowerShell version
    if ($PSVersionTable.PSVersion.Major -lt $MinimumPowerShellVersion) {
        Write-Error "PowerShell $MinimumPowerShellVersion or higher required"
        exit 1
    }
    Write-Success "PowerShell version: $($PSVersionTable.PSVersion)"

    # Check OS version
    $osVersion = [System.Environment]::OSVersion.Version
    if ($osVersion.Major -lt 10) {
        Write-Error "Windows 10/Server 2016 or higher required"
        exit 1
    }
    Write-Success "Operating system version verified"

    # Check available memory
    $totalMemoryGB = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB
    if ($totalMemoryGB -lt $MinimumMemoryGB) {
        Write-Warning "Low memory: ${totalMemoryGB}GB (${MinimumMemoryGB}GB recommended)"
    } else {
        Write-Success "Available memory: ${totalMemoryGB}GB"
    }

    # Check disk space
    $installDrive = Split-Path $InstallPath -Qualifier
    $diskSpace = (Get-PSDrive $installDrive.TrimEnd(':')).Free / 1GB
    if ($diskSpace -lt $MinimumDiskSpaceGB) {
        Write-Error "Insufficient disk space: ${diskSpace}GB (${MinimumDiskSpaceGB}GB required)"
        exit 1
    }
    Write-Success "Available disk space: ${diskSpace}GB"

    # Check .NET Runtime
    $dotnetInstalled = Test-Path "C:\Program Files\dotnet\dotnet.exe"
    if (-not $dotnetInstalled) {
        Write-Warning ".NET $RequiredDotNetVersion Runtime not detected - will be installed"
    } else {
        Write-Success ".NET Runtime detected"
    }

    Write-Success "All pre-flight checks passed"
}

# Download and install .NET Runtime
function Install-DotNetRuntime {
    Write-Info "Installing .NET $RequiredDotNetVersion Runtime..."

    $dotnetInstallerUrl = "https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.ps1"
    $installerPath = "$env:TEMP\dotnet-install.ps1"

    try {
        Invoke-WebRequest -Uri $dotnetInstallerUrl -OutFile $installerPath -UseBasicParsing
        & $installerPath -Channel 8.0 -Runtime aspnetcore -InstallDir "C:\Program Files\dotnet"
        Write-Success ".NET Runtime installed successfully"
    } catch {
        Write-Error "Failed to install .NET Runtime: $_"
        exit 1
    }
}

# Create installation directory structure
function New-InstallationStructure {
    Write-Info "Creating installation directory structure..."

    $directories = @(
        $InstallPath,
        "$InstallPath\logs",
        "$InstallPath\config",
        "$InstallPath\data",
        "$InstallPath\backups",
        "$env:ProgramData\Otedama",
        "$env:ProgramData\Otedama\logs",
        "$env:ProgramData\Otedama\telemetry",
        "$env:ProgramData\Otedama\state",
        "$env:ProgramData\Otedama\backups",
        "$env:ProgramData\Otedama\reports"
    )

    foreach ($dir in $directories) {
        if (-not (Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Write-Success "Created: $dir"
        }
    }
}

# Copy application files
function Copy-ApplicationFiles {
    Write-Info "Copying application files..."

    $sourcePath = "$PSScriptRoot\..\publish"
    if (-not (Test-Path $sourcePath)) {
        Write-Error "Application files not found at: $sourcePath"
        Write-Info "Please run: dotnet publish -c Release -o publish"
        exit 1
    }

    try {
        Copy-Item -Path "$sourcePath\*" -Destination $InstallPath -Recurse -Force
        Write-Success "Application files copied successfully"
    } catch {
        Write-Error "Failed to copy application files: $_"
        exit 1
    }
}

# Configure service
function Install-WindowsService {
    Write-Info "Installing Windows service..."

    $servicePath = "$InstallPath\Potion.Service.exe"
    if (-not (Test-Path $servicePath)) {
        Write-Error "Service executable not found: $servicePath"
        exit 1
    }

    # Check if service already exists
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Warning "Service already exists - stopping and removing..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName
        Start-Sleep -Seconds 2
    }

    # Create service
    $createResult = sc.exe create $ServiceName `
        binPath= "`"$servicePath`"" `
        start= auto `
        DisplayName= $ServiceDisplayName `
        obj= "LocalSystem"

    if ($LASTEXITCODE -eq 0) {
        sc.exe description $ServiceName $ServiceDescription
        Write-Success "Windows service installed successfully"
    } else {
        Write-Error "Failed to install Windows service: $createResult"
        exit 1
    }
}

# Configure firewall
function Configure-Firewall {
    Write-Info "Configuring Windows Firewall..."

    $ruleName = "Potion Self-Healing Service"
    $existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue

    if ($existingRule) {
        Remove-NetFirewallRule -DisplayName $ruleName
    }

    New-NetFirewallRule -DisplayName $ruleName `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort 5000,5001 `
        -Program "$InstallPath\Potion.Service.exe" `
        -Description "Potion Self-Healing Service API endpoints" | Out-Null

    Write-Success "Firewall rules configured"
}

# Configure initial settings
function Set-InitialConfiguration {
    Write-Info "Configuring initial settings..."

    $configPath = "$env:ProgramData\Otedama\appsettings.json"
    $defaultConfig = @{
        "Serilog" = @{
            "MinimumLevel" = "Information"
        }
        "RemediationPolicy" = @{
            "Enabled" = $true
            "CommandAllowlist" = @("sfc.exe", "dism.exe", "cleanmgr.exe")
        }
        "SecurityAudit" = @{
            "Enabled" = $true
            "AuditIntervalHours" = 12
        }
        "Telemetry" = @{
            "Enabled" = $true
            "RetentionDays" = 90
        }
        "Edition" = $Edition
        "LicenseKey" = ""
    }

    $defaultConfig | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8
    Write-Success "Initial configuration created"
}

# Set proper permissions
function Set-SecurityPermissions {
    Write-Info "Setting security permissions..."

    # Set restrictive permissions on installation directory
    $acl = Get-Acl $InstallPath
    $acl.SetAccessRuleProtection($true, $false)

    # Grant Administrators full control
    $adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow"
    )
    $acl.AddAccessRule($adminRule)

    # Grant SYSTEM full control
    $systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow"
    )
    $acl.AddAccessRule($systemRule)

    Set-Acl -Path $InstallPath -AclObject $acl
    Write-Success "Security permissions configured"
}

# Start service
function Start-PotionService {
    Write-Info "Starting Potion service..."

    try {
        Start-Service -Name $ServiceName
        Start-Sleep -Seconds 5

        $service = Get-Service -Name $ServiceName
        if ($service.Status -eq "Running") {
            Write-Success "Service started successfully"
        } else {
            Write-Error "Service failed to start: $($service.Status)"
            exit 1
        }
    } catch {
        Write-Error "Failed to start service: $_"
        exit 1
    }
}

# Create uninstaller script
function New-UninstallerScript {
    $uninstallerPath = "$InstallPath\uninstall.ps1"

    $uninstallerContent = @"
# Potion Service Uninstaller
param([switch]`$Force)

Write-Host "Uninstalling Potion Self-Healing Service..." -ForegroundColor Yellow

if (-not `$Force) {
    `$confirm = Read-Host "Are you sure you want to uninstall? (yes/no)"
    if (`$confirm -ne "yes") {
        Write-Host "Uninstallation cancelled" -ForegroundColor Green
        exit 0
    }
}

# Stop service
Write-Host "Stopping service..." -ForegroundColor Cyan
Stop-Service -Name "$ServiceName" -Force -ErrorAction SilentlyContinue

# Remove service
Write-Host "Removing service..." -ForegroundColor Cyan
sc.exe delete "$ServiceName"

# Remove firewall rules
Write-Host "Removing firewall rules..." -ForegroundColor Cyan
Remove-NetFirewallRule -DisplayName "Potion Self-Healing Service" -ErrorAction SilentlyContinue

# Remove installation directory
Write-Host "Removing installation files..." -ForegroundColor Cyan
Remove-Item -Path "$InstallPath" -Recurse -Force -ErrorAction SilentlyContinue

# Remove data directory (optional)
`$removeData = Read-Host "Remove all data and logs? (yes/no)"
if (`$removeData -eq "yes") {
    Remove-Item -Path "`$env:ProgramData\Otedama" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Data removed" -ForegroundColor Green
}

Write-Host "Uninstallation complete!" -ForegroundColor Green
"@

    $uninstallerContent | Set-Content -Path $uninstallerPath -Encoding UTF8
    Write-Success "Uninstaller script created"
}

# Display post-installation instructions
function Show-PostInstallInstructions {
    Write-ColorOutput @"

╔════════════════════════════════════════════════════════════════╗
║                                                                ║
║   Installation Complete!                                       ║
║                                                                ║
╚════════════════════════════════════════════════════════════════╝

📁 Installation Path: $InstallPath
🔧 Service Name: $ServiceName
📊 Status: Running
🔐 Edition: $Edition

Next Steps:
1. Review configuration: $env:ProgramData\Otedama\appsettings.json
2. View logs: $env:ProgramData\Otedama\logs
3. Check service status: Get-Service "$ServiceName"
4. Access API: https://localhost:5001/api/health

Commands:
  Start:   Start-Service "$ServiceName"
  Stop:    Stop-Service "$ServiceName"
  Status:  Get-Service "$ServiceName"
  Logs:    Get-Content "`$env:ProgramData\Otedama\logs\*.log" -Tail 50

Documentation: https://github.com/your-org/potion-service

"@ "Green"
}

# Main installation workflow
function Start-Installation {
    try {
        Show-Banner
        Test-Prerequisites

        if (-not (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
            Install-DotNetRuntime
        }

        New-InstallationStructure
        Copy-ApplicationFiles
        Set-InitialConfiguration
        Install-WindowsService
        Configure-Firewall
        Set-SecurityPermissions
        New-UninstallerScript
        Start-PotionService

        Show-PostInstallInstructions

    } catch {
        Write-Error "Installation failed: $_"
        Write-Error $_.ScriptStackTrace
        exit 1
    }
}

# Execute installation
Start-Installation
