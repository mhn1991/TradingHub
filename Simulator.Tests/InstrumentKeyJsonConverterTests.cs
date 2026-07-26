using System.Text;
using System.Text.Json;
using Brokers.Models;
using NUnit.Framework;

namespace Simulator.Tests;

[TestFixture]
public sealed class InstrumentKeyJsonConverterTests
{
    [Test]
    public async Task DeserializeAsync_ObjectForm_IgnoresPrimitiveMetadataInPartialBuffer()
    {
        const string json =
            """
            {
              "padding": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
              "instrument": {
                "value": "FX:EUR/USD",
                "isEmpty": false
              },
              "tail": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultBufferSize = 32
        };

        InstrumentEnvelope? result =
            await JsonSerializer.DeserializeAsync<InstrumentEnvelope>(stream, options);

        Assert.That(result?.Instrument, Is.EqualTo(new InstrumentKey("FX:EUR/USD")));
    }

    [TestCase("\"FX:EUR/USD\"")]
    [TestCase("""{ "value": "FX:EUR/USD", "isEmpty": false }""")]
    public void Deserialize_AcceptsSupportedRepresentations(string json)
    {
        InstrumentKey result = JsonSerializer.Deserialize<InstrumentKey>(json);

        Assert.That(result, Is.EqualTo(new InstrumentKey("FX:EUR/USD")));
    }

    private sealed record InstrumentEnvelope
    {
        public string Padding { get; init; } = string.Empty;
        public InstrumentKey Instrument { get; init; }
        public string Tail { get; init; } = string.Empty;
    }
}
