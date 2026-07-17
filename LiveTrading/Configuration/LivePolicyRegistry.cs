using Brokers.Models;
using LiveTrading.Agents;
using TradeManager;

namespace LiveTrading.Configuration;

/// <summary>
/// Gives the portfolio, management, and API layers the exact policy revision assigned to each
/// instrument/strategy Agent. <see cref="Register"/> (the startup path) still requires duplicate
/// registrations to be byte-for-byte equivalent - a host never silently overwrites its initial
/// wiring. <see cref="Activate"/> is the separate, explicit hot-swap path (see
/// <c>LiveTradingRuntimeCoordinator.ActivatePolicyRevisionAsync</c>, which is the only intended
/// caller - it holds the coordinator's serial epoch lock so a swap can only land strictly between
/// two decision epochs): it may change what <see cref="Resolve(string, InstrumentKey)"/>/<see cref="ResolveMode"/>/
/// <see cref="ResolveManagement"/> return for a key going forward, but every prior revision stays
/// permanently retrievable via <see cref="TryResolveManagementByRevision"/> so an already-open
/// position (which remembers the exact <c>PolicyBundleId</c>/<c>Revision</c> it was opened under -
/// see <c>LivePositionManagementService</c>) keeps being managed under its own original policy
/// rather than the newly-activated one.
/// </summary>
public interface ILivePolicyRegistry
{
    void Register(
        AgentInstanceKey key,
        LiveTradingPolicyBundle policy,
        StrategyActivationMode mode,
        TradeManagementCalibration? managementCalibration = null,
        TradeManagementCalibrationOptions? managementCalibrationOptions = null);

    /// <summary>
    /// Activates a new (or updated) policy revision as the "current" one for <paramref name="key"/>.
    /// New candidates for this key resolve against it from this call onward; positions already
    /// open under an earlier revision are unaffected - that revision remains permanently
    /// resolvable via <see cref="TryResolveManagementByRevision"/>.
    /// </summary>
    void Activate(AgentInstanceKey key, LivePolicyRegistration registration);

    LiveTradingPolicyBundle Resolve(LiveTradeCandidate candidate);
    LiveTradingPolicyBundle Resolve(string strategyId, InstrumentKey instrument);
    StrategyActivationMode ResolveMode(string strategyId, InstrumentKey instrument);
    LiveManagementPolicy ResolveManagement(string strategyId, InstrumentKey instrument);

    /// <summary>
    /// Looks up the exact policy revision a specific already-open position was created under,
    /// regardless of whatever is currently "active" for that strategy/instrument. Returns null
    /// (never throws) if that specific revision was never registered/activated - a genuine
    /// orphan/corruption case the caller should treat as unrecoverable, not a routine miss.
    /// </summary>
    LiveManagementPolicy? TryResolveManagementByRevision(
        string strategyId, InstrumentKey instrument, Guid policyBundleId, int revision);

    IReadOnlyList<LivePolicyRegistration> Snapshot { get; }
}

public sealed record LivePolicyRegistration
{
    public required AgentInstanceKey Key { get; init; }
    public required LiveTradingPolicyBundle Policy { get; init; }
    public required StrategyActivationMode Mode { get; init; }
    public TradeManagementCalibration? ManagementCalibration { get; init; }
    public TradeManagementCalibrationOptions ManagementCalibrationOptions { get; init; } = new();
}

public sealed record LiveManagementPolicy
{
    public required LiveTradingPolicyBundle Policy { get; init; }
    public TradeManagementCalibration? Calibration { get; init; }
    public required TradeManagementCalibrationOptions CalibrationOptions { get; init; }
}

public sealed class LivePolicyRegistry : ILivePolicyRegistry
{
    private readonly string _deploymentId;
    private readonly object _sync = new();

    /// <summary>
    /// "Current" resolution is keyed by the deployed strategy slot (deployment + instrument +
    /// StrategyId) - deliberately narrower than the full, revision-widened
    /// <see cref="AgentInstanceKey"/>. Hot-swap replaces what a slot's *new* candidates resolve to;
    /// it is a distinct concept from "does this exact Agent instance/revision already have its own
    /// entry in <c>AgentSupervisor._instances</c>" - collapsing the two would break
    /// <c>LivePositionManagementService</c>'s existing current-vs-pinned-revision mismatch
    /// detection, which depends on "current" being answerable without already knowing which
    /// revision is active.
    /// </summary>
    private readonly Dictionary<(string DeploymentId, InstrumentKey Instrument, string StrategyId), LivePolicyRegistration> _current = [];

    /// <summary>
    /// Every registered/activated revision, permanently retrievable by its own exact identity.
    /// Keyed directly by <see cref="AgentInstanceKey"/> now that the key itself carries
    /// <c>PolicyBundleId</c>/<c>Revision</c> - previously a separate 3-tuple wrapping a
    /// then-narrower key plus those same two fields redundantly.
    /// </summary>
    private readonly Dictionary<AgentInstanceKey, LivePolicyRegistration> _history = [];

    public LivePolicyRegistry(string? deploymentId = null)
    {
        _deploymentId = string.IsNullOrWhiteSpace(deploymentId) ? AgentInstanceKey.DefaultDeploymentId : deploymentId;
    }

    public void Register(
        AgentInstanceKey key,
        LiveTradingPolicyBundle policy,
        StrategyActivationMode mode,
        TradeManagementCalibration? managementCalibration = null,
        TradeManagementCalibrationOptions? managementCalibrationOptions = null)
    {
        LivePolicyRegistration registration = Validate(policy, mode, managementCalibration, managementCalibrationOptions, key);
        var slot = (key.DeploymentId, key.Instrument, key.StrategyId);
        lock (_sync)
        {
            if (_current.TryGetValue(slot, out LivePolicyRegistration? existing))
            {
                if (existing.Policy.ConfigurationHash != policy.ConfigurationHash || existing.Mode != mode)
                {
                    throw new InvalidOperationException(
                        $"Policy for {key.StrategyId}/{key.Instrument} is already registered with a different revision or mode.");
                }
                return;
            }
            _current.Add(slot, registration);
            _history[key] = registration;
        }
    }

    public void Activate(AgentInstanceKey key, LivePolicyRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.Key != key)
            throw new ArgumentException("Registration key must match the activation key.", nameof(registration));
        Validate(registration.Policy, registration.Mode, registration.ManagementCalibration, registration.ManagementCalibrationOptions, key);
        lock (_sync)
        {
            _current[(key.DeploymentId, key.Instrument, key.StrategyId)] = registration;
            _history[key] = registration;
        }
    }

    public LiveTradingPolicyBundle Resolve(LiveTradeCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Resolve(candidate.StrategyId, candidate.Instrument);
    }

    public LiveTradingPolicyBundle Resolve(string strategyId, InstrumentKey instrument) =>
        RequireCurrent(strategyId, instrument).Policy;

    public StrategyActivationMode ResolveMode(string strategyId, InstrumentKey instrument) =>
        RequireCurrent(strategyId, instrument).Mode;

    public LiveManagementPolicy ResolveManagement(string strategyId, InstrumentKey instrument)
    {
        LivePolicyRegistration item = RequireCurrent(strategyId, instrument);
        return new LiveManagementPolicy
        {
            Policy = item.Policy,
            Calibration = item.ManagementCalibration,
            CalibrationOptions = item.ManagementCalibrationOptions
        };
    }

    public LiveManagementPolicy? TryResolveManagementByRevision(
        string strategyId, InstrumentKey instrument, Guid policyBundleId, int revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        lock (_sync)
        {
            var key = new AgentInstanceKey(_deploymentId, instrument, strategyId, policyBundleId, revision);
            if (!_history.TryGetValue(key, out LivePolicyRegistration? item))
            {
                return null;
            }
            return new LiveManagementPolicy
            {
                Policy = item.Policy,
                Calibration = item.ManagementCalibration,
                CalibrationOptions = item.ManagementCalibrationOptions
            };
        }
    }

    public IReadOnlyList<LivePolicyRegistration> Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _current.Values
                    .OrderBy(item => item.Key.Instrument.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.Key.StrategyId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    private LivePolicyRegistration RequireCurrent(string strategyId, InstrumentKey instrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        lock (_sync)
        {
            return _current.TryGetValue((_deploymentId, instrument, strategyId), out LivePolicyRegistration? item)
                ? item
                : throw new KeyNotFoundException($"No active policy is registered for {strategyId}/{instrument}.");
        }
    }

    private static LivePolicyRegistration Validate(
        LiveTradingPolicyBundle policy,
        StrategyActivationMode mode,
        TradeManagementCalibration? managementCalibration,
        TradeManagementCalibrationOptions? managementCalibrationOptions,
        AgentInstanceKey key)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        TradeManagementCalibrationOptions calibrationOptions = managementCalibrationOptions ?? new();
        calibrationOptions.Validate();
        if (calibrationOptions.Enabled && managementCalibration is null)
            throw new InvalidOperationException("Enabled management calibration requires an artifact.");
        managementCalibration?.Validate();
        return new LivePolicyRegistration
        {
            Key = key,
            Policy = policy,
            Mode = mode,
            ManagementCalibration = managementCalibration,
            ManagementCalibrationOptions = calibrationOptions
        };
    }
}
