using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Remediation;

public interface IRemediationTaskCatalog
{
    IReadOnlyList<RemediationTaskDescriptor> GetTasks();
}

public sealed record RemediationTaskDescriptor(string Name, RemediationTaskOption Option);

public sealed class RemediationTaskCatalog : IRemediationTaskCatalog, IDisposable
{
    private readonly ILogger<RemediationTaskCatalog> _logger;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _optionsMonitor;
    private readonly IDisposable _optionsSubscription;
    private IReadOnlyList<RemediationTaskDescriptor> _tasks;

    public RemediationTaskCatalog(ILogger<RemediationTaskCatalog> logger, IOptionsMonitor<RemediationPolicyOptions> optionsMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _tasks = BuildDescriptors(optionsMonitor.CurrentValue);
        _optionsSubscription = _optionsMonitor.OnChange(options =>
        {
            _logger.LogInformation("Remediation policy updated; rebuilding task catalog");
            Volatile.Write(ref _tasks, BuildDescriptors(options));
        });
    }

    public IReadOnlyList<RemediationTaskDescriptor> GetTasks() => Volatile.Read(ref _tasks);

    public void Dispose()
    {
        _optionsSubscription.Dispose();
    }

    private IReadOnlyList<RemediationTaskDescriptor> BuildDescriptors(RemediationPolicyOptions options)
    {
        var descriptors = options.Tasks
            .Where(task => task.Enabled)
            .Select(task => new RemediationTaskDescriptor(task.Name, task))
            .ToArray();

        _logger.LogInformation("Loaded {TaskCount} remediation tasks from configuration", descriptors.Count);
        return Array.AsReadOnly(descriptors);
    }
}
