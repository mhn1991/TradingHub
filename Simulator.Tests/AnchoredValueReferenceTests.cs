using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Value;

namespace Simulator.Tests;

[TestFixture]
public sealed class AnchoredValueReferenceTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");

    [Test]
    public void VolumeLessCandles_UseTwap_AndResetAtSessionBoundary()
    {
        var state = new AnchoredValueReferenceState(4, 80m);
        var history = new List<Candle>();

        Candle first = Create(new DateTimeOffset(2026, 7, 14, 23, 59, 0, TimeSpan.Zero), 9m, 11m, 10m, null);
        history.Add(first);
        IReadOnlyList<AnchoredValueReference> before = state.Update(
            first, history, [], PriceActionSnapshot.Empty, 1m);

        Candle second = Create(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), 19m, 21m, 20m, null);
        history.Add(second);
        IReadOnlyList<AnchoredValueReference> after = state.Update(
            second, history, [], PriceActionSnapshot.Empty, 1m);

        AnchoredValueReference firstSession = before.Single(item => item.AnchorType == ValueAnchorType.SessionOpen);
        AnchoredValueReference secondSession = after.Single(item => item.AnchorType == ValueAnchorType.SessionOpen);
        Assert.Multiple(() =>
        {
            Assert.That(firstSession.Kind, Is.EqualTo(ValueReferenceKind.AnchoredTwap));
            Assert.That(firstSession.Value, Is.EqualTo(10m));
            Assert.That(secondSession.AnchoredAt, Is.EqualTo(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)));
            Assert.That(secondSession.Value, Is.EqualTo(20m));
        });
    }

    [Test]
    public void TickVolume_UsesIncrementalSemanticVwap()
    {
        var state = new AnchoredValueReferenceState(4, 100m);
        var history = new List<Candle>();
        Candle first = Create(new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), 9m, 11m, 10m,
            new MarketVolume(1m, VolumeKind.TickCount));
        history.Add(first);
        state.Update(first, history, [], PriceActionSnapshot.Empty, 1m);
        Candle second = Create(new DateTimeOffset(2026, 7, 15, 10, 1, 0, TimeSpan.Zero), 19m, 21m, 20m,
            new MarketVolume(3m, VolumeKind.TickCount));
        history.Add(second);

        AnchoredValueReference session = state.Update(
                second, history, [], PriceActionSnapshot.Empty, 2m)
            .Single(item => item.AnchorType == ValueAnchorType.SessionOpen);

        Assert.Multiple(() =>
        {
            Assert.That(session.Kind, Is.EqualTo(ValueReferenceKind.BrokerTickVolumeVwap));
            Assert.That(session.Value, Is.EqualTo(17.5m));
            Assert.That(session.DataCoveragePercent, Is.EqualTo(100m));
            Assert.That(session.DistanceAtr, Is.EqualTo(1.25m));
        });
    }

    [Test]
    public void SwingAnchor_IsUnavailableUntilSwingConfirmation()
    {
        var state = new AnchoredValueReferenceState(4, 80m);
        var history = new List<Candle>();
        DateTimeOffset pivot = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        Candle first = Create(pivot, 9m, 11m, 10m, null);
        history.Add(first);
        IReadOnlyList<AnchoredValueReference> before = state.Update(
            first, history, [], PriceActionSnapshot.Empty, 1m);

        Candle confirmation = Create(pivot.AddMinutes(2), 11m, 13m, 12m, null);
        history.Add(Create(pivot.AddMinutes(1), 10m, 12m, 11m, null));
        history.Add(confirmation);
        var swing = new SwingPoint
        {
            PivotTime = pivot,
            ConfirmedAt = confirmation.CloseTime!.Value,
            Price = 11m,
            Type = SwingType.High,
            Strength = 2
        };
        IReadOnlyList<AnchoredValueReference> after = state.Update(
            confirmation, history, [swing], PriceActionSnapshot.Empty, 1m);

        Assert.Multiple(() =>
        {
            Assert.That(before.Any(item => item.AnchorType == ValueAnchorType.MajorSwing), Is.False);
            Assert.That(after.Any(item => item.AnchorType == ValueAnchorType.MajorSwing), Is.True);
            Assert.That(after.Single(item => item.AnchorType == ValueAnchorType.MajorSwing).Value, Is.EqualTo(11m));
        });
    }

    private static Candle Create(
        DateTimeOffset open,
        decimal low,
        decimal high,
        decimal close,
        MarketVolume? volume) => new()
    {
        Instrument = Instrument,
        Interval = BarInterval.Minutes(1),
        OpenTime = open,
        CloseTime = open.AddMinutes(1),
        Prices = new Ohlc(close, high, low, close),
        Volume = volume,
        IsComplete = true
    };
}
