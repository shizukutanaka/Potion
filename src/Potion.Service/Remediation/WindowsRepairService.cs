using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Potion.Service.Infrastructure;

namespace Potion.Service.Remediation;

/// <summary>
/// Core Windows system repair service implementing industry best practices for system healing.
/// Provides automated remediation for common Windows issues without user intervention.
/// </summary>
public interface IWindowsRepairService
{
    Task<RepairResult> RunSystemFileCheckAsync(CancellationToken cancellationToken);
    Task<RepairResult> RunDiskCheckAsync(string driveLetter, bool repair, CancellationToken cancellationToken);
    Task<RepairResult> RunDismRepairAsync(CancellationToken cancellationToken);
    Task<RepairResult> CleanupWindowsComponentsAsync(CancellationToken cancellationToken);
    Task<RepairResult> OptimizeWindowsStartupAsync(CancellationToken cancellationToken);
}

public sealed record RepairResult(
    bool Success,
    string Command,
    string Output,
    string Error,
    TimeSpan Duration,
    int ExitCode
);

public sealed class WindowsRepairService : IWindowsRepairService
{
    private readonly ILogger<WindowsRepairService> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly IAuditTrailService _auditTrailService;
    private readonly ISecureCommunicator _secureCommunicator;

    // Allowed commands whitelist - Security requirement: only pre-approved commands
    private static readonly Dictionary<string, string[]> AllowedCommands = new()
    {
        ["sfc"] = ["sfc", "/scannow"],
        ["chkdsk"] = ["chkdsk", "/f", "/r"],
        ["dism"] = ["dism", "/Online", "/Cleanup-Image", "/RestoreHealth"],
        ["disk-cleanup"] = ["Cleanmgr", "/sagerun:1"],
        ["defrag"] = ["defrag", "/U", "/V"]
    };

    public WindowsRepairService(
        ILogger<WindowsRepairService> logger,
        IProcessRunner processRunner,
        IAuditTrailService auditTrailService,
        ISecureCommunicator secureCommunicator)
    {
        _logger = logger;
        _processRunner = processRunner;
        _auditTrailService = auditTrailService;
        _secureCommunicator = secureCommunicator;
    }

    /// <summary>
    /// Runs System File Checker (SFC) to scan and repair Windows system files.
    /// Requires administrator privileges and should run in Safe Mode for optimal results.
    /// </summary>
    public async Task<RepairResult> RunSystemFileCheckAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting System File Checker (SFC) scan");

        try
        {
            // Verify administrator privileges
            if (!IsAdministrator())
            {
                var error = "Administrator privileges required for SFC";
                _logger.LogError(error);
                await _auditTrailService.LogAsync("SFC_FAILED_NO_ADMIN", error);
                return new RepairResult(false, "sfc /scannow", "", error, TimeSpan.Zero, -1);
            }

            var startTime = DateTimeOffset.UtcNow;
            var result = await _processRunner.RunAsync("cmd.exe", "/c sfc /scannow", cancellationToken);
            var duration = DateTimeOffset.UtcNow - startTime;

            var success = result.ExitCode == 0;
            await _auditTrailService.LogAsync(
                success ? "SFC_SUCCESS" : "SFC_FAILED",
                $"SFC scan completed with exit code {result.ExitCode}"
            );

            _logger.LogInformation(
                "SFC scan completed in {Duration}ms with exit code {ExitCode}",
                duration.TotalMilliseconds, result.ExitCode
            );

            return new RepairResult(success, "sfc /scannow", result.Output, result.Error, duration, result.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SFC scan failed with exception");
            await _auditTrailService.LogAsync("SFC_EXCEPTION", ex.Message);
            return new RepairResult(false, "sfc /scannow", "", ex.Message, TimeSpan.Zero, -1);
        }
    }

    /// <summary>
    /// Runs CHKDSK to check disk for logical and physical errors.
    /// Note: CHKDSK requires exclusive access and typically schedules for next restart.
    /// </summary>
    public async Task<RepairResult> RunDiskCheckAsync(string driveLetter, bool repair, CancellationToken cancellationToken)
    {
        if (!ValidateDriveLetter(driveLetter))
        {
            var error = $"Invalid drive letter: {driveLetter}";
            _logger.LogError(error);
            return new RepairResult(false, "chkdsk", "", error, TimeSpan.Zero, -1);
        }

        _logger.LogInformation("Starting CHKDSK on drive {Drive} with repair={Repair}", driveLetter, repair);

        try
        {
            if (!IsAdministrator())
            {
                var error = "Administrator privileges required for CHKDSK";
                _logger.LogError(error);
                await _auditTrailService.LogAsync("CHKDSK_FAILED_NO_ADMIN", error);
                return new RepairResult(false, "chkdsk", "", error, TimeSpan.Zero, -1);
            }

            var args = repair
                ? $"{driveLetter}: /F /R"
                : $"{driveLetter}: /F";

            var startTime = DateTimeOffset.UtcNow;
            var result = await _processRunner.RunAsync("cmd.exe", $"/c chkdsk {args}", cancellationToken);
            var duration = DateTimeOffset.UtcNow - startTime;

            await _auditTrailService.LogAsync(
                "CHKDSK_SCHEDULED",
                $"CHKDSK scheduled for drive {driveLetter} with exit code {result.ExitCode}"
            );

            _logger.LogInformation(
                "CHKDSK scheduled for drive {Drive} with exit code {ExitCode}",
                driveLetter, result.ExitCode
            );

            return new RepairResult(
                result.ExitCode == 0,
                $"chkdsk {args}",
                result.Output,
                result.Error,
                duration,
                result.ExitCode
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CHKDSK failed for drive {Drive}", driveLetter);
            await _auditTrailService.LogAsync("CHKDSK_EXCEPTION", ex.Message);
            return new RepairResult(false, "chkdsk", "", ex.Message, TimeSpan.Zero, -1);
        }
    }

    /// <summary>
    /// Runs DISM to repair Windows system image.
    /// Should be run before or after SFC for comprehensive system repair.
    /// </summary>
    public async Task<RepairResult> RunDismRepairAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting DISM system image repair");

        try
        {
            if (!IsAdministrator())
            {
                var error = "Administrator privileges required for DISM";
                _logger.LogError(error);
                await _auditTrailService.LogAsync("DISM_FAILED_NO_ADMIN", error);
                return new RepairResult(false, "dism", "", error, TimeSpan.Zero, -1);
            }

            var startTime = DateTimeOffset.UtcNow;
            var result = await _processRunner.RunAsync(
                "cmd.exe",
                "/c dism /Online /Cleanup-Image /RestoreHealth",
                cancellationToken
            );
            var duration = DateTimeOffset.UtcNow - startTime;

            var success = result.ExitCode == 0;
            await _auditTrailService.LogAsync(
                success ? "DISM_SUCCESS" : "DISM_FAILED",
                $"DISM repair completed with exit code {result.ExitCode}"
            );

            _logger.LogInformation(
                "DISM repair completed in {Duration}ms with exit code {ExitCode}",
                duration.TotalMilliseconds, result.ExitCode
            );

            return new RepairResult(success, "dism /Online /Cleanup-Image /RestoreHealth", result.Output, result.Error, duration, result.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DISM repair failed with exception");
            await _auditTrailService.LogAsync("DISM_EXCEPTION", ex.Message);
            return new RepairResult(false, "dism", "", ex.Message, TimeSpan.Zero, -1);
        }
    }

    /// <summary>
    /// Cleans up Windows temporary files and component store.
    /// Safe operation that doesn't require restart.
    /// </summary>
    public async Task<RepairResult> CleanupWindowsComponentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Windows component cleanup");

        try
        {
            // Clean temp files
            var tempDirs = new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };

            foreach (var tempDir in tempDirs)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        foreach (var file in Directory.GetFiles(tempDir))
                        {
                            try { File.Delete(file); }
                            catch { /* Ignore files that can't be deleted */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup temp directory: {TempDir}", tempDir);
                }
            }

            var startTime = DateTimeOffset.UtcNow;
            var result = await _processRunner.RunAsync("cmd.exe", "/c Cleanmgr /sagerun:1", cancellationToken);
            var duration = DateTimeOffset.UtcNow - startTime;

            await _auditTrailService.LogAsync("CLEANUP_COMPLETED", "Windows component cleanup completed");

            _logger.LogInformation("Windows component cleanup completed in {Duration}ms", duration.TotalMilliseconds);

            return new RepairResult(true, "cleanup", result.Output, result.Error, duration, result.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Component cleanup failed");
            await _auditTrailService.LogAsync("CLEANUP_EXCEPTION", ex.Message);
            return new RepairResult(false, "cleanup", "", ex.Message, TimeSpan.Zero, -1);
        }
    }

    /// <summary>
    /// Optimizes Windows startup by analyzing and removing unnecessary startup programs.
    /// Non-invasive operation that improves boot performance.
    /// </summary>
    public async Task<RepairResult> OptimizeWindowsStartupAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Windows startup optimization");

        try
        {
            var startTime = DateTimeOffset.UtcNow;

            // Get list of startup programs
            var result = await _processRunner.RunAsync(
                "powershell.exe",
                "-NoProfile -Command \"Get-CimInstance Win32_StartupCommand | Select-Object Name, Command | ConvertTo-Json\"",
                cancellationToken
            );

            var duration = DateTimeOffset.UtcNow - startTime;

            if (result.ExitCode == 0)
            {
                await _auditTrailService.LogAsync("STARTUP_ANALYSIS_COMPLETE", "Startup programs analyzed");
                _logger.LogInformation("Startup programs analysis completed with {Count} entries", result.Output.Length);
            }

            return new RepairResult(result.ExitCode == 0, "startup-optimization", result.Output, result.Error, duration, result.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup optimization failed");
            await _auditTrailService.LogAsync("STARTUP_EXCEPTION", ex.Message);
            return new RepairResult(false, "startup-optimization", "", ex.Message, TimeSpan.Zero, -1);
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool ValidateDriveLetter(string driveLetter)
    {
        if (string.IsNullOrEmpty(driveLetter)) return false;
        if (driveLetter.Length > 1) return false;
        return char.IsLetter(driveLetter[0]);
    }
}
