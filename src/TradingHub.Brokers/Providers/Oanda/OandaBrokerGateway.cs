using System.Net;
using System.Net.Http.Json;
using TradingHub.Abstractions.Brokers;
using TradingHub.Abstractions.Time;
using TradingHub.Brokers.Internal;
using TradingHub.Brokers.Oanda.Contracts;
using TradingHub.Brokers.Oanda.Http;
using TradingHub.Brokers.Oanda.Mapping;
using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Oanda;

public sealed class OandaBrokerGateway : IBrokerGateway
{
    private readonly OandaBrokerOptions _options;
    private readonly OandaRestClient _restClient;
    private readonly BrokerInstrumentMap _instrumentMap;
    private readonly IClock _clock;
    private readonly BrokerTradingGuard _tradingGuard;

    public OandaBrokerGateway(
        OandaBrokerOptions options,
        HttpClient httpClient,
        BrokerInstrumentMap instrumentMap,
        IClock clock)
    {
        options.EnsureValid();
        _options = options;
        _restClient = new OandaRestClient(httpClient, options);
        _instrumentMap = instrumentMap;
        _clock = clock;
        _tradingGuard = new BrokerTradingGuard(
            options.TradingEnvironment,
            options.AccountId,
            options.LiveAccountConfirmation);
    }

    public string BrokerId => _options.BrokerId;

    public string ProviderName => OandaBrokerProvider.Name;

    public TradingEnvironment Environment => _options.TradingEnvironment;

    public async Task<OrderSubmissionResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_tradingGuard.CanSubmit(request.AccountId, out var guardReason))
        {
            return BrokerResultFactory.RejectedOrder(guardReason, _clock.UtcNow);
        }

        try
        {
            var symbol = _instrumentMap.GetBrokerSymbol(request.InstrumentId);
            var body = OandaOrderMapper.ToExternal(request, symbol);
            var path = $"v3/accounts/{Escape(_options.AccountId)}/orders";
            using var response = await _restClient.SendAsync(HttpMethod.Post, path, body, cancellationToken);
            var payload = await response.Content.ReadFromJsonAsync<OandaOrderResponseDto>(
                OandaJson.SerializerOptions,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return CreateFailure(response.StatusCode, payload?.ErrorMessage);
            }

            return payload is null
                ? BrokerResultFactory.UnknownOrder("OANDA returned an empty order response.", _clock.UtcNow)
                : OandaOrderMapper.ToCanonical(request, payload, _clock.UtcNow);
        }
        catch (NotSupportedException exception)
        {
            return BrokerResultFactory.RejectedOrder(exception.Message, _clock.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BrokerResultFactory.UnknownOrder(
                "OANDA timed out; the order state requires reconciliation.",
                _clock.UtcNow);
        }
        catch (HttpRequestException exception)
        {
            return BrokerResultFactory.UnknownOrder(
                $"OANDA communication failed; reconcile before retrying. {exception.Message}",
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

        var path = $"v3/accounts/{Escape(_options.AccountId)}/orders/{Escape(request.BrokerOrderId)}/cancel";
        try
        {
            using var response = await _restClient.SendAsync<object?>(
                HttpMethod.Put,
                path,
                null,
                cancellationToken);
            var payload = await response.Content.ReadFromJsonAsync<OandaCancelResponseDto>(
                OandaJson.SerializerOptions,
                cancellationToken);

            return response.IsSuccessStatusCode
                ? new OrderCancellationResult
                {
                    Outcome = SubmissionOutcome.Accepted,
                    Status = OrderStatus.Cancelled,
                    OccurredAt = _clock.UtcNow
                }
                : BrokerResultFactory.RejectedCancellation(
                    payload?.ErrorMessage ?? "OANDA rejected the cancellation.",
                    _clock.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BrokerResultFactory.UnknownCancellation(
                "OANDA cancellation timed out; reconcile the order state.",
                _clock.UtcNow);
        }
        catch (HttpRequestException exception)
        {
            return BrokerResultFactory.UnknownCancellation(
                $"OANDA cancellation state is unknown. {exception.Message}",
                _clock.UtcNow);
        }
    }

    public async Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(accountId, _options.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested OANDA account is not configured.");
        }

        var path = $"v3/accounts/{Escape(accountId)}/summary";
        using var response = await _restClient.SendAsync<object?>(HttpMethod.Get, path, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<OandaAccountEnvelopeDto>(
            OandaJson.SerializerOptions,
            cancellationToken);
        var account = payload?.Account
            ?? throw new InvalidOperationException("OANDA returned an empty account response.");
        return OandaAccountMapper.ToCanonical(account, _clock.UtcNow);
    }

    private OrderSubmissionResult CreateFailure(HttpStatusCode statusCode, string? reason)
    {
        var message = reason ?? $"OANDA returned HTTP {(int)statusCode}; reconcile the order state.";
        return BrokerHttpPolicy.IsAmbiguousOrderStatus(statusCode)
            ? BrokerResultFactory.UnknownOrder(message, _clock.UtcNow)
            : BrokerResultFactory.RejectedOrder(message, _clock.UtcNow);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
