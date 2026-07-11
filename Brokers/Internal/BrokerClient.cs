using Networking;

namespace Brokers;

internal sealed class BrokerClient : IBroker
{
    private readonly BrokerRuntimeConfiguration _runtime;
    private readonly IBrokerProviderAdapter _adapter;
    private readonly INetworkDispatcher _networkDispatcher;

    public BrokerClient(
        BrokerRuntimeConfiguration runtime,
        IBrokerProviderAdapter adapter,
        INetworkDispatcher networkDispatcher)
    {
        _runtime = runtime;
        _adapter = adapter;
        _networkDispatcher = networkDispatcher;
    }

    public string Id => _runtime.Configuration.Id;

    public string Name => _runtime.Configuration.Name;

    public BrokerProvider Provider => _runtime.Configuration.Provider;

    public BrokerEnvironment Environment => _runtime.Configuration.Environment;

    public Uri RestEndpoint => _runtime.RestEndpoint;

    public Uri? StreamingEndpoint => _runtime.StreamingEndpoint;

    public CandlePriceBasis CandlePriceBasis => _runtime.PriceBasis;

    public IReadOnlyCollection<BrokerInstrumentConfiguration> Instruments => _runtime.Instruments;

    public async Task<BrokerResult<CandleBatch>> GetCandlesAsync(
        CandleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_runtime.InstrumentsById.TryGetValue(
                query.InstrumentId,
                out BrokerInstrumentConfiguration? instrument) ||
            !instrument.IsEnabled)
        {
            return BrokerResult<CandleBatch>.Failure(new BrokerError(
                BrokerErrorKind.Validation,
                $"Instrument '{query.InstrumentId}' is not enabled for broker '{Id}'."));
        }

        int limit = query.Limit ?? _runtime.Configuration.DefaultCandleLimit;
        BrokerResult<HttpNetworkRequest> requestResult =
            _adapter.CreateCandleRequest(query, instrument, limit);

        if (!requestResult.IsSuccess)
        {
            return BrokerResult<CandleBatch>.Failure(requestResult.Error!);
        }

        HttpNetworkResponse response;
        try
        {
            response = await _networkDispatcher
                .DispatchAsync(requestResult.Value!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NetworkTimeoutException exception)
        {
            return BrokerResult<CandleBatch>.Failure(new BrokerError(
                BrokerErrorKind.Timeout,
                exception.Message,
                IsRetryable: true));
        }
        catch (NetworkResponseTooLargeException exception)
        {
            return BrokerResult<CandleBatch>.Failure(new BrokerError(
                BrokerErrorKind.InvalidResponse,
                exception.Message));
        }
        catch (NetworkTransportException exception)
        {
            return BrokerResult<CandleBatch>.Failure(new BrokerError(
                BrokerErrorKind.Transport,
                exception.Message,
                IsRetryable: true));
        }

        if (!response.IsSuccessStatusCode)
        {
            return BrokerResult<CandleBatch>.Failure(_adapter.ParseError(response));
        }

        try
        {
            CandleBatch batch = _adapter.ParseCandles(query, instrument, limit, response);
            return BrokerResult<CandleBatch>.Success(batch);
        }
        catch (BrokerPayloadException exception)
        {
            return BrokerResult<CandleBatch>.Failure(new BrokerError(
                BrokerErrorKind.InvalidResponse,
                exception.Message,
                HttpStatusCode: response.StatusCodeValue,
                RequestId: BrokerHttpErrorMapper.GetRequestId(response)));
        }
    }
}
