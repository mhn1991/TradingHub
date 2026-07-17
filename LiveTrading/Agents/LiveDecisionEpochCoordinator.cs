using System.Threading.Channels;
using Brokers.Models;
using Microsoft.Extensions.Logging;

namespace LiveTrading.Agents;

public sealed record LiveDecisionEpochCoordinatorOptions
{
    public TimeSpan BarrierTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int ClosedEpochCapacity { get; init; } = 256;
    public int CompletedEpochTombstoneCapacity { get; init; } = 4_096;

    public void Validate()
    {
        if (BarrierTimeout <= TimeSpan.Zero || ClosedEpochCapacity <= 0 || CompletedEpochTombstoneCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(LiveDecisionEpochCoordinatorOptions));
    }
}

public sealed record LiveDecisionEpochBatch
{
    public required long Epoch { get; init; }
    public required DateTimeOffset EpochCloseTime { get; init; }
    public required IReadOnlyList<LiveTradeCandidate> OrderedCandidates { get; init; }
    public required IReadOnlyList<InstrumentKey> UnavailableInstruments { get; init; }
}

/// <summary>
/// Groups candidates from independently evaluated markets into one deterministic batch per
/// canonical market close. Candidate submission does not report a market as complete;
/// <see cref="NotifyEvaluated"/> is called after every eligible Agent on that market update has
/// finished, including updates on which no Agent trigger interval closed. Completed epochs retain bounded tombstones so late results cannot reopen and split a
/// candle into a second portfolio decision batch.
/// </summary>
public sealed class LiveDecisionEpochCoordinator
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<InstrumentKey> _expectedInstruments;
    private readonly IReadOnlySet<InstrumentKey> _expectedInstrumentSet;
    private readonly TimeProvider _timeProvider;
    private readonly LiveDecisionEpochCoordinatorOptions _options;
    private readonly ILogger<LiveDecisionEpochCoordinator> _logger;
    private readonly Dictionary<long, EpochGroup> _openEpochs = [];
    private readonly HashSet<long> _completedEpochs = [];
    private readonly Queue<long> _completedEpochOrder = [];
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Channel<LiveDecisionEpochBatch> _closedEpochs;

    public LiveDecisionEpochCoordinator(
        IReadOnlyList<InstrumentKey> expectedInstruments,
        TimeProvider timeProvider,
        LiveDecisionEpochCoordinatorOptions options,
        ILogger<LiveDecisionEpochCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(expectedInstruments);
        _expectedInstruments = expectedInstruments.Distinct().ToArray();
        _expectedInstrumentSet = _expectedInstruments.ToHashSet();
        if (_expectedInstruments.Count == 0)
            throw new ArgumentException("At least one expected instrument is required.", nameof(expectedInstruments));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _closedEpochs = Channel.CreateBounded<LiveDecisionEpochBatch>(new BoundedChannelOptions(_options.ClosedEpochCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<LiveDecisionEpochBatch> ClosedEpochs => _closedEpochs.Reader;

    public static long EpochFor(DateTimeOffset entryCandleCloseTime) =>
        entryCandleCloseTime.ToUnixTimeMilliseconds();

    public void SubmitCandidate(LiveTradeCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (_completedEpochs.Contains(candidate.DecisionEpoch))
            {
                _logger.LogWarning(
                    "Ignoring late candidate {DecisionId} for already-closed epoch {Epoch}.",
                    candidate.DecisionId,
                    candidate.DecisionEpoch);
                return;
            }

            EpochGroup group = GetOrCreateGroupUnsafe(candidate.DecisionEpoch);
            group.Candidates.Add(candidate);
        }
    }

    public void NotifyEvaluated(long epoch, InstrumentKey instrument)
    {
        lock (_gate)
        {
            if (!_expectedInstrumentSet.Contains(instrument))
            {
                _logger.LogWarning(
                    "Ignoring evaluation notification from unexpected instrument {Instrument} for epoch {Epoch}.",
                    instrument,
                    epoch);
                return;
            }
            if (_completedEpochs.Contains(epoch))
            {
                _logger.LogDebug(
                    "Ignoring late evaluation notification for {Instrument} in closed epoch {Epoch}.",
                    instrument,
                    epoch);
                return;
            }

            EpochGroup group = GetOrCreateGroupUnsafe(epoch);
            group.ReportedInstruments.Add(instrument);
            if (group.ReportedInstruments.SetEquals(_expectedInstrumentSet))
                group.Complete.TrySetResult();
        }
    }

    private EpochGroup GetOrCreateGroupUnsafe(long epoch)
    {
        if (_openEpochs.TryGetValue(epoch, out EpochGroup? existing))
            return existing;

        var group = new EpochGroup
        {
            Epoch = epoch,
            EpochCloseTime = DateTimeOffset.FromUnixTimeMilliseconds(epoch)
        };
        _openEpochs[epoch] = group;
        _ = RunBarrierAsync(group);
        return group;
    }

    private async Task RunBarrierAsync(EpochGroup group)
    {
        try
        {
            Task delay = Task.Delay(_options.BarrierTimeout, _timeProvider, _lifetimeCts.Token);
            await Task.WhenAny(group.Complete.Task, delay).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        LiveDecisionEpochBatch batch;
        lock (_gate)
        {
            if (!_openEpochs.Remove(group.Epoch))
                return;

            RememberCompletedEpochUnsafe(group.Epoch);
            InstrumentKey[] unavailable = _expectedInstruments
                .Where(instrument => !group.ReportedInstruments.Contains(instrument))
                .ToArray();
            LiveTradeCandidate[] ordered = group.Candidates
                .OrderBy(c => c.Instrument.Value, StringComparer.Ordinal)
                .ThenBy(c => c.StrategyId, StringComparer.Ordinal)
                .ThenBy(c => c.DecisionId, StringComparer.Ordinal)
                .ToArray();
            batch = new LiveDecisionEpochBatch
            {
                Epoch = group.Epoch,
                EpochCloseTime = group.EpochCloseTime,
                OrderedCandidates = ordered,
                UnavailableInstruments = unavailable
            };
        }

        if (batch.UnavailableInstruments.Count > 0)
        {
            _logger.LogWarning(
                "Decision epoch {Epoch} closed with {Count} unreported instrument(s): {Instruments}.",
                batch.Epoch,
                batch.UnavailableInstruments.Count,
                string.Join(", ", batch.UnavailableInstruments));
        }

        try
        {
            await _closedEpochs.Writer.WriteAsync(batch, _lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Normal during host shutdown. The epoch remains tombstoned and cannot reopen.
        }
    }

    private void RememberCompletedEpochUnsafe(long epoch)
    {
        if (!_completedEpochs.Add(epoch))
            return;
        _completedEpochOrder.Enqueue(epoch);
        while (_completedEpochOrder.Count > _options.CompletedEpochTombstoneCapacity)
            _completedEpochs.Remove(_completedEpochOrder.Dequeue());
    }

    private sealed class EpochGroup
    {
        public required long Epoch { get; init; }
        public required DateTimeOffset EpochCloseTime { get; init; }
        public List<LiveTradeCandidate> Candidates { get; } = [];
        public HashSet<InstrumentKey> ReportedInstruments { get; } = [];
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
