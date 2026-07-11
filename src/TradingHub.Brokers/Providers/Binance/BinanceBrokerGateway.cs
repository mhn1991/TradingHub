using System.Globalization;
using System.Net;
using System.Text.Json;
using TradingHub.Abstractions.Brokers;
using TradingHub.Abstractions.Time;
using TradingHub.Brokers.Internal;
using TradingHub.Brokers.Binance.Contracts;
using TradingHub.Brokers.Binance.Http;
using TradingHub.Brokers.Binance.Mapping;
using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Binance;

public sealed class BinanceBrokerGateway : IBrokerGateway
{
    private readonly BinanceBrokerOptions _options;
    private readonly BinanceRestClient _restClient;
    private readonly BrokerInstrumentMap _instrumentMap;
    private readonly IClock _clock;
    private readonly BrokerTradingGuard _tradingGuard;

    public BinanceBrokerGateway(
        BinanceBrokerOptions options,
        HttpClient httpClient,
        BrokerInstrumentMap instrumentMap,
        IClock clock)
    {
        options.EnsureValid();
        _options = options;
        _restClient = new BinanceRestClient(httpClient, options);
        _instrumentMap = instrumentMap;
        _clock = clock;
        _tradingGuard = new BrokerTradingGuard(
            options.TradingEnvironment,
            options.AccountId,
            options.LiveAccountConfirmation);
    }

    public string BrokerId => _options.BrokerId;

    public string ProviderName => BinanceBrokerProvider.Name;

    public TradingEnvironment Environment => _options.TradingEnvironment;

    public async Task<OrderSubmissionResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_tradingGuard.CanSubmit(request.AccountId, out var guardReason))
        {
            return BinanceOrderMapper.Rejected(guardReason, _clock.UtcNow);
        }

        try
        {
            var symbol = _instrumentMap.GetBrokerSymbol(request.InstrumentId);
            var parameters = BinanceOrderMapper.ToParameters(
                request,
                symbol,
                _clock.UtcNow.ToUnixTimeMilliseconds(),
                _options.ReceiveWindowMilliseconds);
            using var response = await _restClient.SendSignedAsync(
                HttpMethod.Post,
                "api/v3/order",
                parameters,
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return CreateFailure(response.StatusCode, content);
            }

            var payload = JsonSerializer.Deserialize<BinanceOrderResponseDto>(
                content,
                BinanceJson.SerializerOptions);
            return payload is null
                ? BrokerResultFactory.UnknownOrder("Binance returned an empty order response.", _clock.UtcNow)
                : BinanceOrderMapper.ToCanonical(request, payload, _clock.UtcNow);
        }
        catch (NotSupportedException exception)
        {
            return BinanceOrderMapper.Rejected(exception.Message, _clock.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BrokerResultFactory.UnknownOrder(
                "Binance timed out; query the order before attempting any retry.",
                _clock.UtcNow);
        }
        catch (HttpRequestException exception)
        {
            return BrokerResultFactory.UnknownOrder(
                $"Binance communication failed; query the order before retrying. {exception.Message}",
                _clock.UtcNow);
        }
    }

    public async Task<OrderCancellationResult> CancelOrderAsync(
        OrderCancellationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_tradingGuard.CanSubmit(request.AccountId, out var guardReason))
        {
            return BrokerResultFactory.RejectedCancellation(guardReason, _clock.UtcNow);
        }

        var parameters = CreateCancellationParameters(request);
        try
        {
            using var response = await _restClient.SendSignedAsync(
                HttpMethod.Delete,
                "api/v3/order",
                parameters,
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return BrokerHttpPolicy.IsAmbiguousOrderStatus(response.StatusCode)
                    ? BrokerResultFactory.UnknownCancellation(ReadError(content), _clock.UtcNow)
                    : BrokerResultFactory.RejectedCancellation(ReadError(content), _clock.UtcNow);
            }

            return new OrderCancellationResult
            {
                Outcome = SubmissionOutcome.Accepted,
                Status = OrderStatus.Cancelled,
                OccurredAt = _clock.UtcNow
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BrokerResultFactory.UnknownCancellation(
                "Binance cancellation timed out; query the order state.",
                _clock.UtcNow);
        }
        catch (HttpRequestException exception)
        {
            return BrokerResultFactory.UnknownCancellation(
                $"Binance cancellation state is unknown. {exception.Message}",
                _clock.UtcNow);
        }
    }

    public async Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(accountId, _options.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested Binance account alias is not configured.");
        }

        var parameters = TimestampParameters();
        parameters.Insert(0, Pair("omitZeroBalances", "true"));
        using var response = await _restClient.SendSignedAsync(
            HttpMethod.Get,
            "api/v3/account",
            parameters,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = JsonSerializer.Deserialize<BinanceAccountDto>(content, BinanceJson.SerializerOptions)
            ?? throw new InvalidOperationException("Binance returned an empty account response.");
        return BinanceAccountMapper.ToCanonical(payload, accountId, _clock.UtcNow);
    }

    private List<KeyValuePair<string, string>> CreateCancellationParameters(OrderCancellationRequest request)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            Pair("symbol", _instrumentMap.GetBrokerSymbol(request.InstrumentId)),
            Pair("orderId", request.BrokerOrderId)
        };
        parameters.AddRange(TimestampParameters());
        return parameters;
    }

    private List<KeyValuePair<string, string>> TimestampParameters()
    {
        return
        [
            Pair("recvWindow", _options.ReceiveWindowMilliseconds.ToString(CultureInfo.InvariantCulture)),
            Pair("timestamp", _clock.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
        ];
    }

    private OrderSubmissionResult CreateFailure(HttpStatusCode statusCode, string content)
    {
        var reason = ReadError(content);
        return BrokerHttpPolicy.IsAmbiguousOrderStatus(statusCode)
            ? BrokerResultFactory.UnknownOrder(reason, _clock.UtcNow)
            : BinanceOrderMapper.Rejected(reason, _clock.UtcNow);
    }

    private static string ReadError(string content)
    {
        try
        {
            var error = JsonSerializer.Deserialize<BinanceErrorDto>(content, BinanceJson.SerializerOptions);
            return error is null ? content : $"Binance {error.Code}: {error.Msg}";
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(content) ? "Binance returned an unspecified error." : content;
        }
    }

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);
}
