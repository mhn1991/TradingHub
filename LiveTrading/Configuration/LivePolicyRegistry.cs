using Brokers.Models;
using LiveTrading.Agents;
using TradeManager;
using TradingCore.Pipeline;

namespace LiveTrading.Configuration;

/// <summary>
/// Gives the portfolio, management, and API layers the exact policy revision assigned to each
/// instrument/strategy Agent. <see cref="Register"/> (the startup path) keeps every exact Agent
/// revision and only treats a conflicting re-registration of that same exact identity as an
/// error. <see cref="Activate"/> is the separate, explicit hot-swap path (see
/// <c>LiveTradingRuntimeCoordinator.ActivatePolicyRevisionAsync</c>, which is the only intended
/// caller - it holds the coordinator's serial epoch lock so a swap can only land strictly between
/// two decision epochs): it may change what <see cref="Resolve(string, InstrumentKey)"/>/<see cref="ResolveMode(string, InstrumentKey)"/>/
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
    /// Makes an immutable revision available for exact candidate and open-position lookups without
    /// changing the revision used by current-slot resolution. This is the preparation half of a
    /// controlled hot-swap; <see cref="Activate"/> is the commit half.
    /// </summary>
    void RegisterHistorical(AgentInstanceKey key, LivePolicyRegistration registration);

    /// <summary>
    /// Activates a new (or updated) policy revision as the "current" one for <paramref name="key"/>.
    /// New candidates for this key resolve against it from this call onward; positions already
    /// open under an earlier revision are unaffected - that revision remains permanently
    /// resolvable via <see cref="TryResolveManagementByRevision"/>.
    /// </summary>
    void Activate(AgentInstanceKey key, LivePolicyRegistration registration);

    LiveTradingPolicyBundle Resolve(LiveTradeCandidate candidate);
    StrategyActivationMode ResolveMode(LiveTradeCandidate candidate);
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
            if (_history.TryGetValue(key, out LivePolicyRegistration? exact))
            {
                if (exact.Policy.ConfigurationHash != policy.ConfigurationHash || exact.Mode != mode)
                {
                    throw new InvalidOperationException(
                        $"Agent instance {key} is already registered with different policy content or mode.");
                }
                return;
            }

            _history.Add(key, registration);
            if (!_current.TryGetValue(slot, out LivePolicyRegistration? existing))
            {
                _current.Add(slot, registration);
            }
            else
            {
                _current[slot] = SelectCurrent(existing, registration);
            }
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

    public void RegisterHistorical(AgentInstanceKey key, LivePolicyRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.Key != key)
            throw new ArgumentException("Registration key must match the historical key.", nameof(registration));
        Validate(registration.Policy, registration.Mode, registration.ManagementCalibration,
            registration.ManagementCalibrationOptions, key);
        lock (_sync)
        {
            if (_history.TryGetValue(key, out LivePolicyRegistration? existing))
            {
                if (existing.Policy.ConfigurationHash != registration.Policy.ConfigurationHash
                    || existing.Mode != registration.Mode)
                {
                    throw new InvalidOperationException(
                        $"Agent instance {key} is already registered with different policy content or mode.");
                }
                return;
            }

            _history.Add(key, registration);
        }
    }

    public LiveTradingPolicyBundle Resolve(LiveTradeCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.AgentInstance is null
            ? Resolve(candidate.StrategyId, candidate.Instrument)
            : RequireExact(candidate).Policy;
    }

    public StrategyActivationMode ResolveMode(LiveTradeCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.AgentInstance is null
            ? ResolveMode(candidate.StrategyId, candidate.Instrument)
            : RequireExact(candidate).Mode;
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
            LivePolicyRegistration[] matches = _history.Values.Where(item =>
                    item.Key.Instrument == instrument
                    && string.Equals(item.Key.StrategyId, strategyId, StringComparison.Ordinal)
                    && item.Key.PolicyBundleId == policyBundleId
                    && item.Key.Revision == revision)
                .ToArray();
            if (matches.Length == 0)
                return null;
            LivePolicyRegistration item = matches[0];
            if (matches.Any(candidate => candidate.Policy.ConfigurationHash != item.Policy.ConfigurationHash))
                throw new InvalidOperationException("Conflicting policy content exists for the pinned revision.");
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
                return _history.Values
                    .OrderBy(item => item.Key.Instrument.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.Key.StrategyId, StringComparer.Ordinal)
                    .ThenBy(item => item.Key.Revision)
                    .ThenBy(item => item.Key.PolicyBundleId)
                    .ToArray();
            }
        }
    }

    private LivePolicyRegistration RequireCurrent(string strategyId, InstrumentKey instrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        lock (_sync)
        {
            if (_current.TryGetValue((_deploymentId, instrument, strategyId), out LivePolicyRegistration? item))
                return item;
            LivePolicyRegistration[] matches = _current.Values.Where(candidate =>
                    candidate.Key.Instrument == instrument
                    && string.Equals(candidate.Key.StrategyId, strategyId, StringComparison.Ordinal))
                .ToArray();
            return matches.Length switch
            {
                1 => matches[0],
                0 => throw new KeyNotFoundException($"No active policy is registered for {strategyId}/{instrument}."),
                _ => throw new InvalidOperationException(
                    $"More than one deployment owns the current policy slot for {strategyId}/{instrument}.")
            };
        }
    }

    private LivePolicyRegistration RequireExact(LiveTradeCandidate candidate)
    {
        AgentInstanceKey key = candidate.AgentInstance!;
        if (key.Instrument != candidate.Instrument ||
            !string.Equals(key.StrategyId, candidate.StrategyId, StringComparison.Ordinal) ||
            candidate.PolicyBundleId != key.PolicyBundleId ||
            candidate.PolicyRevision != key.Revision)
        {
            throw new InvalidOperationException(
                $"Candidate {candidate.CandidateId} has inconsistent agent/policy lineage.");
        }

        lock (_sync)
        {
            return _history.TryGetValue(key, out LivePolicyRegistration? registration)
                ? registration
                : throw new KeyNotFoundException($"No exact policy is registered for agent instance {key}.");
        }
    }

    private static LivePolicyRegistration SelectCurrent(
        LivePolicyRegistration existing,
        LivePolicyRegistration candidate)
    {
        bool existingExecutable = existing.Mode is StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic;
        bool candidateExecutable = candidate.Mode is StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic;
        if (existingExecutable && candidateExecutable)
        {
            throw new InvalidOperationException(
                $"Executable policy for {candidate.Key.StrategyId}/{candidate.Key.Instrument} is already registered.");
        }
        if (existingExecutable)
            return existing;
        if (candidateExecutable)
            return candidate;

        int revision = candidate.Key.Revision.CompareTo(existing.Key.Revision);
        if (revision != 0)
            return revision > 0 ? candidate : existing;
        int mode = candidate.Mode.CompareTo(existing.Mode);
        if (mode != 0)
            return mode > 0 ? candidate : existing;
        return candidate.Key.PolicyBundleId.CompareTo(existing.Key.PolicyBundleId) > 0
            ? candidate
            : existing;
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
