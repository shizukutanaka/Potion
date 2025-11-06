@echo off
REM Potion Self-Healing Service Silent Installer
REM Usage: install.cmd [/quiet] [/uninstall]

setlocal enabledelayedexpansion

set "QUIET=false"
set "UNINSTALL=false"

:parse_args
if "%~1"=="" goto :main
if /i "%~1"=="/quiet" (
    set "QUIET=true"
    shift
    goto :parse_args
)
if /i "%~1"=="/uninstall" (
    set "UNINSTALL=true"
    shift
    goto :parse_args
)
echo Invalid argument: %~1
echo Usage: %~nx0 [/quiet] [/uninstall]
exit /b 1

:main
if "%UNINSTALL%"=="true" (
    echo Uninstalling Potion Self-Healing Service...
    if "%QUIET%"=="true" (
        msiexec.exe /x "Potion.msi" /quiet /norestart
    ) else (
        msiexec.exe /x "Potion.msi"
    )
    if !errorlevel! neq 0 (
        echo Error: Failed to uninstall Potion
        exit /b !errorlevel!
    )
    echo Potion has been successfully uninstalled.
) else (
    echo Installing Potion Self-Healing Service...

    REM Check if .NET 8.0 is installed
    echo Checking .NET 8.0 runtime...
    dotnet --version >nul 2>&1
    if !errorlevel! neq 0 (
        echo Error: .NET 8.0 runtime is not installed
        echo Please install .NET 8.0 runtime from: https://dotnet.microsoft.com/download/dotnet/8.0
        exit /b 1
    )

    REM Check administrator privileges
    net session >nul 2>&1
    if !errorlevel! neq 0 (
        echo Error: Administrator privileges required
        echo Please run as administrator
        exit /b 1
    )

    REM Install MSI
    if "%QUIET%"=="true" (
        echo Running silent installation...
        msiexec.exe /i "Potion.msi" /quiet /norestart
    ) else (
        echo Running interactive installation...
        msiexec.exe /i "Potion.msi"
    )

    if !errorlevel! neq 0 (
        echo Error: Failed to install Potion
        exit /b !errorlevel!
    )

    echo Potion has been successfully installed.

    REM Verify service is running
    echo Verifying service status...
    sc query "Potion Self-Healing Service" | findstr "RUNNING" >nul
    if !errorlevel! neq 0 (
        echo Warning: Service may not be running properly
        echo Check the event log for more details
    ) else (
        echo Service is running successfully
    )
)

echo.
echo Installation complete.
if "%QUIET%"=="false" (
    echo Press any key to continue...
    pause >nul
)
exit /b 0
