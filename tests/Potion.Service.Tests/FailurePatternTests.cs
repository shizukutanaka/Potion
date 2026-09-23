using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Regression coverage for the anomaly baseline: the candidate value must be
/// evaluated against the PRIOR window. Including it in its own baseline inflated
/// the deviation and shifted the effective threshold from 2σ to ~2.3σ, masking
/// borderline anomalies.
/// </summary>
public sealed class FailurePatternTests
{
    [Fact]
    public void BorderlineAnomaly_IsDetectedAgainstPriorWindow()
    {
        var pattern = new PredictiveRemediationService.FailurePattern();

        // Build a window of alternating 40/60: mean 50, σ 10, threshold 70.
        for (var i = 0; i < 19; i++)
        {
            Assert.False(pattern.IsAnomaly(i % 2 == 0 ? 40 : 60));
        }

        // 71 exceeds the true 70 threshold; self-inclusion would have masked it
        // (inflated threshold ≈ 72.7).
        Assert.True(pattern.IsAnomaly(71));
    }

    [Fact]
    public void ValueBelowThreshold_IsNotAnomaly()
    {
        var pattern = new PredictiveRemediationService.FailurePattern();

        for (var i = 0; i < 19; i++)
        {
            Assert.False(pattern.IsAnomaly(i % 2 == 0 ? 40 : 60));
        }

        Assert.False(pattern.IsAnomaly(65));
    }

    [Fact]
    public void FewerThanTenSamples_NeverAnomalous()
    {
        var pattern = new PredictiveRemediationService.FailurePattern();

        // Even an extreme spike is not judged before the window is trained.
        for (var i = 0; i < 9; i++)
        {
            Assert.False(pattern.IsAnomaly(i == 8 ? 1000 : 50));
        }
    }
}
