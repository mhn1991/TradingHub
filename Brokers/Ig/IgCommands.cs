using System.Text;
using Brokers.Abstractions;
using Brokers.Infrastructure;
using Networking.Abstractions;
using Networking.Http;

namespace Brokers.Ig;

internal abstract class IgHttpCommand<TResponse>(TransportId transportId)
    : IHttpCommand<TResponse>
{
    public TransportId TransportId { get; } = transportId;
    public TimeSpan? Timeout => null;
    public bool EnsureSuccessStatusCode => false;
    public abstract HttpRequestMessage CreateRequest();

    public virtual ValueTask<TResponse> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        BrokerJson.ReadAsync<TResponse>(BrokerKind.Ig, response, cancellationToken);

    protected static void AddVersion(HttpRequestMessage request, int version) =>
        request.Headers.TryAddWithoutValidation("Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    protected static void AddTokens(HttpRequestMessage request, IgSessionTokens tokens)
    {
        request.Headers.TryAddWithoutValidation("CST", tokens.ClientToken);
        request.Headers.TryAddWithoutValidation("X-SECURITY-TOKEN", tokens.SecurityToken);
    }
}

internal sealed class IgLoginCommand(
    TransportId transportId,
    string identifier,
    string password) : IgHttpCommand<IgLoginResult>(transportId)
{
    public override HttpRequestMessage CreateRequest()
    {
        string json = BrokerJson.Serialize(
            new IgLoginRequest(identifier, password, EncryptedPassword: false));

        var request = new HttpRequestMessage(HttpMethod.Post, "session")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddVersion(request, 2);
        return request;
    }

    public override async ValueTask<IgLoginResult> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        IgLoginResponse body = await BrokerJson.ReadAsync<IgLoginResponse>(
            BrokerKind.Ig,
            response,
            cancellationToken).ConfigureAwait(false);

        return new IgLoginResult(body, ReadTokens(response));
    }

    private static IgSessionTokens ReadTokens(HttpResponseMessage response)
    {
        string cst = GetRequiredHeader(response, "CST");
        string securityToken = GetRequiredHeader(response, "X-SECURITY-TOKEN");
        return new IgSessionTokens(cst, securityToken);
    }

    private static string GetRequiredHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            string? value = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException($"IG login response did not include the {name} header.");
    }
}

internal abstract class IgAuthenticatedCommand<TResponse>(
    TransportId transportId,
    IgSessionTokens tokens,
    int version) : IgHttpCommand<TResponse>(transportId)
{
    public virtual bool IsIdempotent => true;
    public virtual int MaxTransientRetries => 2;

    protected HttpRequestMessage Authenticate(HttpRequestMessage request)
    {
        AddVersion(request, version);
        AddTokens(request, tokens);
        return request;
    }
}

internal sealed class IgSwitchAccountCommand(
    TransportId transportId,
    IgSessionTokens tokens,
    string accountId) : IgAuthenticatedCommand<IgSwitchAccountResult>(transportId, tokens, 1)
{
    public override bool IsIdempotent => false;
    public override int MaxTransientRetries => 0;

    public override HttpRequestMessage CreateRequest()
    {
        string json = BrokerJson.Serialize(
            new IgSwitchAccountRequest(accountId, DefaultAccount: false));

        return Authenticate(new HttpRequestMessage(HttpMethod.Put, "session")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }

    public override async ValueTask<IgSwitchAccountResult> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        IgSwitchAccountResponse body = await BrokerJson.ReadAsync<IgSwitchAccountResponse>(
            BrokerKind.Ig,
            response,
            cancellationToken).ConfigureAwait(false);

        IgSessionTokens? newTokens = null;
        if (response.Headers.TryGetValues("CST", out IEnumerable<string>? cstValues) &&
            response.Headers.TryGetValues("X-SECURITY-TOKEN", out IEnumerable<string>? tokenValues))
        {
            string? cst = cstValues.FirstOrDefault();
            string? security = tokenValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cst) && !string.IsNullOrWhiteSpace(security))
            {
                newTokens = new IgSessionTokens(cst, security);
            }
        }

        return new IgSwitchAccountResult(body, newTokens);
    }
}

internal sealed class IgGetAccountsCommand(
    TransportId transportId,
    IgSessionTokens tokens) : IgAuthenticatedCommand<IgAccountDto[]>(transportId, tokens, 1)
{
    public override HttpRequestMessage CreateRequest() =>
        Authenticate(new HttpRequestMessage(HttpMethod.Get, "accounts"));
}

internal sealed class IgGetPricesCommand(
    TransportId transportId,
    IgSessionTokens tokens,
    string epic,
    string resolution,
    int limit) : IgAuthenticatedCommand<IgPricesResponse>(transportId, tokens, 2)
{
    public override HttpRequestMessage CreateRequest() => Authenticate(
        new HttpRequestMessage(
            HttpMethod.Get,
            $"prices/{Uri.EscapeDataString(epic)}/{Uri.EscapeDataString(resolution)}/{limit}"));
}

internal sealed class IgGetPositionsCommand(
    TransportId transportId,
    IgSessionTokens tokens) : IgAuthenticatedCommand<IgPositionsResponse>(transportId, tokens, 2)
{
    public override HttpRequestMessage CreateRequest() =>
        Authenticate(new HttpRequestMessage(HttpMethod.Get, "positions"));
}

internal sealed class IgGetWorkingOrdersCommand(
    TransportId transportId,
    IgSessionTokens tokens) : IgAuthenticatedCommand<IgWorkingOrdersResponse>(transportId, tokens, 2)
{
    public override HttpRequestMessage CreateRequest() =>
        Authenticate(new HttpRequestMessage(HttpMethod.Get, "working-orders"));
}
