using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Security;

/// <summary>
/// Bayesian Event Correlation Engine for advanced threat detection and root cause analysis.
/// Uses probabilistic inference to identify related security events and determine attack patterns.
/// Supports hypothesis testing and confidence scoring for incident investigation.
/// </summary>
public interface IBayesianCorrelationEngine
{
    /// <summary>Analyzes correlation between security events using Bayesian inference</summary>
    Task<EventCorrelationResult> AnalyzeEventCorrelationAsync(
        List<SecurityEvent> events,
        string hypothesis,
        CancellationToken cancellationToken);

    /// <summary>Performs root cause analysis on security incidents</summary>
    Task<RootCauseAnalysisResult> PerformRootCauseAnalysisAsync(
        string symptom,
        List<SecurityEvent> relatedEvents,
        CancellationToken cancellationToken);

    /// <summary>Identifies attack chains and lateral movement patterns</summary>
    Task<AttackChainAnalysisResult> AnalyzeAttackChainAsync(
        List<SecurityEvent> events,
        CancellationToken cancellationToken);

    /// <summary>Generates predictive threat assessment</summary>
    Task<PredictiveThreatAssessment> GeneratePredictiveThreatAsync(
        List<SecurityEvent> historicalEvents,
        CancellationToken cancellationToken);

    /// <summary>Calculates Bayesian confidence scores for event relationships</summary>
    Task<BayesianConfidenceAnalysis> CalculateConfidenceScoresAsync(
        List<SecurityEvent> events,
        List<string> hypotheses,
        CancellationToken cancellationToken);
}

/// <summary>Security event for correlation analysis</summary>
public sealed record SecurityEvent(
    string EventId,
    string EventType,               // "ProcessCreation", "NetworkConnection", "FileModification", etc.
    string Source,                  // User, process, IP, etc.
    string Target,                  // Resource affected
    DateTime Timestamp,
    ThreatLevel SuspicionLevel,
    Dictionary<string, string> Properties,
    double? BaselineDeviation = null  // How much this deviates from baseline (-100 to +100)
);

/// <summary>Event correlation analysis result</summary>
public sealed record EventCorrelationResult(
    double CorrelationScore,        // 0-100%
    string Hypothesis,
    bool HypothesisSupported,
    List<CorrelatedEventPair> CorrelatedPairs,
    List<string> SuspiciousPatterns,
    string AttackScenario,          // Predicted attack type if correlation found
    DateTime AnalysisTime
);

/// <summary>Pair of correlated events</summary>
public sealed record CorrelatedEventPair(
    string Event1Id,
    string Event2Id,
    double CorrelationStrength,     // 0-100%
    string Relationship,             // "sequential", "causal", "parallel", "dependency"
    TimeSpan TimeBetweenEvents,
    string Interpretation
);

/// <summary>Root cause analysis result</summary>
public sealed record RootCauseAnalysisResult(
    string Symptom,
    string ProbableRootCause,
    double ConfidenceScore,         // 0-100%
    List<CausalChainElement> CausalChain,
    List<string> ContributingFactors,
    List<string> RecommendedActions,
    DateTime AnalysisTime
);

/// <summary>Element in a causal chain</summary>
public sealed record CausalChainElement(
    int Order,
    string Event,
    string Cause,
    double CausalProbability,       // P(B|A)
    TimeSpan TimeSinceInitiation,
    string ImpactOnChain             // How this event impacts progression to next
);

/// <summary>Attack chain analysis result</summary>
public sealed record AttackChainAnalysisResult(
    bool AttackDetected,
    List<AttackStage> AttackStages,
    double OverallConfidence,       // 0-100%
    string EstimatedAttackType,     // MITRE ATT&CK technique
    TimeSpan AttackDuration,
    List<string> CompromisedAssets,
    List<string> ImpactedResources,
    string RecommendedContainment
);

/// <summary>Stage in an attack progression</summary>
public sealed record AttackStage(
    int StageNumber,
    string StageName,               // "Initial Access", "Lateral Movement", "Data Exfiltration"
    List<string> Events,
    double ConfidenceScore,
    string MitreAttackId,           // e.g., "T1566" (Phishing)
    TimeSpan DurationInStage,
    string NextExpectedStage
);

/// <summary>Predictive threat assessment based on historical patterns</summary>
public sealed record PredictiveThreatAssessment(
    double ThreatLikelihoodScore,   // 0-100%
    List<PredictedAttackVector> LikelyAttackVectors,
    List<string> VulnerableAssets,
    TimeSpan PredictedTimeToCompromise,
    string RecommendedPreventionMeasure,
    DateTime AssessmentTime
);

/// <summary>Predicted attack vector</summary>
public sealed record PredictedAttackVector(
    string AttackVector,
    double Likelihood,              // 0-100%
    string TargetAssetType,
    string PrerequisiteMissing,     // What attacker needs first
    string DefenseMeasure           // How to prevent
);

/// <summary>Bayesian confidence analysis result</summary>
public sealed record BayesianConfidenceAnalysis(
    List<HypothesisConfidence> HypothesisScores,
    string MostLikelyHypothesis,
    double HighestConfidenceScore,
    List<string> EvidentForMostLikely,
    List<string> EvidentAgainstAlternatives,
    double PosteriorProbability,    // P(H|E) - probability of hypothesis given evidence
    DateTime AnalysisTime
);

/// <summary>Confidence score for a hypothesis</summary>
public sealed record HypothesisConfidence(
    string Hypothesis,
    double PriorProbability,        // P(H) - initial probability
    double Likelihood,              // P(E|H) - likelihood of evidence if hypothesis true
    double PosteriorProbability,    // P(H|E) - probability of hypothesis given evidence
    List<string> SuportingEvents,
    List<string> ContradictingEvents
);

/// <summary>
/// Implementation of Bayesian Event Correlation Engine.
/// Uses probabilistic inference for advanced threat detection and investigation.
/// </summary>
public sealed class BayesianCorrelationEngine : IBayesianCorrelationEngine
{
    private readonly ILogger<BayesianCorrelationEngine> _logger;

    // Known attack patterns and their baseline probabilities
    private static readonly Dictionary<string, double> AttackPatternPriors = new()
    {
        { "Credential Compromise", 0.15 },      // 15% baseline
        { "Lateral Movement", 0.12 },
        { "Data Exfiltration", 0.08 },
        { "Privilege Escalation", 0.10 },
        { "Ransomware Infection", 0.05 },
        { "Persistence Mechanism", 0.08 },
        { "Reconnaissance", 0.20 }              // Most common
    };

    public BayesianCorrelationEngine(ILogger<BayesianCorrelationEngine> logger)
    {
        _logger = logger;
    }

    public async Task<EventCorrelationResult> AnalyzeEventCorrelationAsync(
        List<SecurityEvent> events,
        string hypothesis,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Analyzing correlation for events with hypothesis: {Hypothesis}", hypothesis);

        try
        {
            if (events.Count < 2)
                return CreateEmptyCorrelationResult(hypothesis);

            var correlatedPairs = new List<CorrelatedEventPair>();
            var suspiciousPatterns = new List<string>();

            // Sort events by timestamp
            var sortedEvents = events.OrderBy(e => e.Timestamp).ToList();

            // Analyze pairwise correlations
            for (int i = 0; i < sortedEvents.Count - 1; i++)
            {
                for (int j = i + 1; j < sortedEvents.Count; j++)
                {
                    var correlation = AnalyzeEventPair(sortedEvents[i], sortedEvents[j], hypothesis);
                    if (correlation.CorrelationStrength > 40) // Only significant correlations
                    {
                        correlatedPairs.Add(correlation);
                    }
                }
            }

            // Identify suspicious patterns
            suspiciousPatterns = IdentifySuspiciousPatterns(sortedEvents, correlatedPairs);

            // Determine if hypothesis is supported
            double avgCorrelation = correlatedPairs.Count > 0
                ? correlatedPairs.Average(p => p.CorrelationStrength)
                : 0;

            bool hypothesisSupported = avgCorrelation > 60;

            string attackScenario = DetermineAttackScenario(correlatedPairs, hypothesis);

            return new EventCorrelationResult(
                CorrelationScore: avgCorrelation,
                Hypothesis: hypothesis,
                HypothesisSupported: hypothesisSupported,
                CorrelatedPairs: correlatedPairs,
                SuspiciousPatterns: suspiciousPatterns,
                AttackScenario: attackScenario,
                AnalysisTime: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing event correlation");
            throw;
        }
    }

    public async Task<RootCauseAnalysisResult> PerformRootCauseAnalysisAsync(
        string symptom,
        List<SecurityEvent> relatedEvents,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Performing root cause analysis for symptom: {Symptom}", symptom);

        try
        {
            var causalChain = new List<CausalChainElement>();
            var sortedEvents = relatedEvents.OrderBy(e => e.Timestamp).ToList();

            // Build causal chain from events
            string currentCause = "Unknown Initial Event";
            for (int i = 0; i < sortedEvents.Count; i++)
            {
                var nextEvent = i + 1 < sortedEvents.Count ? sortedEvents[i + 1] : null;
                string impact = nextEvent != null
                    ? $"Led to {nextEvent.EventType}"
                    : "Final event in chain";

                causalChain.Add(new CausalChainElement(
                    Order: i,
                    Event: sortedEvents[i].EventType,
                    Cause: currentCause,
                    CausalProbability: CalculateCausalProbability(sortedEvents[i], nextEvent),
                    TimeSinceInitiation: sortedEvents[i].Timestamp - sortedEvents[0].Timestamp,
                    ImpactOnChain: impact
                ));

                currentCause = sortedEvents[i].EventType;
            }

            // Identify root cause (first event in chain)
            string probableRootCause = sortedEvents.Count > 0
                ? $"{sortedEvents[0].EventType} from {sortedEvents[0].Source}"
                : "Unable to determine";

            var contributingFactors = IdentifyContributingFactors(sortedEvents);
            var recommendations = GenerateRecommendations(symptom, causalChain);

            double confidence = CalculateRootCauseConfidence(causalChain);

            return new RootCauseAnalysisResult(
                Symptom: symptom,
                ProbableRootCause: probableRootCause,
                ConfidenceScore: confidence,
                CausalChain: causalChain,
                ContributingFactors: contributingFactors,
                RecommendedActions: recommendations,
                AnalysisTime: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in root cause analysis");
            throw;
        }
    }

    public async Task<AttackChainAnalysisResult> AnalyzeAttackChainAsync(
        List<SecurityEvent> events,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Analyzing attack chain from {Count} events", events.Count);

        try
        {
            var sortedEvents = events.OrderBy(e => e.Timestamp).ToList();
            var attackStages = new List<AttackStage>();

            // Classify events into attack stages
            var stageMappings = ClassifyEventsIntoStages(sortedEvents);

            double overallConfidence = 0;
            var compromisedAssets = new HashSet<string>();
            var estimatedAttackType = "Unknown";

            foreach (var stageGroup in stageMappings)
            {
                var stageEvents = stageGroup.Value;
                var stageConfidence = CalculateStageConfidence(stageEvents);

                attackStages.Add(new AttackStage(
                    StageNumber: stageGroup.Key,
                    StageName: MapStageName(stageGroup.Key),
                    Events: stageEvents.Select(e => e.EventType).ToList(),
                    ConfidenceScore: stageConfidence,
                    MitreAttackId: MapMitreAttackId(stageGroup.Key),
                    DurationInStage: CalculateStageDuration(stageEvents),
                    NextExpectedStage: PredictNextStage(stageGroup.Key)
                ));

                overallConfidence += stageConfidence;

                // Collect compromised assets
                foreach (var evt in stageEvents)
                {
                    compromisedAssets.Add(evt.Target);
                }
            }

            overallConfidence /= Math.Max(1, attackStages.Count);
            estimatedAttackType = DetermineAttackType(attackStages);

            bool attackDetected = overallConfidence > 50;
            var totalDuration = sortedEvents.Count > 0
                ? sortedEvents.Last().Timestamp - sortedEvents.First().Timestamp
                : TimeSpan.Zero;

            return new AttackChainAnalysisResult(
                AttackDetected: attackDetected,
                AttackStages: attackStages,
                OverallConfidence: overallConfidence,
                EstimatedAttackType: estimatedAttackType,
                AttackDuration: totalDuration,
                CompromisedAssets: compromisedAssets.ToList(),
                ImpactedResources: sortedEvents.Select(e => e.Target).Distinct().ToList(),
                RecommendedContainment: GenerateContainmentRecommendation(estimatedAttackType)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in attack chain analysis");
            throw;
        }
    }

    public async Task<PredictiveThreatAssessment> GeneratePredictiveThreatAsync(
        List<SecurityEvent> historicalEvents,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Generating predictive threat assessment from {Count} historical events", historicalEvents.Count);

        try
        {
            var likelyVectors = new List<PredictedAttackVector>();
            double threatLikelihood = 0;

            // Analyze historical patterns
            var patterns = AnalyzeHistoricalPatterns(historicalEvents);

            foreach (var pattern in patterns)
            {
                var vector = new PredictedAttackVector(
                    AttackVector: pattern.Key,
                    Likelihood: Math.Min(100, GetAttackPatternPrior(pattern.Key) * 100 + pattern.Value),
                    TargetAssetType: DetermineTargetAsset(pattern.Key),
                    PrerequisiteMissing: DeterminePrerequisite(pattern.Key),
                    DefenseMeasure: SuggestDefense(pattern.Key)
                );
                likelyVectors.Add(vector);
                threatLikelihood += vector.Likelihood;
            }

            threatLikelihood /= Math.Max(1, likelyVectors.Count);

            var vulnerableAssets = IdentifyVulnerableAssets(historicalEvents);
            var timeToCompromise = EstimateTimeToCompromise(likelyVectors);

            return new PredictiveThreatAssessment(
                ThreatLikelihoodScore: Math.Min(100, threatLikelihood),
                LikelyAttackVectors: likelyVectors.OrderByDescending(v => v.Likelihood).ToList(),
                VulnerableAssets: vulnerableAssets,
                PredictedTimeToCompromise: timeToCompromise,
                RecommendedPreventionMeasure: RecommendPrevention(likelyVectors),
                AssessmentTime: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in predictive threat assessment");
            throw;
        }
    }

    public async Task<BayesianConfidenceAnalysis> CalculateConfidenceScoresAsync(
        List<SecurityEvent> events,
        List<string> hypotheses,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Calculating Bayesian confidence scores for {Count} hypotheses", hypotheses.Count);

        try
        {
            var hypothesisScores = new List<HypothesisConfidence>();

            foreach (var hypothesis in hypotheses)
            {
                double priorProb = GetAttackPatternPrior(hypothesis);
                double likelihood = CalculateLikelihood(events, hypothesis);
                double posteriorProb = (likelihood * priorProb) /
                    Math.Max(0.01, hypotheses.Sum(h => CalculateLikelihood(events, h) * GetAttackPatternPrior(h)));

                var supporting = events
                    .Where(e => e.EventType.Contains(hypothesis) || e.Properties.Values.Any(v => v.Contains(hypothesis)))
                    .Select(e => e.EventId)
                    .ToList();

                hypothesisScores.Add(new HypothesisConfidence(
                    Hypothesis: hypothesis,
                    PriorProbability: priorProb,
                    Likelihood: likelihood,
                    PosteriorProbability: posteriorProb,
                    SuportingEvents: supporting,
                    ContradictingEvents: new()
                ));
            }

            var topHypothesis = hypothesisScores.OrderByDescending(h => h.PosteriorProbability).First();

            return new BayesianConfidenceAnalysis(
                HypothesisScores: hypothesisScores.OrderByDescending(h => h.PosteriorProbability).ToList(),
                MostLikelyHypothesis: topHypothesis.Hypothesis,
                HighestConfidenceScore: topHypothesis.PosteriorProbability * 100,
                EvidentForMostLikely: topHypothesis.SuportingEvents,
                EvidentAgainstAlternatives: new(),
                PosteriorProbability: topHypothesis.PosteriorProbability,
                AnalysisTime: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating confidence scores");
            throw;
        }
    }

    // Private helper methods

    private CorrelatedEventPair AnalyzeEventPair(SecurityEvent e1, SecurityEvent e2, string hypothesis)
    {
        var timeBetween = e2.Timestamp - e1.Timestamp;
        double correlation = 0;

        // Calculate correlation based on temporal proximity and event similarity
        if (timeBetween.TotalSeconds < 300) // Within 5 minutes
            correlation += 30;

        if (e1.Target == e2.Source) // Causal relationship
            correlation += 40;

        if (e1.EventType.Contains(hypothesis) && e2.EventType.Contains(hypothesis))
            correlation += 30;

        correlation = Math.Min(100, correlation);

        return new CorrelatedEventPair(
            Event1Id: e1.EventId,
            Event2Id: e2.EventId,
            CorrelationStrength: correlation,
            Relationship: DetermineRelationship(e1, e2),
            TimeBetweenEvents: timeBetween,
            Interpretation: $"{e1.EventType} likely caused {e2.EventType}"
        );
    }

    private List<string> IdentifySuspiciousPatterns(List<SecurityEvent> events, List<CorrelatedEventPair> pairs)
    {
        var patterns = new List<string>();

        if (pairs.Count > 5)
            patterns.Add("Rapid sequence of correlated events suggests coordinated attack");

        var highSuspicionEvents = events.Where(e => e.SuspicionLevel >= ThreatLevel.High).ToList();
        if (highSuspicionEvents.Count > 2)
            patterns.Add($"Multiple high-suspicion events detected: {highSuspicionEvents.Count}");

        return patterns;
    }

    private string DetermineAttackScenario(List<CorrelatedEventPair> pairs, string hypothesis)
    {
        return pairs.Count > 3
            ? $"Possible {hypothesis} attack detected"
            : $"Insufficient evidence for {hypothesis}";
    }

    private string DetermineRelationship(SecurityEvent e1, SecurityEvent e2)
    {
        if (e1.Target == e2.Source) return "causal";
        if ((e2.Timestamp - e1.Timestamp).TotalSeconds < 10) return "sequential";
        return "related";
    }

    private double CalculateCausalProbability(SecurityEvent e1, SecurityEvent? e2)
    {
        if (e2 == null) return 0;
        return e1.Target == e2.Source ? 0.85 : 0.35;
    }

    private List<string> IdentifyContributingFactors(List<SecurityEvent> events)
    {
        var factors = new List<string>();
        if (events.Any(e => e.SuspicionLevel >= ThreatLevel.High))
            factors.Add("Multiple high-severity events");
        if (events.GroupBy(e => e.Source).Count() > 3)
            factors.Add("Activity from multiple sources");
        return factors;
    }

    private List<string> GenerateRecommendations(string symptom, List<CausalChainElement> chain)
    {
        return new List<string>
        {
            $"Investigate root cause: {chain.FirstOrDefault()?.Cause ?? "Unknown"}",
            "Isolate affected systems for forensic analysis",
            "Review access logs for indicator of compromise",
            "Update detection rules based on findings"
        };
    }

    private double CalculateRootCauseConfidence(List<CausalChainElement> chain)
    {
        return chain.Count > 0
            ? chain.Average(e => e.CausalProbability) * 100
            : 0;
    }

    private Dictionary<int, List<SecurityEvent>> ClassifyEventsIntoStages(List<SecurityEvent> events)
    {
        // Simplified: group by event type
        var stages = new Dictionary<int, List<SecurityEvent>>();
        int stage = 1;
        var currentType = "";

        foreach (var evt in events)
        {
            if (evt.EventType != currentType)
            {
                stage++;
                currentType = evt.EventType;
            }

            if (!stages.ContainsKey(stage))
                stages[stage] = new List<SecurityEvent>();

            stages[stage].Add(evt);
        }

        return stages;
    }

    private double CalculateStageConfidence(List<SecurityEvent> stageEvents)
    {
        return Math.Min(100, stageEvents.Count * 15);
    }

    private string MapStageName(int stage)
    {
        return stage switch
        {
            1 => "Initial Access",
            2 => "Lateral Movement",
            3 => "Privilege Escalation",
            4 => "Persistence",
            5 => "Data Exfiltration",
            _ => "Unknown Stage"
        };
    }

    private string MapMitreAttackId(int stage)
    {
        return stage switch
        {
            1 => "T1566", // Phishing
            2 => "T1570", // Lateral Movement
            3 => "T1548", // Privilege Escalation
            4 => "T1547", // Persistence
            5 => "T1567", // Exfiltration
            _ => "Unknown"
        };
    }

    private TimeSpan CalculateStageDuration(List<SecurityEvent> events)
    {
        return events.Count > 1
            ? events.Last().Timestamp - events.First().Timestamp
            : TimeSpan.Zero;
    }

    private string PredictNextStage(int currentStage)
    {
        return (currentStage + 1) switch
        {
            2 => "Lateral Movement",
            3 => "Privilege Escalation",
            4 => "Persistence",
            5 => "Data Exfiltration",
            _ => "Attack Completion"
        };
    }

    private string DetermineAttackType(List<AttackStage> stages)
    {
        return stages.Count > 3 ? "Sophisticated Multi-Stage APT" : "Standard Attack";
    }

    private string GenerateContainmentRecommendation(string attackType)
    {
        return $"Isolate affected systems immediately. Block attacker IP ranges. Reset compromised credentials. Restore from clean backups.";
    }

    private Dictionary<string, double> AnalyzeHistoricalPatterns(List<SecurityEvent> events)
    {
        var patterns = new Dictionary<string, double>();
        var eventTypes = events.GroupBy(e => e.EventType).ToList();

        foreach (var group in eventTypes)
        {
            patterns[group.Key] = Math.Min(1, group.Count() / 10.0);
        }

        return patterns;
    }

    private double GetAttackPatternPrior(string pattern)
    {
        return AttackPatternPriors.TryGetValue(pattern, out var prior) ? prior : 0.05;
    }

    private double CalculateLikelihood(List<SecurityEvent> events, string hypothesis)
    {
        var matching = events.Count(e =>
            e.EventType.Contains(hypothesis) ||
            e.Properties.Values.Any(v => v.Contains(hypothesis)));

        return Math.Min(1, matching / (double)Math.Max(1, events.Count));
    }

    private string DetermineTargetAsset(string attackVector) => "Critical Infrastructure";
    private string DeterminePrerequisite(string attackVector) => "Initial Access Credential";
    private string SuggestDefense(string attackVector) => "Implement multi-factor authentication";
    private List<string> IdentifyVulnerableAssets(List<SecurityEvent> events) => events.Select(e => e.Target).Distinct().ToList();
    private TimeSpan EstimateTimeToCompromise(List<PredictedAttackVector> vectors) => TimeSpan.FromDays(7);
    private string RecommendPrevention(List<PredictedAttackVector> vectors) => "Implement defense-in-depth strategy";

    private EventCorrelationResult CreateEmptyCorrelationResult(string hypothesis) =>
        new(0, hypothesis, false, new(), new(), "Insufficient data", DateTime.UtcNow);
}
