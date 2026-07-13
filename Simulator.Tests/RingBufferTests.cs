using ChartAnnotator.Collections;

namespace Simulator.Tests;

[TestFixture]
public sealed class RingBufferTests
{
    [Test]
    public void Add_WhenCapacityIsExceeded_OverwritesOldestAndKeepsLogicalOrder()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        bool overwritten = buffer.Add(4, out int removed);

        Assert.Multiple(() =>
        {
            Assert.That(overwritten, Is.True);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(buffer.Count, Is.EqualTo(3));
            Assert.That(buffer.Oldest, Is.EqualTo(2));
            Assert.That(buffer.Latest, Is.EqualTo(4));
            Assert.That(buffer.Snapshot(), Is.EqualTo(new[] { 2, 3, 4 }));
            Assert.That(buffer[^2], Is.EqualTo(3));
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Constructor_RejectsNonPositiveCapacity(int capacity)
    {
        Assert.That(
            () => new RingBuffer<int>(capacity),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void EmptyBuffer_RejectsElementAccessAndReplacement()
    {
        var buffer = new RingBuffer<string>(2);

        Assert.Multiple(() =>
        {
            Assert.That(() => buffer.Oldest, Throws.InvalidOperationException);
            Assert.That(() => buffer.Latest, Throws.InvalidOperationException);
            Assert.That(() => buffer[0], Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => buffer.ReplaceLatest("x"), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void ReplaceLatest_ChangesOnlyNewestElement()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);

        buffer.ReplaceLatest(9);

        Assert.That(buffer.Snapshot(), Is.EqualTo(new[] { 1, 9 }));
    }

    [TestCase(-1, false, 0)]
    [TestCase(0, true, 30)]
    [TestCase(1, true, 20)]
    [TestCase(2, true, 10)]
    [TestCase(3, false, 0)]
    public void TryGetLatest_HandlesOffsets(
        int offset,
        bool expectedSuccess,
        int expectedValue)
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);

        bool success = buffer.TryGetLatest(offset, out int value);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(value, Is.EqualTo(expectedValue));
        });
    }

    [Test]
    public void CopyTo_RequiresEnoughSpaceAndPreservesLogicalOrderAfterWrap()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);
        var destination = new int[3];

        buffer.CopyTo(destination);

        Assert.That(destination, Is.EqualTo(new[] { 2, 3, 4 }));
        Assert.That(
            () => buffer.CopyTo(new int[2]),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void CapacityOne_AlwaysRetainsLatestItem()
    {
        var buffer = new RingBuffer<string>(1);
        buffer.Add("first");

        bool overwritten = buffer.Add("second", out string removed);

        Assert.Multiple(() =>
        {
            Assert.That(overwritten, Is.True);
            Assert.That(removed, Is.EqualTo("first"));
            Assert.That(buffer.Single(), Is.EqualTo("second"));
        });
    }

    [Test]
    public void Clear_ResetsLogicalStateAndAllowsReuse()
    {
        var buffer = new RingBuffer<object>(2);
        buffer.Add(new object());
        buffer.Add(new object());

        buffer.Clear();
        buffer.Add("reused");

        Assert.Multiple(() =>
        {
            Assert.That(buffer.Count, Is.EqualTo(1));
            Assert.That(buffer.IsFull, Is.False);
            Assert.That(buffer.Latest, Is.EqualTo("reused"));
        });
    }
}
