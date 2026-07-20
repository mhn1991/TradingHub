namespace Simulator.Calibration;

/// <summary>Outcome states for one calibration run (blueprint §9.6/§13.3). Only <see cref="Improved"/> may be approved.</summary>
public enum CalibrationOutcome
{
    Improved,
    NoImprovement,
    InsufficientEvidence,
    UnstableAcrossFolds,
    FailedAbsoluteQualityGate,
    FailedExternalHoldout,
    BudgetRejected,
    FailedDataQuality,
    Cancelled,
    Failed
}

/// <summary>One parameter's final calibrated value plus the cross-fold evidence behind it (blueprint §9.7).</summary>
public sealed record CalibratedParameterOverride
{
    public required string ParameterId { get; init; }
    public required decimal DefaultValue { get; init; }
    public required decimal CalibratedValue { get; init; }
    /// <summary>Percentage of folds whose acceptable range/plateau contains <see cref="CalibratedValue"/>.</summary>
    public required decimal FoldSupportPercent { get; init; }
    /// <summary>Width (in the parameter's own units) of the stable plateau this value was drawn from.</summary>
    public required decimal PlateauWidth { get; init; }
    /// <summary>Which phase produced the final value - SensitivityScreening/CoordinateDescent/Interaction/Refinement.</summary>
    public required string SelectionStage { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ParameterId))
            throw new ArgumentException("ParameterId is required.");
        if (FoldSupportPercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(FoldSupportPercent));
        if (PlateauWidth < 0m)
            throw new ArgumentOutOfRangeException(nameof(PlateauWidth));
        if (string.IsNullOrWhiteSpace(SelectionStage))
            throw new ArgumentException("SelectionStage is required.");
    }
}

/// <summary>
/// Aggregate research evidence behind a candidate's <see cref="CalibrationOutcome"/> - the
/// numbers a human reviewer actually inspects before approving (blueprint §16 step 3).
/// </summary>
public sealed record CalibrationEvidenceSummary
{
    public required int FoldCount { get; init; }
    public required int AcceptableFoldCount { get; init; }
    public required decimal AcceptableFoldPercent { get; init; }

    public required decimal MedianValidationExpectancyR { get; init; }
    public required decimal MedianValidationDrawdownR { get; init; }
    public required int MedianValidationTradeCount { get; init; }
    public required decimal TrainValidationDegradation { get; init; }

    public required decimal BaselineMedianExpectancyR { get; init; }
    public required decimal ImprovementOverBaseline { get; init; }

    public required decimal ExternalHoldoutExpectancyR { get; init; }
    public required decimal ExternalHoldoutDrawdownR { get; init; }
    public required int ExternalHoldoutTradeCount { get; init; }
    public required decimal ExternalHoldoutBaselineExpectancyR { get; init; }

    public required int TotalCandidatesEvaluated { get; init; }

    public void Validate()
    {
        if (FoldCount < 1)
            throw new ArgumentOutOfRangeException(nameof(FoldCount));
        if (AcceptableFoldCount < 0 || AcceptableFoldCount > FoldCount)
            throw new ArgumentOutOfRangeException(nameof(AcceptableFoldCount));
        if (AcceptableFoldPercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(AcceptableFoldPercent));
        if (MedianValidationTradeCount < 0 || ExternalHoldoutTradeCount < 0)
            throw new ArgumentException("Trade counts cannot be negative.");
        if (TotalCandidatesEvaluated < 1)
            throw new ArgumentOutOfRangeException(nameof(TotalCandidatesEvaluated));
    }
}

/// <summary>
/// The compact, promotable research artifact (blueprint §13.2). Stores only differences from the
/// effective baseline as <see cref="Overrides"/>/<see cref="AblationOverrides"/>, plus the
/// complete resolved-candidate configuration hash so a consumer can verify the reconstructed
/// options object matches exactly what was actually evaluated. Never contains the full research
/// ledger - that is a separate, much larger record (<see cref="ExperimentLedgerId"/> points to it).
/// </summary>
public sealed record IndicatorCalibrationArtifact
{
    public required int SchemaVersion { get; init; }
    public required string CalibrationId { get; init; }

    public required string StrategyId { get; init; }
    public required string StrategyImplementationVersion { get; init; }
    public required string OptionsSchemaVersion { get; init; }
    public required string ManifestVersion { get; init; }

    /// <summary>"Instrument" in the first release - blueprint §7.2 defers group scope entirely.</summary>
    public required string Scope { get; init; }
    public required string Instrument { get; init; }

    public required string TimeframeTopologyHash { get; init; }
    public required string CandleDataIdentityHash { get; init; }

    public required string BaselineConfigurationHash { get; init; }
    public required string ResolvedCandidateConfigurationHash { get; init; }

    public required IReadOnlyList<CalibratedParameterOverride> Overrides { get; init; }
    public required IReadOnlyDictionary<string, bool> AblationOverrides { get; init; }

    public required CalibrationEvidenceSummary Evidence { get; init; }
    public required CalibrationOutcome Outcome { get; init; }

    public required string ExperimentLedgerId { get; init; }
    public required string ExperimentLedgerChecksum { get; init; }

    public required CalibrationPromotionStatus PromotionStatus { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public const string InstrumentScope = "Instrument";
    public const string GroupScope = "Group";

    public void Validate()
    {
        if (SchemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
        if (string.IsNullOrWhiteSpace(CalibrationId) ||
            string.IsNullOrWhiteSpace(StrategyId) ||
            string.IsNullOrWhiteSpace(StrategyImplementationVersion) ||
            string.IsNullOrWhiteSpace(OptionsSchemaVersion) ||
            string.IsNullOrWhiteSpace(ManifestVersion) ||
            string.IsNullOrWhiteSpace(Instrument) ||
            string.IsNullOrWhiteSpace(TimeframeTopologyHash) ||
            string.IsNullOrWhiteSpace(CandleDataIdentityHash) ||
            string.IsNullOrWhiteSpace(BaselineConfigurationHash) ||
            string.IsNullOrWhiteSpace(ResolvedCandidateConfigurationHash) ||
            string.IsNullOrWhiteSpace(ExperimentLedgerId) ||
            string.IsNullOrWhiteSpace(ExperimentLedgerChecksum))
        {
            throw new ArgumentException("Indicator calibration artifact identity fields are required.");
        }
        if (!string.Equals(Scope, InstrumentScope, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Scope must be '{InstrumentScope}' - group-level calibration is deferred (blueprint §7.2, §3.2).");
        }
        if (Overrides is null || AblationOverrides is null)
            throw new ArgumentException("Overrides and AblationOverrides collections are required (may be empty).");
        foreach (CalibratedParameterOverride item in Overrides)
            item.Validate();
        if (Overrides.Select(item => item.ParameterId).Distinct(StringComparer.Ordinal).Count() != Overrides.Count)
            throw new ArgumentException("Duplicate ParameterId values in Overrides.");
        if (!Enum.IsDefined(Outcome))
            throw new ArgumentOutOfRangeException(nameof(Outcome));
        if (!Enum.IsDefined(PromotionStatus))
            throw new ArgumentOutOfRangeException(nameof(PromotionStatus));
        if (PromotionStatus == CalibrationPromotionStatus.Approved && Outcome != CalibrationOutcome.Improved)
        {
            throw new ArgumentException(
                "Only artifacts with Outcome = Improved may carry PromotionStatus = Approved (blueprint §13.3).");
        }
        ArgumentNullException.ThrowIfNull(Evidence);
        Evidence.Validate();
        if (CreatedAt > DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(CreatedAt));
    }
}
