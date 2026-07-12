using Networking.Abstractions;
using System.Net;

namespace Networking.Http;

/// <summary>
/// Sends HTTP commands through one reusable HttpClient.
/// </summary>
public sealed class HttpTransport : IRequestTransport, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _defaultTimeout;
    private readonly bool _ownsHttpClient;
    private int _disposed;

    public HttpTransport(
        TransportId id,
        HttpClient httpClient,
        TimeSpan? defaultTimeout = null,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        Id = id;
        _httpClient = httpClient;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
        _ownsHttpClient = ownsHttpClient;

        ValidateTimeout(_defaultTimeout, nameof(defaultTimeout));
    }

    public TransportId Id { get; }

    public TransportKind Kind => TransportKind.Http;

    public async Task<TResponse> SendAsync<TResponse>(
        INetworkCommand<TResponse> command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        ArgumentNullException.ThrowIfNull(command);

        if (command.TransportId != Id)
        {
            throw new InvalidOperationException(
                $"Command route '{command.TransportId}' does not match HTTP transport '{Id}'.");
        }

        if (command is not IHttpCommand<TResponse> httpCommand)
        {
            throw new ArgumentException(
                $"Commands sent through '{Id}' must implement " +
                $"{typeof(IHttpCommand<TResponse>).Name}.",
                nameof(command));
        }

        TimeSpan timeout = httpCommand.Timeout ?? _defaultTimeout;
        ValidateTimeout(timeout, nameof(httpCommand.Timeout));

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (timeout != Timeout.InfiniteTimeSpan)
        {
            operationCts.CancelAfter(timeout);
        }

        try
        {
            int maximumRetries = httpCommand.IsIdempotent
                ? httpCommand.MaxTransientRetries
                : 0;
            if (maximumRetries < 0)
            {
                throw new InvalidOperationException("An HTTP command cannot specify a negative retry count.");
            }

            for (int attempt = 0; ; attempt++)
            {
                using HttpRequestMessage request = httpCommand.CreateRequest()
                    ?? throw new InvalidOperationException("The HTTP command returned a null request.");

                try
                {
                    using HttpResponseMessage response = await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            operationCts.Token)
                        .ConfigureAwait(false);

                    if (attempt < maximumRetries && IsTransient(response.StatusCode))
                    {
                        await DelayBeforeRetryAsync(response, attempt, operationCts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (httpCommand.EnsureSuccessStatusCode)
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    return await httpCommand.ReadResponseAsync(response, operationCts.Token)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                    when (attempt < maximumRetries &&
                          (exception.StatusCode is null || IsTransient(exception.StatusCode.Value)))
                {
                    await Task.Delay(RetryDelay(attempt), operationCts.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested &&
                  timeout != Timeout.InfiniteTimeSpan &&
                  operationCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The HTTP operation on transport '{Id}' exceeded its {timeout} timeout.",
                exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Timeout must be positive or Timeout.InfiniteTimeSpan.");
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static async Task DelayBeforeRetryAsync(
        HttpResponseMessage response,
        int attempt,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : RetryDelay(attempt));

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        if (delay > TimeSpan.FromSeconds(30))
        {
            delay = TimeSpan.FromSeconds(30);
        }

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
}
