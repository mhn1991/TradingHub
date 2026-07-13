using Brokers.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class IntervalDomainEdgeTests
{
    [TestCase(1, BarUnit.Second, "2026-01-01T00:00:01Z")]
    [TestCase(5, BarUnit.Minute, "2026-01-01T00:05:00Z")]
    [TestCase(2, BarUnit.Hour, "2026-01-01T02:00:00Z")]
    [TestCase(3, BarUnit.Day, "2026-01-04T00:00:00Z")]
    [TestCase(2, BarUnit.Week, "2026-01-15T00:00:00Z")]
    [TestCase(1, BarUnit.Month, "2026-02-01T00:00:00Z")]
    public void AddTo_AdvancesEverySupportedUnit(
        int value,
        BarUnit unit,
        string expected)
    {
        var interval = new BarInterval(value, unit);
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.That(interval.AddTo(start), Is.EqualTo(DateTimeOffset.Parse(expected)));
    }

    [Test]
    public void MonthArithmeticUsesCalendarRules()
    {
        DateTimeOffset januaryEnd = DateTimeOffset.Parse("2024-01-31T00:00:00Z");

        DateTimeOffset result = BarInterval.Months(1).AddTo(januaryEnd);

        Assert.That(result, Is.EqualTo(DateTimeOffset.Parse("2024-02-29T00:00:00Z")));
    }

    [Test]
    public void DefaultAndUndefinedIntervalsAreInvalid()
    {
        var undefined = new BarInterval(1, (BarUnit)999);

        Assert.Multiple(() =>
        {
            Assert.That(default(BarInterval).IsValid, Is.False);
            Assert.That(undefined.IsValid, Is.False);
            Assert.That(
                () => default(BarInterval).AddTo(DateTimeOffset.UtcNow),
                Throws.InvalidOperationException);
            Assert.That(
                () => undefined.AddTo(DateTimeOffset.UtcNow),
                Throws.InvalidOperationException);
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ConstructorRejectsNonPositiveValue(int value)
    {
        Assert.That(
            () => new BarInterval(value, BarUnit.Minute),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
