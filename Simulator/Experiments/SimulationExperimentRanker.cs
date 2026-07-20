using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Simulator.Experiments.Models;

namespace Simulator.Experiments;

/// <summary>Deterministic held-out ranking. It may nominate candidates but never grants execution permission.</summary>
public static class SimulationExperimentRanker
{
    public static SimulationExperimentComparisonSummary Build(SimulationExperimentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SimulationExperimentRankingPolicy policy = snapshot.Manifest.Ranking;
        policy.Validate();

        Guid? baselineId = snapshot.Manifest.ProfileRuns
            .FirstOrDefault(run => run.BaselineProfileRunId is null && snapshot.Manifest.ProfileRuns
                .Any(other => other.BaselineProfileRunId == run.ProfileRunId.ToString("N")))?.ProfileRunId;
        SimulationExperimentComparisonRow[] source = snapshot.Profiles
            .Select(item => item.Result ?? throw new InvalidOperationException(
                $"Profile '{item.ProfileName}' has no held-out evaluation result."))
            .ToArray();
        decimal? baselineNet = baselineId is { } id
            ? source.Single(item => item.ProfileRunId == id).NetProfit
            : null;
        bool warmupEligible = snapshot.Manifest.TrainingWarmups.Values.All(item => item.PromotionEligible) &&
                              snapshot.Manifest.EvaluationWarmups.Values.All(item => item.PromotionEligible);
        bool datasetVerified = string.Equals(snapshot.Manifest.DatasetStatus, "Ready", StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(snapshot.Manifest.DatasetHash);

        var evaluated = source.Select(row => Evaluate(row, policy, baselineNet, warmupEligible, datasetVerified)).ToArray();
        SimulationExperimentComparisonRow[] eligible = evaluated.Where(row => row.IsEligible)
            .OrderByDescending(row => row.StabilityScore)
            .ThenBy(row => row.MaximumDrawdown)
            .ThenBy(row => row.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProfileRunId)
            .Select((row, index) => row with
            {
                Rank = index + 1,
                IsShortlisted = index < policy.ShortlistSize
            }).ToArray();
        Dictionary<Guid, SimulationExperimentComparisonRow> ranked = eligible.ToDictionary(row => row.ProfileRunId);
        SimulationExperimentComparisonRow[] rows = evaluated.Select(row => ranked.GetValueOrDefault(row.ProfileRunId, row))
            .OrderBy(row => row.Rank ?? int.MaxValue)
            .ThenByDescending(row => row.StabilityScore)
            .ThenBy(row => row.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SimulationPromotionCandidate[] candidates = policy.CreatePromotionCandidates
            ? rows.Where(row => row.IsShortlisted).Select(row => CreateCandidate(snapshot, row)).ToArray()
            : [];
        return new SimulationExperimentComparisonSummary
        {
            BaselineProfileRunId = baselineId,
            Profiles = rows,
            PromotionCandidates = candidates
        };
    }

    private static SimulationExperimentComparisonRow Evaluate(
        SimulationExperimentComparisonRow row,
        SimulationExperimentRankingPolicy policy,
        decimal? baselineNet,
        bool warmupEligible,
        bool datasetVerified)
    {
        var failures = new List<string>();
        if (row.TradeCount < policy.MinimumHeldOutTradeCount) failures.Add("MinimumHeldOutTradeCount");
        if (row.Expectancy is null || row.Expectancy <= policy.MinimumExpectancy) failures.Add("PositiveExpectancyAfterCosts");
        if (row.NetProfit is null) failures.Add("NetProfitUnavailable");
        if (row.MaximumDrawdown is null) failures.Add("MaximumDrawdownUnavailable");
        else if (policy.MaximumDrawdown is { } cap && row.MaximumDrawdown > cap) failures.Add("MaximumDrawdownCap");
        if (row.ProfitFactor is null || row.ProfitFactor < policy.MinimumProfitFactor) failures.Add("MinimumProfitFactor");
        if (policy.RequireWarningFreeEvaluation && row.Warnings.Count > 0) failures.Add("EvaluationWarningsPresent");
        if (!warmupEligible) failures.Add("WarmupNotPromotionEligible");
        if (!datasetVerified) failures.Add("DatasetHashNotVerified");

        if (failures.Count > 0)
        {
            return row with
            {
                DifferenceFromBaseline = Difference(row.NetProfit, baselineNet),
                HardGateFailures = failures.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        decimal expectancy = row.Expectancy!.Value;
        decimal drawdownAdjustedReturn = row.NetProfit!.Value / Math.Max(1m, Math.Abs(row.MaximumDrawdown!.Value));
        decimal profitFactorExcess = Math.Max(0m, row.ProfitFactor!.Value - 1m);
        decimal sampleConfidence = Math.Min(1m,
            row.TradeCount / (decimal)Math.Max(1, policy.MinimumHeldOutTradeCount * 4));
        var breakdown = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["HeldOutExpectancy"] = expectancy * policy.ExpectancyWeight,
            ["DrawdownAdjustedReturn"] = drawdownAdjustedReturn * policy.DrawdownAdjustedReturnWeight,
            ["ProfitFactorExcess"] = profitFactorExcess * policy.ProfitFactorWeight,
            ["SampleConfidence"] = sampleConfidence * policy.SampleConfidenceWeight
        };
        return row with
        {
            DifferenceFromBaseline = Difference(row.NetProfit, baselineNet),
            IsEligible = true,
            StabilityScore = decimal.Round(breakdown.Values.Sum(), 8, MidpointRounding.ToEven),
            ScoreBreakdown = breakdown
        };
    }

    private static SimulationPromotionCandidate CreateCandidate(
        SimulationExperimentSnapshot snapshot,
        SimulationExperimentComparisonRow row)
    {
        SimulationExperimentProfileRun run = snapshot.Manifest.ProfileRuns.Single(item => item.ProfileRunId == row.ProfileRunId);
        SimulationProfileRunProgress progress = snapshot.Profiles.Single(item => item.ProfileRunId == row.ProfileRunId);
        SimulationArtifactReference[] artifacts = progress.Artifacts.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.ArtifactId).ToArray();
        string agentHash = Hash(JsonSerializer.Serialize(run.Profile.ResolvedProfile.Agent.ToAgentDefinition()));
        string artifactManifest = string.Join('|', artifacts.Select(item => $"{item.ArtifactId:N}:{item.Kind}:{item.ContentHash}"));
        string packageHash = Hash(string.Join('|', run.Profile.ContentHash, agentHash,
            snapshot.Manifest.DatasetHash, artifactManifest, JsonSerializer.Serialize(snapshot.Manifest.Timeline)));
        Guid candidateId = StableGuid(Hash($"{snapshot.Id:N}|{run.ProfileRunId:N}|{packageHash}"));
        return new SimulationPromotionCandidate
        {
            CandidateId = candidateId,
            ExperimentId = snapshot.Id,
            ProfileRunId = run.ProfileRunId,
            ProfileId = run.Profile.ProfileId,
            ProfileRevision = run.Profile.Revision,
            ProfileContentHash = run.Profile.ContentHash,
            AgentDefinitionHash = agentHash,
            DatasetHash = snapshot.Manifest.DatasetHash!,
            CandidatePackageHash = packageHash,
            Timeline = snapshot.Manifest.Timeline,
            Artifacts = artifacts,
            Rank = row.Rank!.Value,
            StabilityScore = row.StabilityScore!.Value,
            AutomaticExecutablePermission = false
        };
    }

    private static decimal? Difference(decimal? value, decimal? baseline) =>
        value is null || baseline is null ? null : value - baseline;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid StableGuid(string hash) => new(Convert.FromHexString(hash)[..16]);
}
