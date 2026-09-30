using System.Diagnostics;
using System.Linq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// PotionActivitySource is the OTLP tracing surface. Without a registered
/// ActivityListener, StartActivity returns null — tests must install a
/// listener first, so they pin that the helpers produce properly tagged
/// activities only when a consumer exists (the production case).
/// </summary>
public sealed class PotionActivitySourceTests
{
    private static ActivityListener ListenAll()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Potion.Service",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public void StartRemediationActivity_WithListener_ReturnsTaggedActivity()
    {
        using var listener = ListenAll();

        using var activity = PotionActivitySource.StartRemediationActivity("cleanup-temp");

        Assert.NotNull(activity);
        Assert.Equal("RemediationTask", activity.OperationName);
        Assert.Contains(activity.TagObjects, t => t.Key == "task.name" && (string?)t.Value == "cleanup-temp");
    }

    [Fact]
    public void StartHealthCheckActivity_WithListener_ReturnsTaggedActivity()
    {
        using var listener = ListenAll();

        using var activity = PotionActivitySource.StartHealthCheckActivity();

        Assert.NotNull(activity);
        Assert.Equal("HealthCheck", activity.OperationName);
        Assert.Contains(activity.TagObjects, t => t.Key == "span.kind" && (string?)t.Value == "internal");
    }

    [Fact]
    public void StartSelfHealingActivity_WithListener_ReturnsTaggedActivity()
    {
        using var listener = ListenAll();

        using var activity = PotionActivitySource.StartSelfHealingActivity("memory-pressure");

        Assert.NotNull(activity);
        Assert.Equal("SelfHealing", activity.OperationName);
        Assert.Contains(activity.TagObjects, t => t.Key == "issue.type" && (string?)t.Value == "memory-pressure");
    }

    [Fact]
    public void StartActivities_ProduceDistinctCurrentActivities()
    {
        using var listener = ListenAll();

        using var a = PotionActivitySource.StartRemediationActivity("t");
        using var b = PotionActivitySource.StartHealthCheckActivity();

        Assert.NotSame(a, b);
        Assert.Same(b, Activity.Current);
    }
}
