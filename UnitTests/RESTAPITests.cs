using API;
using Brokers.Brokers;

namespace UnitTests;


public class RESTAPITests
{
    private Rest? _rest { get; set; } = null;
    private Instrument? _instrument { get; set; } = null;
    [SetUp]
    public void Setup()
    {
        _rest = new Rest();
        _instrument = new Instrument("BTCUSDT", "5m", "500");
    }

    [Test]
    public async Task TestGet()
    {
        await (_rest?.Get(_instrument.GetFinalUrl()) ?? Task.CompletedTask);
    }
}