using System.IO.Compression;
using System.Text.Json;
using Brokers.Abstractions;
using Brokers.Models;
using Brokers.Oanda;
using DBManager.Abstractions.Credentials;
using DBManager.Postgres.Security;
using Simulator.MarketData;

namespace TradingClassifierRunner;

/// <summary>
/// Downloads OANDA history for the classifier, using the repo's existing paging source and its
/// encrypted database credential vault.
/// <para>
/// The token is never read into this process's configuration or printed: the vault hands back a
/// credential, it goes straight into <see cref="OandaOptions"/>, and the only thing written to disk
/// is candles.
/// </para>
/// <para>
/// Output is written to a plain file rather than into <c>.cache/historical</c>. That cache carries
/// a validated manifest and content hash owned by <see cref="StreamingCandleCache"/>; writing a
/// look-alike entry by hand would leave a half-valid record that the backtester might later trust.
/// </para>
/// </summary>
public static class CandleFetcher
{
    public static async Task<int> FetchAsync(
        string instrumentKey,
        string intervalText,
        DateTimeOffset from,
        DateTimeOffset to,
        string outputPath,
        bool live,
        CancellationToken cancellationToken)
    {
        InstrumentKey instrument = new(instrumentKey);
        BarInterval interval = BarIntervalParser.Parse(intervalText);
        BrokerEnvironment environment = live ? BrokerEnvironment.Live : BrokerEnvironment.Demo;

        BrokerCredentialDatabaseConfiguration? configuration =
            BrokerCredentialDatabaseConfiguration.TryLoad(Directory.GetCurrentDirectory(), out string repositoryRoot);
        if (configuration is null)
        {
            Console.Error.WriteLine("No broker-credential database configuration was found from the current directory.");
            return 1;
        }

        IBrokerCredentialStore store = configuration.OpenStore(repositoryRoot);
        BrokerCredential? credential = await store
            .GetAsync("OANDA", live ? "LIVE" : "DEMO", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            Console.Error.WriteLine($"No enabled OANDA {(live ? "LIVE" : "DEMO")} credential is stored in the vault.");
            return 1;
        }

        Console.WriteLine($"Fetching {instrument.Value} {intervalText} {from:yyyy-MM-dd} -> {to:yyyy-MM-dd} " +
            $"from OANDA {(live ? "live" : "practice")}...");

        await using OandaBrokerClient client = new(new OandaOptions
        {
            AccessToken = credential.AccessToken!,
            AccountId = credential.AccountId ?? string.Empty,
            Environment = environment
        });

        OandaHistoricalCandleSource source = new(client.MarketData, instrument, interval, from, to);

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        long written = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;

        await using (FileStream file = File.Create(outputPath))
        await using (GZipStream gzip = new(file, CompressionLevel.Optimal))
        await using (StreamWriter writer = new(gzip))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                instrument = instrument.Value,
                interval = intervalText,
                from,
                to
            })).ConfigureAwait(false);

            await foreach (Candle candle in source.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // closeTime is what the classifier stamps rows with; OpenTime + interval is the
                // same thing and is always populated, unlike Candle.CloseTime.
                DateTimeOffset closeTime = candle.CloseTime ?? interval.AddTo(candle.OpenTime);
                first ??= closeTime;
                last = closeTime;

                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    openTime = candle.OpenTime,
                    closeTime,
                    open = candle.Prices.Open,
                    high = candle.Prices.High,
                    low = candle.Prices.Low,
                    close = candle.Prices.Close
                })).ConfigureAwait(false);

                written++;
                if (written % 20_000 == 0)
                    Console.WriteLine($"  {written:N0} candles... ({closeTime:yyyy-MM-dd})");
            }
        }

        Console.WriteLine($"Wrote {written:N0} candles to {outputPath}");
        if (first is not null)
            Console.WriteLine($"  range {first:yyyy-MM-dd} -> {last:yyyy-MM-dd}");
        return written > 0 ? 0 : 1;
    }
}
