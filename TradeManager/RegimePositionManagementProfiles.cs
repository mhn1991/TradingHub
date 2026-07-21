using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace TradeManager;

public sealed record PositionManagementProfile
{
    public required string ProfileId { get; init; }
    public required PositionManagementOptions Options { get; init; }
    public required IReadOnlyList<MarketRegime> ApplicableRegimes { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProfileId) || ApplicableRegimes is null || ApplicableRegimes.Count == 0 ||
            ApplicableRegimes.Any(regime => !Enum.IsDefined(regime)))
            throw new ArgumentException("A management profile requires an id and valid regimes.");
        Options.Validate();
    }
}

public sealed record RegimeManagementOptions
{
    public bool Enabled { get; init; }
    public IReadOnlyList<PositionManagementProfile> Profiles { get; init; } = [];

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Profiles);
        foreach (PositionManagementProfile profile in Profiles) profile.Validate();
        if (Profiles.Select(item => item.ProfileId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count)
            throw new ArgumentException("Management profile ids must be unique.");
        MarketRegime[] duplicates = Profiles.SelectMany(item => item.ApplicableRegimes)
            .GroupBy(item => item).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length > 0)
            throw new ArgumentException($"Regimes map to more than one management profile: {string.Join(",", duplicates)}.");
    }
}

public sealed class RegimeAwareStructureBasedTradeManager : IStructureBasedTradeManager, IManagementProfileResolver
{
    private readonly IStructureBasedTradeManager _fallback;
    private readonly IReadOnlyDictionary<MarketRegime, (string Id, IStructureBasedTradeManager Manager)> _profiles;

    public RegimeAwareStructureBasedTradeManager(
        PositionManagementOptions fallback,
        RegimeManagementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        fallback.Validate();
        RegimeManagementOptions resolved = options ?? new RegimeManagementOptions();
        resolved.Validate();
        _fallback = new StructureBasedTradeManager(fallback);
        IEnumerable<PositionManagementProfile> profiles = resolved.Profiles.Count == 0
            ? DefaultProfiles(fallback)
            : resolved.Profiles;
        _profiles = profiles
            .SelectMany(profile => profile.ApplicableRegimes.Select(regime => (regime, profile)))
            .ToDictionary(
                item => item.regime,
                item => (item.profile.ProfileId, (IStructureBasedTradeManager)new StructureBasedTradeManager(item.profile.Options)));
    }

    public string ResolveProfileId(MarketRegime regime) =>
        _profiles.TryGetValue(regime, out (string Id, IStructureBasedTradeManager Manager) profile)
            ? profile.Id
            : "default";

    public TradeManagementRecommendation Evaluate(ManagedTradeState trade, AnalysisSnapshot analysis) =>
        Select(analysis.MarketRegime.Regime).Evaluate(trade, analysis);

    public TradeManagementRecommendation Evaluate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        TradeManagementEvaluationScope scope,
        EquityProtectionDirective? equityProtection = null) =>
        Select(analysis.MarketRegime.Regime).Evaluate(trade, analysis, scope, equityProtection);

    private IStructureBasedTradeManager Select(MarketRegime regime) =>
        _profiles.TryGetValue(regime, out (string Id, IStructureBasedTradeManager Manager) profile)
            ? profile.Manager
            : _fallback;

    private static IReadOnlyList<PositionManagementProfile> DefaultProfiles(PositionManagementOptions basis) =>
    [
        new()
        {
            ProfileId = "trend-runner",
            ApplicableRegimes = [MarketRegime.TrendingUp, MarketRegime.TrendingDown],
            Options = basis with
            {
                MinimumRunnerFraction = Math.Max(basis.MinimumRunnerFraction, 0.50m),
                AtrBufferMultiplier = Math.Max(basis.AtrBufferMultiplier, 0.30m),
                StagnationBars = Math.Max(basis.StagnationBars, 16)
            }
        },
        new()
        {
            ProfileId = "range-early-reduction",
            ApplicableRegimes = [MarketRegime.Range],
            Options = basis with
            {
                BreakEvenActivationR = Math.Min(basis.BreakEvenActivationR, 0.75m),
                StructureTrailActivationR = Math.Max(Math.Min(basis.StructureTrailActivationR, 1.25m), Math.Min(basis.BreakEvenActivationR, 0.75m)),
                StagnationBars = Math.Min(basis.StagnationBars, 10),
                MinimumRunnerFraction = Math.Min(basis.MinimumRunnerFraction, 0.25m)
            }
        },
        new()
        {
            ProfileId = "breakout-retest",
            ApplicableRegimes = [MarketRegime.BreakoutExpansionUp, MarketRegime.BreakoutExpansionDown],
            Options = basis with
            {
                // StructureTrailActivationR must stay >= BreakEvenActivationR (Validate()'s own
                // invariant) - raising BreakEven alone breaks presets whose StructureTrailActivationR
                // starts below the new floor (e.g. StructuralDefaults' 0.3/0.3), so raise both
                // together the same way the range-early-reduction profile below already does.
                BreakEvenActivationR = Math.Max(basis.BreakEvenActivationR, 1m),
                StructureTrailActivationR = Math.Max(basis.StructureTrailActivationR, Math.Max(basis.BreakEvenActivationR, 1m)),
                MinimumStructuralTrailDistanceAtr = Math.Max(basis.MinimumStructuralTrailDistanceAtr, 0.50m)
            }
        },
        new()
        {
            ProfileId = "disorder-defensive",
            ApplicableRegimes = [MarketRegime.HighVolatilityDisorder, MarketRegime.IlliquidUnsafe],
            Options = basis with
            {
                EnableRegimeDegradationReduction = true,
                RegimeDegradationMinimumOpenProfitR = 0m,
                RegimeDegradationReductionFraction = Math.Max(basis.RegimeDegradationReductionFraction, 0.25m)
            }
        }
    ];
}
