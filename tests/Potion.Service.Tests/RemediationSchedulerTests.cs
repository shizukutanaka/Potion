using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Options;
using Potion.Service.Remediation;
using Potion.Service.Scheduling;
using Xunit;
using Xunit.Abstractions;

namespace Potion.Service.Tests.Scheduling;

public class RemediationSchedulerTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<RemediationScheduler>> _loggerMock;
    private readonly Mock<IMaintenanceWindowEvaluator> _maintenanceWindowEvaluatorMock;
    private readonly Mock<IOptionsMonitor<RemediationPolicyOptions>> _optionsMock;
    private readonly Mock<IRemediationTaskExecutor> _taskExecutorMock;

    public RemediationSchedulerTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerMock = new Mock<ILogger<RemediationScheduler>>();
        _maintenanceWindowEvaluatorMock = new Mock<IMaintenanceWindowEvaluator>();
        _optionsMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();
        _taskExecutorMock = new Mock<IRemediationTaskExecutor>();
    }

    [Fact]
    public async Task ExecutePendingTasksAsync_WithValidTasks_ExecutesSuccessfully()
    {
        // Arrange
        var options = CreateTestOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);
        _maintenanceWindowEvaluatorMock.Setup(m => m.IsWithinMaintenanceWindow(It.IsAny<string>()))
            .Returns(true);

        var scheduler = new RemediationScheduler(
            _loggerMock.Object,
            _maintenanceWindowEvaluatorMock.Object,
            _optionsMock.Object,
            _taskExecutorMock.Object);

        // Act
        await scheduler.ExecutePendingTasksAsync(default);

        // Assert
        // Verify that tasks were attempted to be executed
        _taskExecutorMock.Verify(t => t.ExecuteTaskAsync(It.IsAny<RemediationTask>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecutePendingTasksAsync_OutsideMaintenanceWindow_SkipsExecution()
    {
        // Arrange
        var options = CreateTestOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);
        _maintenanceWindowEvaluatorMock.Setup(m => m.IsWithinMaintenanceWindow(It.IsAny<string>()))
            .Returns(false);

        var scheduler = new RemediationScheduler(
            _loggerMock.Object,
            _maintenanceWindowEvaluatorMock.Object,
            _optionsMock.Object,
            _taskExecutorMock.Object);

        // Act
        await scheduler.ExecutePendingTasksAsync(default);

        // Assert
        // Verify that no tasks were executed
        _taskExecutorMock.Verify(t => t.ExecuteTaskAsync(It.IsAny<RemediationTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void GetNextExecutionTime_ForScheduledTask_ReturnsCorrectTime()
    {
        // Arrange
        var options = CreateTestOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var scheduler = new RemediationScheduler(
            _loggerMock.Object,
            _maintenanceWindowEvaluatorMock.Object,
            _optionsMock.Object,
            _taskExecutorMock.Object);

        var task = options.Tasks[0];
        var now = DateTimeOffset.UtcNow;

        // Act
        var nextExecution = scheduler.GetNextExecutionTime(task, now);

        // Assert
        nextExecution.Should().BeAfter(now);
        nextExecution.Should().BeOnOrAfter(now.AddMinutes(task.RunEveryMinutes));
    }

    private static RemediationPolicyOptions CreateTestOptions()
    {
        return new RemediationPolicyOptions
        {
            MaxConcurrency = 2,
            SchedulerIntervalSeconds = 300,
            ScheduleJitterSeconds = 60,
            CommandAllowlist = new[] { "sfc.exe", "dism.exe" },
            MaintenanceWindows = new List<MaintenanceWindow>
            {
                new()
                {
                    Tag = "test",
                    StartTime = "22:00",
                    EndTime = "06:00",
                    DaysOfWeek = new[] { "Monday", "Tuesday" }
                }
            },
            Tasks = new List<RemediationTask>
            {
                new()
                {
                    Name = "test_task",
                    DisplayName = "Test Task",
                    Command = "sfc.exe",
                    Arguments = "/scannow",
                    RunEveryMinutes = 1440,
                    TimeoutSeconds = 3600,
                    RequiresElevation = true,
                    Enabled = true,
                    MaxRetries = 1,
                    RetryBackoffSeconds = 1800,
                    StopOnFailure = false,
                    MaintenanceWindowTag = "test",
                    AllowedExitCodes = new[] { 0 }
                }
            }
        };
    }
}
