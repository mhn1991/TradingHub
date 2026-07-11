using System.Globalization;
using System.Text.Json;
using Networking;

namespace Brokers;

internal sealed class BinanceBrokerAdapter : IBrokerProviderAdapter
{
    private readonly BrokerRuntimeConfiguration _runtime;
    private readonly TimeProvider _timeProvider;

    public BinanceBrokerAdapter(BrokerRuntimeConfiguration runtime, TimeProvider timeProvider)
    {
        _runtime = runtime;
        _timeProvider = timeProvider;
    }

    public BrokerProvider Provider => BrokerProvider.BinanceSpot;

    public CandlePriceBasis PriceBasis => _runtime.PriceBasis;

    public BrokerResult<HttpNetworkRequest> CreateCandleRequest(
        CandleQuery query,
        BrokerInstrumentConfiguration instrument,
        int limit)
    {
        if (!TryMapTimeframe(query.Timeframe, out string? interval))
        {
            return BrokerResult<HttpNetworkRequest>.Failure(new BrokerError(
                BrokerErrorKind.Unsupported,
                $"Binance Spot does not support the canonical timeframe '{query.Timeframe}'."));
        }

        var uriBuilder = new BrokerUriBuilder(_runtime.RestEndpoint, "api/v3/klines")
            .Add("symbol", instrument.ProviderSymbol)
            .Add("interval", interval!)
            .Add("timeZone", "0")
            .Add("limit", limit);

        if (query.StartTime is { } startTime)
        {
            uriBuilder.AddUnixMilliseconds("startTime", startTime);
        }

        if (query.EndTime is { } endTime)
        {
            // Binance treats endTime as inclusive; the canonical query uses an exclusive upper bound.
            uriBuilder.AddUnixMilliseconds("endTime", endTime.AddMilliseconds(-1));
        }

        var request = new HttpNetworkRequest(
            NetworkProtocol.Https,
            uriBuilder.Build(),
            HttpMethod.Get,
            timeout: _runtime.RequestTimeout);

        return BrokerResult<HttpNetworkRequest>.Success(request);
    }

    public CandleBatch ParseCandles(
        CandleQuery query,
        BrokerInstrumentConfiguration instrument,
        int limit,
        HttpNetworkResponse response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new BrokerPayloadException("Binance returned a candle payload that is not an array.");
            }

            var candles = new List<Candle>();
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (candles.Count == limit)
                {
                    break;
                }

                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 9)
                {
                    throw new BrokerPayloadException("Binance returned a malformed kline entry.");
                }

                long openMilliseconds = item[0].GetInt64();
                long inclusiveCloseMilliseconds = item[6].GetInt64();
                DateTimeOffset openTime = DateTimeOffset.FromUnixTimeMilliseconds(openMilliseconds);
                DateTimeOffset closeTime = DateTimeOffset.FromUnixTimeMilliseconds(
                    checked(inclusiveCloseMilliseconds + 1));
                bool isComplete = closeTime <= _timeProvider.GetUtcNow();

                if (!IsWithinRequestedRange(query, openTime))
                {
                    continue;
                }

                if (!query.IncludeIncomplete && !isComplete)
                {
                    continue;
                }

                candles.Add(new Candle(
                    query.InstrumentId,
                    query.Timeframe,
                    PriceBasis,
                    openTime,
                    closeTime,
                    ReadDecimal(item[1], "open"),
                    ReadDecimal(item[2], "high"),
                    ReadDecimal(item[3], "low"),
                    ReadDecimal(item[4], "close"),
                    isComplete,
                    baseAssetVolume: ReadDecimal(item[5], "base volume"),
                    quoteAssetVolume: ReadDecimal(item[7], "quote volume"),
                    tradeCount: item[8].GetInt64()));
            }

            return new CandleBatch(
                _runtime.Configuration.Id,
                Provider,
                query.InstrumentId,
                instrument.ProviderSymbol,
                query.Timeframe,
                PriceBasis,
                candles,
                BrokerHttpErrorMapper.GetRequestId(response),
                response.Elapsed);
        }
        catch (BrokerPayloadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException or
            FormatException or
            OverflowException or
            InvalidOperationException or
            ArgumentException)
        {
            throw new BrokerPayloadException("Binance returned an invalid candle payload.", exception);
        }
    }

    public BrokerError ParseError(HttpNetworkResponse response) =>
        BrokerHttpErrorMapper.Map(Provider, response);

    private static decimal ReadDecimal(JsonElement value, string fieldName)
    {
        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        if (text is null ||
            !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal result))
        {
            throw new BrokerPayloadException($"Binance returned an invalid {fieldName} value.");
        }

        return result;
    }

    private static bool TryMapTimeframe(CandleTimeframe timeframe, out string? value)
    {
        value = (timeframe.Unit, timeframe.Value) switch
        {
            (CandleTimeframeUnit.Second, 1) => "1s",
            (CandleTimeframeUnit.Minute, 1) => "1m",
            (CandleTimeframeUnit.Minute, 3) => "3m",
            (CandleTimeframeUnit.Minute, 5) => "5m",
            (CandleTimeframeUnit.Minute, 15) => "15m",
            (CandleTimeframeUnit.Minute, 30) => "30m",
            (CandleTimeframeUnit.Hour, 1) => "1h",
            (CandleTimeframeUnit.Hour, 2) => "2h",
            (CandleTimeframeUnit.Hour, 4) => "4h",
            (CandleTimeframeUnit.Hour, 6) => "6h",
            (CandleTimeframeUnit.Hour, 8) => "8h",
            (CandleTimeframeUnit.Hour, 12) => "12h",
            (CandleTimeframeUnit.Day, 1) => "1d",
            (CandleTimeframeUnit.Day, 3) => "3d",
            (CandleTimeframeUnit.Week, 1) => "1w",
            (CandleTimeframeUnit.Month, 1) => "1M",
            _ => null
        };

        return value is not null;
    }

    private static bool IsWithinRequestedRange(CandleQuery query, DateTimeOffset openTime) =>
        (query.StartTime is null || openTime >= query.StartTime.Value) &&
        (query.EndTime is null || openTime < query.EndTime.Value);
}
