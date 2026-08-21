using ChartAnnotator.Confluence;
using ChartAnnotator.Liquidity;
using ChartAnnotator.SupplyDemand;
using Dashboard.Contracts;

namespace Dashboard.Live;

/// <summary>
/// Projects in-memory analysis frames into a transport-safe shape for live SSE/HTTP snapshots.
/// Full structural history (liquidity event rings, retained pools, NEoWave, etc.) is duplicated on
/// every frame in the ring buffer and can push a single snapshot into hundreds of megabytes —
/// large enough to truncate in EventSource and yield "Unexpected end of JSON input".
/// </summary>
internal static class LiveSseFrameProjector
{
    /// <summary>Enough closed candles for the chart window without saturating browser/proxy JSON paths.</summary>
    public const int MaxSnapshotFrames = 150;

    /// <summary>Cap active structural overlays on the transport edge frame.</summary>
    public const int MaxTransportActiveZones = 16;
    public const int MaxTransportActivePools = 24;

    public static LiveReplayPayload ProjectSnapshot(LiveReplayPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ReplaySeries[] series = payload.Dataset.Series
            .Select(ProjectSeries)
            .ToArray();
        return payload with
        {
            Dataset = payload.Dataset with { Series = series }
        };
    }

    public static WorkspaceSnapshot ProjectWorkspaceSnapshot(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ReplaySeries[] series = snapshot.Dataset.Series
            .Select(ProjectSeries)
            .ToArray();
        return snapshot with
        {
            Dataset = snapshot.Dataset with { Series = series }
        };
    }

    /// <summary>
    /// Projects a drill-in window. Deliberately does NOT apply <see cref="MaxSnapshotFrames"/>:
    /// the whole point of the window is that the caller asked for a specific span around an anchor,
    /// so truncating it to the newest 150 would silently discard most of it - including everything
    /// before the anchor, which is the half the user drilled in to see.
    ///
    /// Size stays bounded the same way <see cref="ProjectSeries"/> bounds it: only ONE frame keeps
    /// structural overlays (here the anchor, not the last frame, because the anchor is what the
    /// chart centres and annotates). Every other frame keeps just candle/indicators/regime, which is
    /// what the chart body actually draws.
    /// </summary>
    public static WorkspaceWindowSnapshot ProjectWorkspaceWindow(WorkspaceWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ReplaySeries[] series = snapshot.Dataset.Series
            .Select(item => ProjectWindowSeries(item, snapshot.AnchorIndex))
            .ToArray();
        return snapshot with
        {
            Dataset = snapshot.Dataset with { Series = series }
        };
    }

    private static ReplaySeries ProjectWindowSeries(ReplaySeries series, int structuralIndex)
    {
        IReadOnlyList<ReplayFrame> frames = series.Frames;
        if (frames.Count == 0)
        {
            return series;
        }

        int keepAt = Math.Clamp(structuralIndex, 0, frames.Count - 1);
        ReplayFrame[] projected = new ReplayFrame[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            projected[i] = ProjectFrame(frames[i], keepStructure: i == keepAt);
        }

        return series with { Frames = projected };
    }

    public static IReadOnlyList<ReplayFrame> ProjectUpdateFrames(IReadOnlyList<ReplayFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            return frames;
        }

        // Warmup often arrives as one large "update" after an empty WarmingUp snapshot.
        // Only the newest frame needs structural overlays; earlier frames stay chart-light.
        int lastIndex = frames.Count - 1;
        ReplayFrame[] projected = new ReplayFrame[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            projected[i] = ProjectFrame(frames[i], keepStructure: i == lastIndex);
        }

        return projected;
    }

    private static ReplaySeries ProjectSeries(ReplaySeries series)
    {
        IReadOnlyList<ReplayFrame> frames = series.Frames;
        if (frames.Count > MaxSnapshotFrames)
        {
            frames = frames.Skip(frames.Count - MaxSnapshotFrames).ToArray();
        }

        if (frames.Count == 0)
        {
            return series with { Frames = frames };
        }

        int lastIndex = frames.Count - 1;
        ReplayFrame[] projected = new ReplayFrame[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            projected[i] = ProjectFrame(frames[i], keepStructure: i == lastIndex);
        }

        return series with { Frames = projected };
    }

    private static ReplayFrame ProjectFrame(ReplayFrame frame, bool keepStructure)
    {
        if (!keepStructure)
        {
            // Keep candle/indicators/regime for the chart body. Drop everything else — historical
            // overlays are reconstructed from the edge frame, and event rings explode SSE size.
            return frame with
            {
                Swings = [],
                PriceZones = [],
                Trendlines = [],
                Channels = [],
                MarketStructure = ChartAnnotator.Models.MarketStructureSnapshot.Empty,
                PriceAction = ChartAnnotator.Models.PriceActionSnapshot.Empty,
                ValueReferences = null,
                NeoWave = null,
                SupplyDemand = null,
                Liquidity = null,
                SupplyDemandLiquidityConfluence = null
            };
        }

        return frame with
        {
            SupplyDemand = SlimSupplyDemand(frame.SupplyDemand),
            Liquidity = SlimLiquidity(frame.Liquidity),
            SupplyDemandLiquidityConfluence = SlimConfluence(frame.SupplyDemandLiquidityConfluence)
        };
    }

    private static SupplyDemandAnalysisSnapshot? SlimSupplyDemand(SupplyDemandAnalysisSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsEnabled)
        {
            return snapshot;
        }

        // Active zones are enough for the chart edge; drop retained lifecycle/event rings.
        IReadOnlyList<SupplyDemandZone> active = RankTake(
            snapshot.ActiveZones,
            MaxTransportActiveZones,
            static zone => zone.QualityScore);
        return snapshot with
        {
            Zones = active,
            ActiveZones = active,
            RecentEvents = []
        };
    }

    private static LiquidityAnalysisSnapshot? SlimLiquidity(LiquidityAnalysisSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsEnabled)
        {
            return snapshot;
        }

        IReadOnlyList<LiquidityPool> active = RankTake(
            snapshot.ActivePools,
            MaxTransportActivePools,
            static pool => pool.QualityScore);
        return snapshot with
        {
            Pools = active,
            ActivePools = active,
            RecentEvents = [],
            RecentSweeps = []
        };
    }

    private static IReadOnlyList<T> RankTake<T>(
        IReadOnlyList<T> items,
        int limit,
        Func<T, decimal> score)
    {
        if (items.Count <= limit)
        {
            return items;
        }

        return items
            .OrderByDescending(score)
            .Take(limit)
            .ToArray();
    }

    private static SupplyDemandLiquidityConfluenceSnapshot? SlimConfluence(
        SupplyDemandLiquidityConfluenceSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsEnabled)
        {
            return snapshot;
        }

        // Relationships list is already bounded; keep as-is for the latest frame only.
        return snapshot;
    }
}
