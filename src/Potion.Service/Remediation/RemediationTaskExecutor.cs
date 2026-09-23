using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Potion.Service.Infrastructure;
using Potion.Service.Options;

namespace Potion.Service.Remediation;

public sealed record RemediationTaskDescriptor(string Name, RemediationTaskOption Option);

public interface IRemediationTaskExecutor
{
    Task ExecuteAsync(RemediationTaskDescriptor descriptor, CancellationToken cancellationToken);
}

public sealed class RemediationTaskExecutor : IRemediationTaskExecutor
{
    private readonly ILogger<RemediationTaskExecutor> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly ICommandValidator _commandValidator;
    private readonly RemediationExecutionStats _executionStats;

    public RemediationTaskExecutor(
        ILogger<RemediationTaskExecutor> logger,
        IProcessRunner processRunner,
        ICommandValidator commandValidator,
        RemediationExecutionStats executionStats)
    {
        _logger = logger;
        _processRunner = processRunner;
        _commandValidator = commandValidator;
        _executionStats = executionStats;
    }

    public async Task ExecuteAsync(RemediationTaskDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var option = descriptor.Option;
        var startUtc = DateTimeOffset.UtcNow;

        _commandValidator.EnsureCommandIsAllowed(option.Command);
        _logger.LogInformation("Executing remediation task: {TaskName}", option.Name);

        _executionStats.IncrementInFlight();
        PotionMetrics.UpdateConcurrentOperations((int)_executionStats.InFlightCount);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = option.Command,
                Arguments = option.Arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var timeout = TimeSpan.FromSeconds(option.TimeoutSeconds);
            var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);

            var duration = DateTimeOffset.UtcNow - startUtc;
            var success = option.AllowedExitCodes.Contains(result.ExitCode) ||
                         (option.AllowedExitCodes.Count == 0 && result.ExitCode == 0);

            _logger.LogInformation(
                "Remediation task {TaskName} completed in {Duration}ms with exit code {ExitCode}",
                option.Name, duration.TotalMilliseconds, result.ExitCode
            );

            PotionMetrics.RecordRemediationTask(option.Name, success, duration);
            _executionStats.RecordExecution(success);

            if (!success)
            {
                _logger.LogWarning(
                    "Remediation task {TaskName} failed: {Error}",
                    option.Name, result.StandardError
                );
            }
        }
        catch (Exception ex)
        {
            PotionMetrics.RecordRemediationTask(option.Name, false, DateTimeOffset.UtcNow - startUtc);
            _executionStats.RecordExecution(false);
            _logger.LogError(ex, "Remediation task {TaskName} failed with exception", option.Name);
            throw;
        }
        finally
        {
            _executionStats.DecrementInFlight();
            PotionMetrics.UpdateConcurrentOperations((int)_executionStats.InFlightCount);
        }
    }
}
