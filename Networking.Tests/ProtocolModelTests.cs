using System.Net.WebSockets;
using System.Text;

namespace Networking.Tests;

public sealed class ProtocolModelTests
{
    [Test]
    public void WebSocketConnectRequest_InfersSecureProtocolAndOwnsHandshakeData()
    {
        var request = new WebSocketConnectRequest(
            new Uri("wss://stream.broker.example/prices"),
            [new HttpNetworkHeader("X-Api-Key", "key")],
            ["prices.v1"]);

        Assert.Multiple(() =>
        {
            Assert.That(request.Protocol, Is.EqualTo(NetworkProtocol.WebSocketSecure));
            Assert.That(request.Endpoint.Scheme, Is.EqualTo("wss"));
            Assert.That(request.HandshakeHeaders[0].Name, Is.EqualTo("X-Api-Key"));
            Assert.That(request.SubProtocols, Is.EqualTo(new[] { "prices.v1" }));
        });
    }

    [Test]
    public void FixConnectRequest_ModelsStreamDestinationWithoutHttpFields()
    {
        var request = new FixConnectRequest(
            NetworkProtocol.FixTls,
            "fix.broker.example",
            9876);

        Assert.Multiple(() =>
        {
            Assert.That(request.Protocol, Is.EqualTo(NetworkProtocol.FixTls));
            Assert.That(request.Destination, Is.EqualTo("fix.broker.example:9876"));
            Assert.That(request.TlsTargetHost, Is.EqualTo("fix.broker.example"));
        });
    }

    [Test]
    public void ProtocolMessages_CopyCallerOwnedPayloads()
    {
        byte[] webSocketPayload = Encoding.UTF8.GetBytes("tick");
        byte[] fixPayload = Encoding.ASCII.GetBytes("8=FIX.4.4\u00019=0\u000110=000\u0001");

        WebSocketNetworkMessage webSocketMessage = WebSocketNetworkMessage.Binary(webSocketPayload);
        var fixMessage = new FixNetworkMessage(fixPayload);
        webSocketPayload[0] = 0;
        fixPayload[0] = 0;

        Assert.Multiple(() =>
        {
            Assert.That(webSocketMessage.MessageType, Is.EqualTo(WebSocketMessageType.Binary));
            Assert.That(webSocketMessage.Payload.Span[0], Is.EqualTo((byte)'t'));
            Assert.That(fixMessage.Payload.Span[0], Is.EqualTo((byte)'8'));
        });
    }
}
