using System.Net;
using Brokers.Exceptions;
using Networking.Abstractions;

namespace Brokers.Ig;

internal sealed record IgSessionTokens(string ClientToken, string SecurityToken);

internal sealed record IgSessionContext(
    IgSessionTokens Tokens,
    string CurrentAccountId,
    string? LightstreamerEndpoint);

internal sealed class IgSessionManager : IDisposable
{
    private readonly INetworkGateway _gateway;
    private readonly TransportId _transportId;
    private readonly string _identifier;
    private readonly string _password;
    private readonly string? _requestedAccountId;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private IgSessionContext? _session;
    private int _disposed;

    public IgSessionManager(
        INetworkGateway gateway,
        TransportId transportId,
        string identifier,
        string password,
        string? requestedAccountId)
    {
        _gateway = gateway;
        _transportId = transportId;
        _identifier = identifier;
        _password = password;
        _requestedAccountId = requestedAccountId;
    }

    public async Task<IgSessionContext> GetSessionAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        IgSessionContext? existing = Volatile.Read(ref _session);
        if (existing is not null)
        {
            return existing;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = _session;
            if (existing is not null)
            {
                return existing;
            }

            IgLoginResult login = await _gateway.SendAsync(
                new IgLoginCommand(_transportId, _identifier, _password),
                cancellationToken).ConfigureAwait(false);

            var session = new IgSessionContext(
                login.Tokens,
                login.Response.CurrentAccountId,
                login.Response.LightstreamerEndpoint);

            if (!string.IsNullOrWhiteSpace(_requestedAccountId) &&
                !string.Equals(
                    session.CurrentAccountId,
                    _requestedAccountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                IgSwitchAccountResult switched = await _gateway.SendAsync(
                    new IgSwitchAccountCommand(
                        _transportId,
                        session.Tokens,
                        _requestedAccountId),
                    cancellationToken).ConfigureAwait(false);

                if (!switched.Response.DealingEnabled)
                {
                    throw new InvalidOperationException(
                        $"IG switched to account '{_requestedAccountId}', but dealing is disabled.");
                }

                session = session with
                {
                    CurrentAccountId = _requestedAccountId,
                    Tokens = switched.Tokens ?? session.Tokens
                };
            }

            Volatile.Write(ref _session, session);
            return session;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<TResponse> SendAuthenticatedAsync<TResponse>(
        Func<IgSessionContext, INetworkCommand<TResponse>> commandFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandFactory);

        for (int attempt = 0; ; attempt++)
        {
            IgSessionContext session = await GetSessionAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                return await _gateway.SendAsync(commandFactory(session), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BrokerApiException exception)
                when (attempt == 0 && IsAuthenticationFailure(exception))
            {
                Interlocked.CompareExchange(ref _session, null, session);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _session, null);
        }
    }

    private static bool IsAuthenticationFailure(BrokerApiException exception) =>
        exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
