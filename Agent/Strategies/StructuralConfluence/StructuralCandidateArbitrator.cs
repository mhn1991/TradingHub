using Agent.Strategies.StructuralConfluence.Playbooks;
using ChartAnnotator.Models;

namespace Agent.Strategies.StructuralConfluence;

public sealed record StructuralArbitrationResult(
    PlaybookEvaluation? Selected,
    string ReasonCode,
    decimal ConfluenceAdjustment = 0m);

public sealed class StructuralCandidateArbitrator(StructuralArbitrationOptions options)
{
    public StructuralArbitrationResult Select(IReadOnlyList<PlaybookEvaluation> evaluations)
    {
        PlaybookEvaluation[] ready = evaluations
            .Where(item => item.IsReady && item.Geometry?.IsValid == true)
            .ToArray();
        if (ready.Length == 0)
            return new(null, evaluations.Select(item => item.ReasonCode).FirstOrDefault() ?? "StructuralNoCandidate");
        if (ready.Select(item => item.Direction).Distinct().Count() > 1)
            return new(null, "StructuralPlaybookConflict");

        PlaybookEvaluation selected = ready
            .OrderByDescending(item => item.MandatoryQualityFloor)
            .ThenByDescending(item => item.GeometryQuality)
            .ThenByDescending(item => item.Confidence)
            .ThenByDescending(item => item.CatalystAt)
            .ThenBy(item => item.PlaybookId, StringComparer.Ordinal)
            .First();
        decimal adjustment = ready.Length > 1
            ? Math.Clamp(options.SameDirectionConfluenceAdjustment, 0m, 8m)
            : 0m;
        if (adjustment > 0m)
            selected = selected with { Confidence = Math.Clamp(selected.Confidence + adjustment, 0m, 100m) };
        return new(selected, "StructuralCandidateSelected", adjustment);
    }
}
