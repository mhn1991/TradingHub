using System.Globalization;
using System.Text.Json;
using Networking;

namespace Brokers;

internal sealed class OandaBrokerAdapter : IBrokerProviderAdapter
{
    private readonly BrokerRuntimeConfiguration _runtime;

    public OandaBrokerAdapter(BrokerRuntimeConfiguration runtime)
    {
        _runtime = runtime;
    }

    public BrokerProvider Provider => BrokerProvider.OandaV20;

    public CandlePriceBasis PriceBasis => _runtime.PriceBasis;

    public BrokerResult<HttpNetworkRequest> CreateCandleRequest(
        CandleQuery query,
        BrokerInstrumentConfiguration instrument,
        int limit)
    {
        if (!TryMapTimeframe(query.Timeframe, out string? granularity))
        {
            return BrokerResult<HttpNetworkRequest>.Failure(new BrokerError(
                BrokerErrorKind.Unsupported,
                $"OANDA v20 does not support the canonical timeframe '{query.Timeframe}'."));
        }

        string accountId = Uri.EscapeDataString(_runtime.Configuration.AccountId!);
        string providerSymbol = Uri.EscapeDataString(instrument.ProviderSymbol);
        var uriBuilder = new BrokerUriBuilder(
                _runtime.RestEndpoint,
                $"v3/accounts/{accountId}/instruments/{providerSymbol}/candles")
            .Add("price", GetPriceComponent(PriceBasis))
            .Add("granularity", granularity!)
            .Add("smooth", "false")
            .Add("includeFirst", "true")
            .Add("dailyAlignment", 0)
            .Add("alignmentTimezone", "UTC")
            .Add("weeklyAlignment", "Monday");

        if (query.StartTime is { } startTime && query.EndTime is { } endTime)
        {
            DateTimeOffset cappedEnd = query.Timeframe.AddPeriods(startTime, limit);
            uriBuilder
                .Add("from", ToRfc3339(startTime))
                .Add("to", ToRfc3339(cappedEnd < endTime ? cappedEnd : endTime));
        }
        else
        {
            uriBuilder.Add("count", limit);

            if (query.StartTime is { } start)
            {
                uriBuilder.Add("from", ToRfc3339(start));
            }

            if (query.EndTime is { } end)
            {
                uriBuilder.Add("to", ToRfc3339(end));
            }
        }

        var request = new HttpNetworkRequest(
            NetworkProtocol.Https,
            uriBuilder.Build(),
            HttpMethod.Get,
            [
                new HttpNetworkHeader("Authorization", $"Bearer {_runtime.Credentials.AccessToken}"),
                new HttpNetworkHeader("Accept-Datetime-Format", "RFC3339")
            ],
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
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("candles", out JsonElement candleArray) ||
                candleArray.ValueKind != JsonValueKind.Array)
            {
                throw new BrokerPayloadException("OANDA returned a malformed candle response.");
            }

            if (root.TryGetProperty("instrument", out JsonElement responseInstrument) &&
                !string.Equals(
                    responseInstrument.GetString(),
                    instrument.ProviderSymbol,
                    StringComparison.Ordinal))
            {
                throw new BrokerPayloadException("OANDA returned candles for a different instrument.");
            }

            if (root.TryGetProperty("granularity", out JsonElement responseGranularity) &&
                (!TryMapTimeframe(query.Timeframe, out string? expectedGranularity) ||
                 !string.Equals(
                     responseGranularity.GetString(),
                     expectedGranularity,
                     StringComparison.Ordinal)))
            {
                throw new BrokerPayloadException("OANDA returned candles with a different granularity.");
            }

            string priceProperty = GetPriceProperty(PriceBasis);
            var candles = new List<Candle>();
            foreach (JsonElement item in candleArray.EnumerateArray())
            {
                if (candles.Count == limit)
                {
                    break;
                }

                bool isComplete = item.GetProperty("complete").GetBoolean();
                if (!query.IncludeIncomplete && !isComplete)
                {
                    continue;
                }

                if (!item.TryGetProperty(priceProperty, out JsonElement price) ||
                    price.ValueKind != JsonValueKind.Object)
                {
                    throw new BrokerPayloadException(
                        $"OANDA did not return the requested '{priceProperty}' candle prices.");
                }

                DateTimeOffset openTime = ParseRfc3339(item.GetProperty("time").GetString());
                DateTimeOffset closeTime = query.Timeframe.AddPeriods(openTime);

                if (!IsWithinRequestedRange(query, openTime))
                {
                    continue;
                }

                candles.Add(new Candle(
                    query.InstrumentId,
                    query.Timeframe,
                    PriceBasis,
                    openTime,
                    closeTime,
                    ReadDecimal(price, "o", "open"),
                    ReadDecimal(price, "h", "high"),
                    ReadDecimal(price, "l", "low"),
                    ReadDecimal(price, "c", "close"),
                    isComplete,
                    tickCount: item.GetProperty("volume").GetInt64()));
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
            throw new BrokerPayloadException("OANDA returned an invalid candle payload.", exception);
        }
    }

    public BrokerError ParseError(HttpNetworkResponse response) =>
        BrokerHttpErrorMapper.Map(Provider, response);

    private static string GetPriceComponent(CandlePriceBasis priceBasis) => priceBasis switch
    {
        CandlePriceBasis.Midpoint => "M",
        CandlePriceBasis.Bid => "B",
        CandlePriceBasis.Ask => "A",
        _ => throw new InvalidOperationException($"Unsupported OANDA price basis '{priceBasis}'.")
    };

    private static string GetPriceProperty(CandlePriceBasis priceBasis) => priceBasis switch
    {
        CandlePriceBasis.Midpoint => "mid",
        CandlePriceBasis.Bid => "bid",
        CandlePriceBasis.Ask => "ask",
        _ => throw new InvalidOperationException($"Unsupported OANDA price basis '{priceBasis}'.")
    };

    private static decimal ReadDecimal(JsonElement parent, string propertyName, string fieldName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new BrokerPayloadException($"OANDA omitted the candle {fieldName} value.");
        }

        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        if (text is null ||
            !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal result))
        {
            throw new BrokerPayloadException($"OANDA returned an invalid candle {fieldName} value.");
        }

        return result;
    }

    private static DateTimeOffset ParseRfc3339(string? value)
    {
        if (value is null ||
            !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset result))
        {
            throw new BrokerPayloadException("OANDA returned an invalid candle timestamp.");
        }

        return result;
    }

    private static string ToRfc3339(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryMapTimeframe(CandleTimeframe timeframe, out string? value)
    {
        value = (timeframe.Unit, timeframe.Value) switch
        {
            (CandleTimeframeUnit.Second, 5) => "S5",
            (CandleTimeframeUnit.Second, 10) => "S10",
            (CandleTimeframeUnit.Second, 15) => "S15",
            (CandleTimeframeUnit.Second, 30) => "S30",
            (CandleTimeframeUnit.Minute, 1) => "M1",
            (CandleTimeframeUnit.Minute, 2) => "M2",
            (CandleTimeframeUnit.Minute, 3) => "M3",
            (CandleTimeframeUnit.Minute, 4) => "M4",
            (CandleTimeframeUnit.Minute, 5) => "M5",
            (CandleTimeframeUnit.Minute, 10) => "M10",
            (CandleTimeframeUnit.Minute, 15) => "M15",
            (CandleTimeframeUnit.Minute, 30) => "M30",
            (CandleTimeframeUnit.Hour, 1) => "H1",
            (CandleTimeframeUnit.Hour, 2) => "H2",
            (CandleTimeframeUnit.Hour, 3) => "H3",
            (CandleTimeframeUnit.Hour, 4) => "H4",
            (CandleTimeframeUnit.Hour, 6) => "H6",
            (CandleTimeframeUnit.Hour, 8) => "H8",
            (CandleTimeframeUnit.Hour, 12) => "H12",
            (CandleTimeframeUnit.Day, 1) => "D",
            (CandleTimeframeUnit.Week, 1) => "W",
            (CandleTimeframeUnit.Month, 1) => "M",
            _ => null
        };

        return value is not null;
    }

    private static bool IsWithinRequestedRange(CandleQuery query, DateTimeOffset openTime) =>
        (query.StartTime is null || openTime >= query.StartTime.Value) &&
        (query.EndTime is null || openTime < query.EndTime.Value);
}
