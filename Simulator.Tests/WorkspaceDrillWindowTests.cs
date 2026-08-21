using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using Dashboard.Contracts;
using Dashboard.Live;

namespace Simulator.Tests;

/// <summary>
/// Cover for the timeframe drill-in window: anchor resolution, clamping at the edges of available
/// history, and the projection that must NOT truncate the window the way the live snapshot does.
/// </summary>
[TestFixture]
public sealed class WorkspaceDrillWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void FindAnchorFrameIndex_ResolvesTheCandleContainingTheAnchor()
    {
        IReadOnlyList<ReplayFrame> frames = Frames(10, TimeSpan.FromMinutes(5));

        // Exactly on a candle boundary resolves to that candle.
        Assert.That(WorkspaceAnalysis.FindAnchorFrameIndex(frames, Start.AddMinutes(15)), Is.EqualTo(3));
        // Mid-candle resolves DOWN to the candle that contains it, never up to one not yet open.
        Assert.That(WorkspaceAnalysis.FindAnchorFrameIndex(frames, Start.AddMinutes(17)), Is.EqualTo(3));
    }

    [Test]
    public void FindAnchorFrameIndex_ReturnsMinusOneWhenEveryFrameIsNewer()
    {
        IReadOnlyList<ReplayFrame> frames = Frames(5, TimeSpan.FromMinutes(5));
        Assert.That(WorkspaceAnalysis.FindAnchorFrameIndex(frames, Start.AddMinutes(-1)), Is.EqualTo(-1));
    }

    [Test]
    public void WindowBounds_GivesExactly499BeforeAnd500After()
    {
        // The requested shape: 1,000 candles with the anchor at index 499.
        (int start, int end) = WorkspaceAnalysis.WindowBounds(anchorIndex: 800, before: 499, after: 500, frameCount: 2_000);

        Assert.Multiple(() =>
        {
            Assert.That(start, Is.EqualTo(301));
            Assert.That(end, Is.EqualTo(1_300));
            Assert.That(end - start + 1, Is.EqualTo(1_000));
            Assert.That(800 - start, Is.EqualTo(499), "the anchor must sit 499 candles from the window start");
        });
    }

    [Test]
    public void WindowBounds_ClampsAtTheStartOfHistoryWithoutSlidingTheAnchor()
    {
        // Only 10 candles precede the anchor. Clamping must not borrow the shortfall from the
        // "after" side - that would move the anchor off centre while still reporting 1,000 candles.
        (int start, int end) = WorkspaceAnalysis.WindowBounds(anchorIndex: 10, before: 499, after: 500, frameCount: 2_000);

        Assert.Multiple(() =>
        {
            Assert.That(start, Is.Zero);
            Assert.That(end, Is.EqualTo(510), "the after-side must stay at exactly 500, not stretch to fill");
            Assert.That(10 - start, Is.EqualTo(10), "anchor offset reports the truth: only 10 candles before");
        });
    }

    [Test]
    public void WindowBounds_ClampsAtTheLiveEdge()
    {
        (int start, int end) = WorkspaceAnalysis.WindowBounds(anchorIndex: 995, before: 499, after: 500, frameCount: 1_000);

        Assert.Multiple(() =>
        {
            Assert.That(start, Is.EqualTo(496));
            Assert.That(end, Is.EqualTo(999));
            Assert.That(995 - start, Is.EqualTo(499), "a full before-side is still delivered");
        });
    }

    [Test]
    public void WindowBounds_HandlesAnEmptyFrameSet()
    {
        (int start, int end) = WorkspaceAnalysis.WindowBounds(anchorIndex: 0, before: 499, after: 500, frameCount: 0);
        Assert.That(end, Is.LessThan(start), "an empty set yields an empty range rather than index 0");
    }

    [Test]
    public void ProjectWorkspaceWindow_KeepsEveryFrame()
    {
        // The live snapshot projector caps at MaxSnapshotFrames (150). Applying that here would
        // discard everything before the anchor - the half the drill-in exists to show.
        WorkspaceWindowSnapshot window = Window(frameCount: 1_000, anchorIndex: 499);

        WorkspaceWindowSnapshot projected = LiveSseFrameProjector.ProjectWorkspaceWindow(window);

        Assert.That(projected.Dataset.Series[0].Frames, Has.Count.EqualTo(1_000));
        Assert.That(LiveSseFrameProjector.MaxSnapshotFrames, Is.LessThan(1_000),
            "guards the premise: the live cap really is smaller than a drill window");
    }

    [Test]
    public void ProjectWorkspaceWindow_KeepsStructureOnTheAnchorAndStripsItElsewhere()
    {
        WorkspaceWindowSnapshot window = Window(frameCount: 20, anchorIndex: 7);

        IReadOnlyList<ReplayFrame> frames = LiveSseFrameProjector
            .ProjectWorkspaceWindow(window).Dataset.Series[0].Frames;

        Assert.Multiple(() =>
        {
            Assert.That(frames[7].Swings, Is.Not.Empty, "the anchor keeps its overlays");
            Assert.That(frames[6].Swings, Is.Empty, "neighbours are stripped to chart-light frames");
            Assert.That(frames[19].Swings, Is.Empty, "the LAST frame is not special here - the anchor is");
            // Chart body data survives everywhere, or the candles could not be drawn at all.
            Assert.That(frames[0].Candle, Is.Not.Null);
            Assert.That(frames[0].Indicators.Atr, Is.Not.Null);
        });
    }

    [Test]
    public void ProjectWorkspaceWindow_ClampsAnOutOfRangeAnchorIndex()
    {
        WorkspaceWindowSnapshot window = Window(frameCount: 5, anchorIndex: 99);

        IReadOnlyList<ReplayFrame> frames = LiveSseFrameProjector
            .ProjectWorkspaceWindow(window).Dataset.Series[0].Frames;

        Assert.That(frames[4].Swings, Is.Not.Empty, "clamped to the last frame rather than throwing");
    }

    private static WorkspaceWindowSnapshot Window(int frameCount, int anchorIndex) => new(
        new LiveFeedStatus
        {
            State = LiveConnectionState.Connected,
            Symbol = "EUR_USD",
            Interval = "5m",
            Message = "test"
        },
        new ReplayDataset(
            1, "test", "OANDA:EUR/USD", Start, "test", new ChartAnnotationOptions(),
            [new ReplaySeries("5m", 300, Frames(frameCount, TimeSpan.FromMinutes(5)))]),
        "5m",
        Start,
        Start,
        anchorIndex,
        499,
        500,
        250,
        true);

    private static IReadOnlyList<ReplayFrame> Frames(int count, TimeSpan step) =>
        Enumerable.Range(0, count).Select(index =>
        {
            DateTimeOffset at = Start + step * index;
            return new ReplayFrame(
                index,
                at,
                new ReplayCandle(at - step, at, 1m, 2m, 0.5m, 1.5m, 100m),
                new IndicatorSnapshot { Atr = 0.5m, Rsi = 50m },
                [new SwingPoint { Type = SwingType.High, Price = 2m, PivotTime = at, ConfirmedAt = at, Strength = 2 }],
                [],
                [],
                [],
                MarketStructureSnapshot.Empty,
                PriceActionSnapshot.Empty,
                MarketRegimeSnapshot.Unknown,
                new ConfidenceScore { Total = 0m, Contributions = [] },
                0d);
        }).ToArray();
}
