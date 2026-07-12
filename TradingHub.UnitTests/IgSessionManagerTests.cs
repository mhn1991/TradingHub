using System.Net;
using Brokers.Abstractions;
using Brokers.Exceptions;
using Brokers.Ig;
using Networking.Abstractions;
using NUnit.Framework;

namespace TradingHub.UnitTests;

[TestFixture]
public sealed class IgSessionManagerTests
{
    [Test]
    public async Task AuthenticationFailure_InvalidatesSessionAndRetriesOnce()
    {
        var gateway = new SessionGateway();
        using var sessions = new IgSessionManager(
            gateway,
            "ig.test",
            "identifier",
            "password",
            requestedAccountId: null);

        string result = await sessions.SendAuthenticatedAsync(
            session => new TestCommand("ig.test", session.Tokens.ClientToken),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo("done"));
            Assert.That(gateway.LoginCount, Is.EqualTo(2));
            Assert.That(gateway.AuthenticatedCount, Is.EqualTo(2));
        });
    }

    private sealed record TestCommand(
        TransportId TransportId,
        string Token) : INetworkCommand<string>;

    private sealed class SessionGateway : INetworkGateway
    {
        public int LoginCount { get; private set; }
        public int AuthenticatedCount { get; private set; }

        public Task<TResponse> SendAsync<TResponse>(
            INetworkCommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            object response;

            if (command is IgLoginCommand)
            {
                LoginCount++;
                response = new IgLoginResult(
                    new IgLoginResponse
                    {
                        CurrentAccountId = "account"
                    },
                    new IgSessionTokens($"client-{LoginCount}", $"security-{LoginCount}"));
            }
            else if (command is TestCommand)
            {
                AuthenticatedCount++;
                if (AuthenticatedCount == 1)
                {
                    throw new BrokerApiException(
                        BrokerKind.Ig,
                        HttpStatusCode.Unauthorized,
                        "expired");
                }

                response = "done";
            }
            else
            {
                throw new InvalidOperationException($"Unexpected command {command.GetType().Name}.");
            }

            return Task.FromResult((TResponse)response);
        }

        public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
            INetworkSubscription<TEvent> subscription,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
