import type { ReplayFrame, ReplaySeries } from './types'

export interface TimingMetrics {
  average: number
  p95: number
  maximum: number
}

export interface IntegrityMetrics {
  gaps: number
  futureSwings: number
  warmupFrames: number
}

interface SeriesMetricCache {
  firstFrameIndex: number
  lastFrameIndex: number
  frameCount: number
  intervalSeconds: number
  isForex: boolean
  timing: TimingMetrics[]
  integrity: IntegrityMetrics[]
}

const cache = new WeakMap<ReplayFrame[], SeriesMetricCache>()

export function metricsAt(
  series: ReplaySeries,
  frameIndex: number,
  isForex: boolean,
): { timing: TimingMetrics; integrity: IntegrityMetrics } {
  const metrics = getOrBuildMetrics(series, isForex)
  const index = Math.max(0, Math.min(frameIndex, metrics.frameCount - 1))
  return {
    timing: metrics.timing[index] ?? { average: 0, p95: 0, maximum: 0 },
    integrity: metrics.integrity[index] ?? { gaps: 0, futureSwings: 0, warmupFrames: 0 },
  }
}

function getOrBuildMetrics(series: ReplaySeries, isForex: boolean): SeriesMetricCache {
  const frames = series.frames
  const existing = cache.get(frames)
  const firstFrameIndex = frames[0]?.index ?? -1
  const lastFrameIndex = frames.at(-1)?.index ?? -1
  if (existing &&
    existing.frameCount === frames.length &&
    existing.firstFrameIndex === firstFrameIndex &&
    existing.lastFrameIndex === lastFrameIndex &&
    existing.intervalSeconds === series.intervalSeconds &&
    existing.isForex === isForex) {
    return existing
  }

  const built = buildMetrics(frames, series.intervalSeconds, isForex)
  cache.set(frames, built)
  return built
}

function buildMetrics(
  frames: ReplayFrame[],
  intervalSeconds: number,
  isForex: boolean,
): SeriesMetricCache {
  const timing: TimingMetrics[] = new Array(frames.length)
  const integrity: IntegrityMetrics[] = new Array(frames.length)
  const sortedDurations = [...new Set(frames.map((frame) => frame.analysisMicroseconds))]
    .sort((left, right) => left - right)
  const durationRanks = new Map(sortedDurations.map((value, index) => [value, index]))
  const durationCounts = new FenwickTree(sortedDurations.length)
  let totalDuration = 0
  let maximumDuration = 0
  let gaps = 0
  let futureSwings = 0
  let warmupFrames = 0

  frames.forEach((frame, index) => {
    const analysisDuration = frame.analysisMicroseconds
    totalDuration += analysisDuration
    maximumDuration = Math.max(maximumDuration, analysisDuration)
    durationCounts.add(durationRanks.get(analysisDuration)!, 1)
    const percentileRank = Math.max(1, Math.ceil((index + 1) * 0.95))
    const percentileIndex = durationCounts.indexAtOrder(percentileRank)
    timing[index] = {
      average: totalDuration / (index + 1),
      p95: sortedDurations[percentileIndex] ?? 0,
      maximum: maximumDuration,
    }

    if (frame.indicators.rsi == null || frame.indicators.bollingerMiddle == null) {
      warmupFrames++
    }
    if (index > 0) {
      const previous = frames[index - 1]
      const elapsed = (Date.parse(frame.availableAt) - Date.parse(previous.availableAt)) / 1_000
      if (Math.abs(elapsed - intervalSeconds) > 1 &&
        !(isForex && isExpectedForexClosure(previous.availableAt, frame.availableAt))) {
        gaps++
      }
    }
    const availableAt = Date.parse(frame.availableAt)
    for (const swing of frame.swings) {
      if (Date.parse(swing.confirmedAt) > availableAt) futureSwings++
    }
    integrity[index] = { gaps, futureSwings, warmupFrames }
  })

  return {
    firstFrameIndex: frames[0]?.index ?? -1,
    lastFrameIndex: frames.at(-1)?.index ?? -1,
    frameCount: frames.length,
    intervalSeconds,
    isForex,
    timing,
    integrity,
  }
}

function isExpectedForexClosure(previousValue: string, nextValue: string): boolean {
  const previous = new Date(previousValue)
  const next = new Date(nextValue)
  const elapsed = next.getTime() - previous.getTime()
  if (elapsed <= 0 || elapsed > 4 * 24 * 60 * 60 * 1_000) return false
  const day = new Date(Date.UTC(previous.getUTCFullYear(), previous.getUTCMonth(), previous.getUTCDate()))
  const finalDay = Date.UTC(next.getUTCFullYear(), next.getUTCMonth(), next.getUTCDate())
  while (day.getTime() <= finalDay) {
    if (day.getUTCDay() === 0 || day.getUTCDay() === 6) return true
    day.setUTCDate(day.getUTCDate() + 1)
  }
  return false
}

class FenwickTree {
  private readonly values: Uint32Array

  constructor(size: number) {
    this.values = new Uint32Array(size + 1)
  }

  add(index: number, amount: number): void {
    for (let cursor = index + 1; cursor < this.values.length; cursor += cursor & -cursor) {
      this.values[cursor] += amount
    }
  }

  indexAtOrder(order: number): number {
    let index = 0
    let step = 1
    while ((step << 1) < this.values.length) step <<= 1
    for (; step > 0; step >>= 1) {
      const next = index + step
      if (next < this.values.length && this.values[next] < order) {
        index = next
        order -= this.values[next]
      }
    }
    return Math.min(index, this.values.length - 2)
  }
}
