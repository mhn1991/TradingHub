using TradingHub.Application.Orders;
using TradingHub.Application.Risk;
using TradingHub.Application.Strategies;
using TradingHub.Application.Trading;
using TradingHub.Domain.Markets;
using TradingHub.Simulation.Broker;
using TradingHub.Simulation.Engine;
using TradingHub.Simulation.Execution;
using TradingHub.Simulation.MarketData;
using TradingHub.Simulation.Results;
using TradingHub.Simulation.Runner.Configuration;
using TradingHub.Simulation.Time;

namespace TradingHub.Simulation.Runner;

internal static class SimulationCompositionRoot
{
    public static SimulationRuntime Build(LoadedSimulationConfiguration loaded)
    {
        var config = loaded.Value;
        var instrument = config.Instrument.ToDomain();
        var instruments = new Dictionary<string, InstrumentDefinition>(StringComparer.Ordinal)
        {
            [instrument.Id] = instrument
        };
        var clock = new SimulationClock();
        var broker = CreateBroker(config, clock, instruments);
        var session = CreateSession(config, clock, broker, instruments);
        var engine = new SimulationEngine(new SimulationEventQueue(clock), session, broker);

        return new SimulationRuntime
        {
            RunId = config.RunId,
            OutputDirectory = ResolveOutputDirectory(loaded),
            Engine = engine,
            MarketDataSource = CreateMarketDataSource(loaded),
            ResultWriter = new SimulationResultWriter()
        };
    }

    private static SimulatedBroker CreateBroker(
        SimulationConfigurationDto config,
        SimulationClock clock,
        IReadOnlyDictionary<string, InstrumentDefinition> instruments)
    {
        var brokerOptions = new SimulatedBrokerOptions
        {
            BrokerId = "historical-simulator",
            AccountId = config.Account.Id,
            AccountCurrency = config.Account.Currency,
            InitialBalance = config.Account.InitialBalance,
            OutboundLatency = TimeSpan.FromMilliseconds(config.Execution.OutboundLatencyMilliseconds)
        };
        var executionOptions = new ExecutionModelOptions
        {
            SlippageBps = config.Execution.SlippageBps,
            CommissionBps = config.Execution.CommissionBps,
            MaximumFillQuantityPerBar = config.Execution.MaximumFillQuantityPerBar
        };
        return new SimulatedBroker(brokerOptions, clock, instruments, executionOptions);
    }

    private static TradingSession CreateSession(
        SimulationConfigurationDto config,
        SimulationClock clock,
        SimulatedBroker broker,
        IReadOnlyDictionary<string, InstrumentDefinition> instruments)
    {
        var strategy = new MovingAverageCrossStrategy(new MovingAverageCrossOptions
        {
            StrategyId = config.Strategy.Id,
            ShortPeriod = config.Strategy.ShortPeriod,
            LongPeriod = config.Strategy.LongPeriod,
            OrderQuantity = config.Strategy.OrderQuantity
        });
        var riskEngine = new RiskEngine(new RiskLimits
        {
            MaximumOrderQuantity = config.Risk.MaximumOrderQuantity,
            MaximumAbsolutePosition = config.Risk.MaximumAbsolutePosition,
            MaximumSpreadBps = config.Risk.MaximumSpreadBps,
            MaximumOpenOrders = config.Risk.MaximumOpenOrders,
            AllowShortSelling = config.Risk.AllowShortSelling
        });
        var options = new TradingSessionOptions
        {
            AccountId = config.Account.Id,
            AccountCurrency = config.Account.Currency,
            InitialBalance = config.Account.InitialBalance,
            Instruments = instruments
        };
        var services = new TradingSessionServices
        {
            Clock = clock,
            Broker = broker,
            RiskEngine = riskEngine,
            OrderFactory = new OrderFactory(),
            Strategies = [strategy]
        };
        return new TradingSession(options, services);
    }

    private static CsvHistoricalBarSource CreateMarketDataSource(LoadedSimulationConfiguration loaded)
    {
        var config = loaded.Value;
        return new CsvHistoricalBarSource(new CsvHistoricalBarOptions
        {
            FilePath = ResolvePath(loaded.BaseDirectory, config.Data.File),
            InstrumentId = config.Instrument.Id,
            Period = TimeSpan.FromMinutes(config.Data.PeriodMinutes)
        });
    }

    private static string ResolveOutputDirectory(LoadedSimulationConfiguration loaded)
    {
        var root = ResolvePath(loaded.BaseDirectory, loaded.Value.Output.Directory);
        return Path.Combine(root, loaded.Value.RunId);
    }

    private static string ResolvePath(string baseDirectory, string configuredPath)
    {
        return Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(baseDirectory, configuredPath));
    }
}
