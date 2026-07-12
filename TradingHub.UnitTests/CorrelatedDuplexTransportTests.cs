using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Networking.Abstractions;
using Networking.Duplex;
using NUnit.Framework;

namespace TradingHub.UnitTests;

[TestFixture]
public sealed class CorrelatedDuplexTransportTests
{
    [Test]
    public async Task FullStreamBuffer_DoesNotBlockCorrelatedResponses()
    {
        var session = new FakeSession();
        await using var transport = new CorrelatedDuplexTransport(
            "test.duplex",
            TransportKind.WebSocket,
            session,
            new Classifier(),
            ownsSession: true);

        await using IAsyncEnumerator<string> stream = transport
            .SubscribeAsync(new Subscription("test.duplex", "prices"))
            .GetAsyncEnumerator();

        Task<bool> firstItem = stream.MoveNextAsync().AsTask();
        await session.ReadSentAsync();
        await session.PublishAsync("stream:prices:first");
        Assert.That(await firstItem, Is.True);

        await session.PublishAsync("stream:prices:buffered");

        Task<string> command = transport.SendAsync(
            new Command("test.duplex", "request-1", TimeSpan.FromSeconds(1)));
        await session.ReadSentAsync();
        await session.PublishAsync("response:request-1:ok");

        Assert.That(await command, Is.EqualTo("response:request-1:ok"));
    }

    private sealed record Command(
        TransportId TransportId,
        string CorrelationId,
        TimeSpan Timeout) : IDuplexCommand<string>
    {
        public ReadOnlyMemory<byte> Encode() => Bytes($"command:{CorrelationId}");

        public string DecodeResponse(ReadOnlyMemory<byte> response) => Text(response);
    }

    private sealed record Subscription(
        TransportId TransportId,
        string StreamKey) : IDuplexSubscription<string>
    {
        public int BufferCapacity => 1;
        public BoundedChannelFullMode FullMode => BoundedChannelFullMode.Wait;

        public ReadOnlyMemory<byte> EncodeSubscribe() => Bytes($"subscribe:{StreamKey}");

        public ReadOnlyMemory<byte>? EncodeUnsubscribe() => Bytes($"unsubscribe:{StreamKey}");

        public string DecodeEvent(ReadOnlyMemory<byte> message) => Text(message);
    }

    private sealed class Classifier : IInboundMessageClassifier
    {
        public bool TryGetCorrelationId(
            ReadOnlyMemory<byte> message,
            out string correlationId) => TryReadPart(message, "response", out correlationId);

        public bool TryGetStreamKey(
            ReadOnlyMemory<byte> message,
            out string streamKey) => TryReadPart(message, "stream", out streamKey);

        private static bool TryReadPart(
            ReadOnlyMemory<byte> message,
            string prefix,
            out string value)
        {
            string[] parts = Text(message).Split(':', 3);
            value = parts.Length >= 2 && parts[0] == prefix ? parts[1] : string.Empty;
            return value.Length > 0;
        }
    }

    private sealed class FakeSession : IDuplexSession
    {
        private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _sent = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public bool IsStarted { get; private set; }
        public Task Completion { get; private set; } = Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsStarted = true;
            Completion = _inbound.Reader.Completion;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken = default) =>
            _sent.Writer.WriteAsync(message.ToArray(), cancellationToken);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (ReadOnlyMemory<byte> message in _inbound.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }

        public ValueTask PublishAsync(string message) => _inbound.Writer.WriteAsync(Bytes(message));

        public ValueTask<ReadOnlyMemory<byte>> ReadSentAsync() => _sent.Reader.ReadAsync();

        public ValueTask DisposeAsync()
        {
            IsStarted = false;
            _inbound.Writer.TryComplete();
            _sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);
}
