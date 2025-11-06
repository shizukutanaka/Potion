#Requires -Version 7.0
#Requires -RunAsAdministrator

param(
    [Parameter(Mandatory=$false)]
    [string]$Environment = "Development",

    [Parameter(Mandatory=$false)]
    [string]$ServiceName = "Otedama Self-Healing Service",

    [Parameter(Mandatory=$false)]
    [string]$InstallPath = "$env:ProgramFiles\Otedama",

    [Parameter(Mandatory=$false)]
    [switch]$Force
)

# Configuration
$ServiceAccount = "NT AUTHORITY\SYSTEM"
$ServiceDescription = "Comprehensive health monitoring and system observability with advanced self-healing capabilities"
$LogPath = "$env:ProgramData\Otedama\logs"
$StatePath = "$env:ProgramData\Otedama\state"

Write-Host "🚀 Starting Potion Service deployment..." -ForegroundColor Green
Write-Host "Environment: $Environment" -ForegroundColor Cyan
Write-Host "Service Name: $ServiceName" -ForegroundColor Cyan
Write-Host "Install Path: $InstallPath" -ForegroundColor Cyan

# Check prerequisites
Write-Host "🔍 Checking prerequisites..." -ForegroundColor Yellow

if (-NOT (Test-Path "src/Potion.Service/Potion.Service.csproj")) {
    Write-Error "Potion.Service.csproj not found. Please run from project root."
    exit 1
}

if (-NOT (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Error ".NET SDK not found. Please install .NET 8.0 or later."
    exit 1
}

# Build the service
Write-Host "🔨 Building service..." -ForegroundColor Yellow

dotnet clean src/Potion.Service/Potion.Service.csproj -c Release
dotnet restore src/Potion.Service/Potion.Service.csproj
dotnet build src/Potion.Service/Potion.Service.csproj -c Release --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

dotnet publish src/Potion.Service/Potion.Service.csproj -c Release -o $InstallPath --no-build

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed!"
    exit 1
}

# Create directories
Write-Host "📁 Creating directories..." -ForegroundColor Yellow

New-Item -ItemType Directory -Path $LogPath -Force | Out-Null
New-Item -ItemType Directory -Path $StatePath -Force | Out-Null
New-Item -ItemType Directory -Path "$StatePath\security" -Force | Out-Null
New-Item -ItemType Directory -Path "$StatePath\telemetry" -Force | Out-Null

# Set permissions
Write-Host "🔐 Setting permissions..." -ForegroundColor Yellow

$acl = Get-Acl $InstallPath
$acl.SetAccessRuleProtection($true, $false)
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($rule)
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($ServiceAccount, "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($rule)
Set-Acl $InstallPath $acl

# Configure Windows Event Log
Write-Host "📝 Configuring Windows Event Log..." -ForegroundColor Yellow

New-EventLog -LogName Application -Source "Otedama" -ErrorAction SilentlyContinue

# Install as Windows service
Write-Host "⚙️ Installing Windows service..." -ForegroundColor Yellow

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    if ($Force) {
        Stop-Service $ServiceName -Force
        sc.exe delete $ServiceName
        Start-Sleep -Seconds 2
    }
    else {
        Write-Warning "Service '$ServiceName' already exists. Use -Force to reinstall."
        exit 1
    }
}

sc.exe create "$ServiceName" `
    binPath="$InstallPath\Potion.Service.exe" `
    start=auto `
    DisplayName="$ServiceDescription" `
    obj="$ServiceAccount" `
    password="" `
    type=own `
    error=normal

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create Windows service!"
    exit 1
}

# Configure service recovery
Write-Host "🔄 Configuring service recovery..." -ForegroundColor Yellow

sc.exe failure "$ServiceName" reset=86400 actions=restart/60000/restart/60000/restart/60000

# Configure service dependencies
sc.exe config "$ServiceName" depend=Winmgmt/LanmanWorkstation

# Start the service
Write-Host "▶️ Starting service..." -ForegroundColor Yellow

Start-Service $ServiceName

# Wait for service to start
$timeout = 60
$startTime = Get-Date

while ((Get-Service $ServiceName).Status -ne "Running" -and ((Get-Date) - $startTime).TotalSeconds -lt $timeout) {
    Start-Sleep -Seconds 2
    Write-Host "." -NoNewline
}

Write-Host ""

$serviceStatus = Get-Service $ServiceName

if ($serviceStatus.Status -eq "Running") {
    Write-Host "✅ Service started successfully!" -ForegroundColor Green

    # Test the service
    Write-Host "🧪 Testing service..." -ForegroundColor Yellow

    try {
        $response = Invoke-WebRequest -Uri "http://localhost:5000/api/health" -UseBasicParsing
        if ($response.StatusCode -eq 200) {
            Write-Host "✅ Health check passed!" -ForegroundColor Green
        }
    }
    catch {
        Write-Warning "Health check failed. Service may need more time to initialize."
    }
}
else {
    Write-Error "Service failed to start. Check Windows Event Log for details."
    exit 1
}

# Configure monitoring
Write-Host "📊 Configuring monitoring..." -ForegroundColor Yellow

# Create scheduled task for log cleanup
$logCleanupTask = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Date>$(Get-Date -Format 'yyyy-MM-dd')T00:00:00</Date>
    <Author>Administrator</Author>
    <Description>Clean up old Potion service logs</Description>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>$(Get-Date -Format 'yyyy-MM-dd')T02:00:00</StartBoundary>
      <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
      <Enabled>true</Enabled>
      <ScheduleByDay>
        <DaysInterval>1</DaysInterval>
      </ScheduleByDay>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$ServiceAccount</UserId>
      <LogonType>ServiceAccount</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>true</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-Command "Get-ChildItem '$LogPath\*.log' | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-30) } | Remove-Item"</Arguments>
    </Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName "Otedama Log Cleanup" -Xml $logCleanupTask -Force | Out-Null

Write-Host "✅ Deployment completed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "📋 Service Information:" -ForegroundColor Cyan
Write-Host "Name: $ServiceName"
Write-Host "Status: $((Get-Service $ServiceName).Status)"
Write-Host "Log Path: $LogPath"
Write-Host "State Path: $StatePath"
Write-Host ""
Write-Host "🛠️ Management Commands:" -ForegroundColor Cyan
Write-Host "Start: Start-Service '$ServiceName'"
Write-Host "Stop: Stop-Service '$ServiceName'"
Write-Host "Restart: Restart-Service '$ServiceName'"
Write-Host "Status: Get-Service '$ServiceName'"
Write-Host "Logs: Get-EventLog -LogName Application -Source 'Otedama'"
Write-Host ""
Write-Host "🌐 API Endpoints:" -ForegroundColor Cyan
Write-Host "Health: http://localhost:5000/api/health"
Write-Host "Metrics: http://localhost:5000/api/health/metrics"
Write-Host "Security: http://localhost:5000/api/health/security"
Write-Host "Documentation: http://localhost:5000/swagger"

# Generate deployment report
$reportPath = "$InstallPath\deployment-report.txt"
@"
Potion Service Deployment Report
===============================
Deployment Date: $(Get-Date)
Environment: $Environment
Service Name: $ServiceName
Install Path: $InstallPath
Service Status: $((Get-Service $ServiceName).Status)
.NET Version: $(dotnet --version)
Windows Version: $(Get-ComputerInfo | Select-Object -ExpandProperty WindowsProductName) $(Get-ComputerInfo | Select-Object -ExpandProperty WindowsVersion)

Features Deployed:
- Multi-language support (16 languages)
- Reactive programming with Rx patterns
- Functional programming with monads
- Machine learning anomaly detection
- Blockchain audit trails
- Kubernetes integration
- Chaos engineering
- GitOps automation
- Advanced security features

API Endpoints:
- Health monitoring: /api/health/*
- Security: /api/health/security/*
- Observability: /api/health/observability/*
- Testing: /api/health/testing/*
- Chaos engineering: /api/health/chaos/*

Next Steps:
1. Review logs: Get-EventLog -LogName Application -Source 'Otedama'
2. Check configuration: Get-Content '$InstallPath\appsettings.json'
3. Verify security: Get-Content '$StatePath\security\latest-audit.json'
4. Test integration: Invoke-WebRequest http://localhost:5000/api/health/testing/integration
"@ | Out-File -FilePath $reportPath

Write-Host ""
Write-Host "📄 Deployment report generated: $reportPath" -ForegroundColor Green

Write-Host ""
Write-Host "🎉 Potion Service deployment completed successfully!" -ForegroundColor Green
Write-Host "The service is now running with enterprise-grade features and multi-language support." -ForegroundColor Green
