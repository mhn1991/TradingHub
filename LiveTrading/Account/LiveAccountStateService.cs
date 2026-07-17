using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.Agents;
using LiveTrading.MarketData;
using LiveTrading.Portfolio;
using LiveTrading.Registry;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Safety;

namespace LiveTrading.Account;

public sealed record LiveAccountStateOptions
{
    public TimeSpan MaximumSnapshotAge { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaximumQuoteAge { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan InstrumentMetadataMaximumAge { get; init; } = TimeSpan.FromHours(1);

    public void Validate()
    {
        if (MaximumSnapshotAge <= TimeSpan.Zero || MaximumQuoteAge <= TimeSpan.Zero ||
            InstrumentMetadataMaximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSnapshotAge));
        }
    }
}

public sealed record LiveAccountStateSnapshot
{
    public required AccountSnapshot Account { get; init; }
    public required IReadOnlyList<BrokerPosition> Positions { get; init; }
    public required IReadOnlyList<BrokerOrder> Orders { get; init; }
    public IReadOnlyDictionary<InstrumentKey, InstrumentTradingMetadata> InstrumentMetadata { get; init; } =
        new Dictionary<InstrumentKey, InstrumentTradingMetadata>();
    public required DateTimeOffset RefreshedAt { get; init; }
    public required bool IsStale { get; init; }
    public decimal Equity => Account.Equity ??
        (Account.Balance ?? 0m) + (Account.UnrealizedProfitLoss ?? 0m);
}

public interface ILiveAccountStateService
{
    LiveAccountStateSnapshot? Snapshot { get; }
    void ApplyQuote(LiveQuoteSnapshot quote);
    bool TryGetQuote(InstrumentKey instrument, out LiveQuoteSnapshot quote);
    Task<LiveAccountStateSnapshot> RefreshAsync(CancellationToken cancellationToken);
    LiveOpportunityEvaluationContext CreateOpportunityContext(
        IReadOnlyList<LiveTradeCandidate> candidates,
        Func<LiveTradeCandidate, LiveTradingPolicyBundle> policyResolver,
        ILiveOrderPositionRegistry registry,
        IPortfolioReservationBook reservations,
        ITradingSafetyController safety);
}

/// <summary>
/// One authoritative in-memory projection of the OANDA account plus a conversion graph built from
/// the latest executable FX quotes. The account snapshot is refreshed through REST before each
/// portfolio epoch; missing conversion paths fail closed in PositionSizer.
/// </summary>
public sealed class LiveAccountStateService(
    IBrokerClient broker,
    TimeProvider timeProvider,
    LiveAccountStateOptions options) : ILiveAccountStateService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<InstrumentKey, LiveQuoteSnapshot> _quotes = [];
    private IReadOnlyDictionary<InstrumentKey, InstrumentTradingMetadata> _instrumentMetadata =
        new Dictionary<InstrumentKey, InstrumentTradingMetadata>();
    private DateTimeOffset? _instrumentMetadataRefreshedAt;
    private LiveAccountStateSnapshot? _snapshot;

    public LiveAccountStateSnapshot? Snapshot
    {
        get
        {
            lock (_sync)
            {
                if (_snapshot is null)
                    return null;
                DateTimeOffset now = timeProvider.GetUtcNow();
                TimeSpan age = now - _snapshot.RefreshedAt;
                return _snapshot with
                {
                    IsStale = age < TimeSpan.Zero || age > options.MaximumSnapshotAge
                };
            }
        }
    }

    public void ApplyQuote(LiveQuoteSnapshot quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.Bid <= 0m || quote.Ask < quote.Bid || quote.ReceivedAt == default)
            throw new ArgumentOutOfRangeException(nameof(quote), "A live quote requires positive ordered prices and a receive timestamp.");
        lock (_sync)
        {
            _quotes[quote.Instrument] = quote;
        }
    }

    public bool TryGetQuote(InstrumentKey instrument, out LiveQuoteSnapshot quote)
    {
        lock (_sync)
        {
            if (!_quotes.TryGetValue(instrument, out LiveQuoteSnapshot? stored))
            {
                quote = null!;
                return false;
            }
            quote = WithDynamicStaleness(stored, timeProvider.GetUtcNow());
            return true;
        }
    }

    public async Task<LiveAccountStateSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task<IReadOnlyList<AccountSnapshot>> accountsTask = broker.Accounts.GetAccountsAsync(cancellationToken);
            Task<IReadOnlyList<BrokerPosition>> positionsTask = broker.Positions.GetOpenPositionsAsync(cancellationToken);
            Task<IReadOnlyList<BrokerOrder>> ordersTask = broker.Orders.GetOpenOrdersAsync(
                cancellationToken: cancellationToken);
            Task metadataTask = RefreshInstrumentMetadataIfRequiredAsync(cancellationToken);
            await Task.WhenAll(accountsTask, positionsTask, ordersTask, metadataTask).ConfigureAwait(false);
            IReadOnlyList<AccountSnapshot> accounts = await accountsTask.ConfigureAwait(false);
            AccountSnapshot account = accounts.SingleOrDefault() ?? throw new InvalidOperationException(
                "OANDA did not return exactly one authoritative account snapshot.");
            if (account.CanTrade == false)
                throw new InvalidOperationException("The OANDA account is currently marked non-tradeable.");
            var snapshot = new LiveAccountStateSnapshot
            {
                Account = account,
                Positions = await positionsTask.ConfigureAwait(false),
                Orders = await ordersTask.ConfigureAwait(false),
                InstrumentMetadata = _instrumentMetadata,
                RefreshedAt = timeProvider.GetUtcNow(),
                IsStale = false
            };
            if (snapshot.Equity <= 0m)
                throw new InvalidOperationException("The authoritative account equity is not positive.");
            lock (_sync)
            {
                _snapshot = snapshot;
            }
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public LiveOpportunityEvaluationContext CreateOpportunityContext(
        IReadOnlyList<LiveTradeCandidate> candidates,
        Func<LiveTradeCandidate, LiveTradingPolicyBundle> policyResolver,
        ILiveOrderPositionRegistry registry,
        IPortfolioReservationBook reservations,
        ITradingSafetyController safety)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(policyResolver);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(safety);
        LiveAccountStateSnapshot account = Snapshot ?? throw new InvalidOperationException(
            "The live account has not been refreshed.");
        if (account.IsStale)
            throw new InvalidOperationException("The authoritative account snapshot is stale.");

        InstrumentKey[] instruments = candidates.Select(candidate => candidate.Instrument)
            .Concat(account.Positions.Select(position => position.Instrument))
            .Distinct()
            .ToArray();
        Dictionary<InstrumentKey, decimal> conversionRates = instruments.ToDictionary(
            instrument => instrument,
            instrument => ResolveQuoteToAccountRate(instrument, account.Account.Currency));
        Dictionary<InstrumentKey, InstrumentRiskSpec> specs = instruments.ToDictionary(
            instrument => instrument,
            instrument => account.InstrumentMetadata.TryGetValue(instrument, out InstrumentTradingMetadata? metadata)
                ? new InstrumentRiskSpec
                {
                    ContractMultiplier = 1m,
                    PipSize = metadata.PipSize,
                    MarginRate = metadata.MarginRate
                }
                : InstrumentRiskSpec.ForInstrument(instrument));

        LiveRegistrySnapshot local = registry.Snapshot;
        decimal openRisk = 0m;
        var strategyRisk = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var instrumentRisk = new Dictionary<InstrumentKey, decimal>();
        var currencyRisk = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (LivePositionRecord position in local.Positions)
        {
            if (position.AveragePrice is not > 0m || position.ProtectiveStopPrice is not > 0m)
            {
                throw new InvalidOperationException(
                    $"Owned position '{position.PositionId}' lacks entry or broker-stop state; open risk cannot be calculated safely.");
            }
            if (!conversionRates.TryGetValue(position.Instrument, out decimal rate) || rate <= 0m)
            {
                throw new InvalidOperationException(
                    $"No quote-to-account conversion path exists for owned position '{position.PositionId}'.");
            }
            InstrumentRiskSpec spec = specs.GetValueOrDefault(position.Instrument, InstrumentRiskSpec.UnitNotional);
            decimal risk = spec.EstimateStopLossAccountCurrency(
                Math.Abs(position.AveragePrice.Value - position.ProtectiveStopPrice.Value),
                position.Quantity,
                rate);
            openRisk += risk;
            strategyRisk[position.StrategyId] = strategyRisk.GetValueOrDefault(position.StrategyId) + risk;
            instrumentRisk[position.Instrument] = instrumentRisk.GetValueOrDefault(position.Instrument) + risk;
            try
            {
                (string baseCurrency, string quoteCurrency) = CurrencyExposureCalculator.ParseCurrencies(
                    position.Instrument);
                currencyRisk[baseCurrency] = currencyRisk.GetValueOrDefault(baseCurrency) + risk / 2m;
                currencyRisk[quoteCurrency] = currencyRisk.GetValueOrDefault(quoteCurrency) + risk / 2m;
            }
            catch (ArgumentException)
            {
                // Unknown instruments remain covered by total/instrument heat even without a currency split.
            }
        }

        TradingSafetySnapshot safetySnapshot = safety.Snapshot;
        decimal equityMultiplier = Math.Clamp(
            safetySnapshot.EquityProtection.CurrentRiskMultiplier,
            0m,
            1m);
        return new LiveOpportunityEvaluationContext
        {
            Portfolio = new LivePortfolioSnapshot
            {
                Account = account.Account,
                Equity = account.Equity,
                Positions = account.Positions,
                Orders = account.Orders,
                Reservations = reservations.Snapshot,
                CurrentOpenRiskAccountCurrency = openRisk,
                OpenStrategyRisk = strategyRisk,
                OpenInstrumentRisk = instrumentRisk,
                OpenCurrencyRisk = currencyRisk
            },
            PolicyResolver = policyResolver,
            QuoteToAccountCurrencyRates = conversionRates,
            InstrumentRiskSpecs = specs,
            InstrumentMetadata = account.InstrumentMetadata,
            RequireInstrumentMetadata = broker is IInstrumentMetadataBrokerClient,
            DrawdownPercent = safetySnapshot.EquityProtection.DrawdownPercent,
            EquityProtectionMultipliers = candidates.Select(candidate => candidate.Instrument)
                .Distinct()
                .ToDictionary(instrument => instrument, _ => equityMultiplier)
        };
    }

    private async Task RefreshInstrumentMetadataIfRequiredAsync(CancellationToken cancellationToken)
    {
        if (broker is not IInstrumentMetadataBrokerClient metadataBroker)
            return;
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (_instrumentMetadataRefreshedAt is DateTimeOffset refreshed &&
            now - refreshed <= options.InstrumentMetadataMaximumAge)
        {
            return;
        }
        IReadOnlyList<InstrumentTradingMetadata> metadata = await metadataBroker.InstrumentMetadata
            .GetInstrumentMetadataAsync(cancellationToken).ConfigureAwait(false);
        var validated = new Dictionary<InstrumentKey, InstrumentTradingMetadata>();
        foreach (InstrumentTradingMetadata item in metadata)
        {
            item.Validate();
            validated[item.Instrument] = item;
        }
        _instrumentMetadata = validated;
        _instrumentMetadataRefreshedAt = now;
    }

    private decimal ResolveQuoteToAccountRate(InstrumentKey instrument, string? accountCurrency)
    {
        if (string.IsNullOrWhiteSpace(accountCurrency) || !TryParseFx(instrument, out _, out string quoteCurrency))
            return 0m;
        if (string.Equals(quoteCurrency, accountCurrency, StringComparison.OrdinalIgnoreCase))
            return 1m;

        Dictionary<string, List<(string Currency, decimal Rate)>> graph = BuildCurrencyGraph();
        var queue = new Queue<(string Currency, decimal Rate, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { quoteCurrency };
        queue.Enqueue((quoteCurrency, 1m, 0));
        while (queue.Count > 0)
        {
            (string currency, decimal rate, int depth) = queue.Dequeue();
            if (string.Equals(currency, accountCurrency, StringComparison.OrdinalIgnoreCase))
                return rate;
            if (depth >= 3 || !graph.TryGetValue(currency, out List<(string Currency, decimal Rate)>? edges))
                continue;
            foreach ((string next, decimal edgeRate) in edges)
            {
                if (edgeRate <= 0m || !visited.Add(next))
                    continue;
                queue.Enqueue((next, rate * edgeRate, depth + 1));
            }
        }
        return 0m;
    }

    private Dictionary<string, List<(string Currency, decimal Rate)>> BuildCurrencyGraph()
    {
        var graph = new Dictionary<string, List<(string Currency, decimal Rate)>>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (_sync)
        {
            foreach ((InstrumentKey instrument, LiveQuoteSnapshot stored) in _quotes)
            {
                LiveQuoteSnapshot quote = WithDynamicStaleness(stored, now);
                if (quote.IsStale || !quote.IsTradeable || quote.Mid <= 0m ||
                    !TryParseFx(instrument, out string baseCurrency, out string quoteCurrency))
                {
                    continue;
                }
                AddEdge(graph, baseCurrency, quoteCurrency, quote.Mid);
                AddEdge(graph, quoteCurrency, baseCurrency, 1m / quote.Mid);
            }
        }
        return graph;
    }

    private LiveQuoteSnapshot WithDynamicStaleness(LiveQuoteSnapshot quote, DateTimeOffset now)
    {
        bool outsideClockWindow = quote.ReceivedAt > now || now - quote.ReceivedAt > options.MaximumQuoteAge;
        return quote with { IsStale = quote.IsStale || outsideClockWindow };
    }

    private static void AddEdge(
        IDictionary<string, List<(string Currency, decimal Rate)>> graph,
        string from,
        string to,
        decimal rate)
    {
        if (!graph.TryGetValue(from, out List<(string Currency, decimal Rate)>? edges))
        {
            edges = [];
            graph[from] = edges;
        }
        edges.Add((to, rate));
    }

    private static bool TryParseFx(InstrumentKey instrument, out string baseCurrency, out string quoteCurrency)
    {
        string value = instrument.Value;
        int colon = value.IndexOf(':');
        string symbol = colon >= 0 ? value[(colon + 1)..] : value;
        string[] parts = symbol.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && parts[0].Length == 3 && parts[1].Length == 3)
        {
            baseCurrency = parts[0].ToUpperInvariant();
            quoteCurrency = parts[1].ToUpperInvariant();
            return true;
        }
        baseCurrency = string.Empty;
        quoteCurrency = string.Empty;
        return false;
    }
}
