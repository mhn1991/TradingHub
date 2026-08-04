import type { ReplayFrame } from '../types'

/** Same interval grammar as SimulatorPanel's form fields: 5m, 1h, 4h, 1d, 1w, 1mo. */
export function parseIntervalSeconds(value: string): number {
  const match = value.trim().match(/^([1-9][0-9]*)(s|m|h|d|w|mo)$/i)
  if (!match) throw new Error(`Unsupported interval "${value}" — use a form such as 5m, 1h, or 4h.`)
  const amount = Number(match[1])
  const multiplier: Record<string, number> = {
    s: 1, m: 60, h: 3_600, d: 86_400, w: 604_800, mo: 2_592_000,
  }
  return amount * multiplier[match[2]!.toLowerCase()]!
}

/** Nearest frame at or before `anchorAt` — the core "don't lose the candle" lookup. Falls back to the last frame when `anchorAt` is null (follow-latest) or later than every frame. */
export function findAnchorIndex(frames: ReplayFrame[], anchorAt: string | null): number {
  if (frames.length === 0) return -1
  if (anchorAt === null) return frames.length - 1
  const target = Date.parse(anchorAt)

  let low = 0
  let high = frames.length - 1
  let result = 0
  while (low <= high) {
    const mid = (low + high) >> 1
    const midTime = Date.parse(frames[mid]!.availableAt)
    if (midTime <= target) {
      result = mid
      low = mid + 1
    } else {
      high = mid - 1
    }
  }
  return result
}

/**
 * Aggregates a source series into coarser candles by wall-clock bucket. Only OHLCV are
 * mathematically valid to derive this way — indicators computed for the source interval do not
 * transfer to a resampled one, so callers must treat `indicators` on the result as unavailable
 * until real per-interval analysis data exists server-side.
 */
export function resampleFrames(
  source: ReplayFrame[],
  sourceIntervalSeconds: number,
  targetIntervalSeconds: number,
): ReplayFrame[] {
  if (targetIntervalSeconds <= sourceIntervalSeconds || source.length === 0) return source

  const bucketMs = targetIntervalSeconds * 1000
  const buckets = new Map<number, ReplayFrame[]>()
  for (const frame of source) {
    const bucketStart = Math.floor(Date.parse(frame.availableAt) / bucketMs) * bucketMs
    const bucket = buckets.get(bucketStart)
    if (bucket) bucket.push(frame)
    else buckets.set(bucketStart, [frame])
  }

  const emptyIndicators = {
    atr: null, rsi: null, bollingerMiddle: null, bollingerUpper: null, bollingerLower: null,
    efficiencyRatio: null,
  }

  return [...buckets.entries()]
    .sort(([a], [b]) => a - b)
    .map(([bucketStart, members], index): ReplayFrame => {
      const open = members[0]!.candle.open
      const close = members[members.length - 1]!.candle.close
      const high = Math.max(...members.map((m) => m.candle.high))
      const low = Math.min(...members.map((m) => m.candle.low))
      const volume = members.reduce((sum, m) => sum + (m.candle.volume ?? 0), 0)
      const last = members[members.length - 1]!
      return {
        index,
        availableAt: new Date(bucketStart + bucketMs).toISOString(),
        candle: {
          openTime: members[0]!.candle.openTime,
          closeTime: last.candle.closeTime,
          open, high, low, close, volume,
        },
        indicators: emptyIndicators,
        swings: [],
        priceZones: [],
        trendlines: [],
        channels: [],
        marketRegime: last.marketRegime,
        confidence: { total: 0, contributions: [] },
        analysisMicroseconds: 0,
      }
    })
}
