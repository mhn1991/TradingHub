import { computed, ref, watch, type ComputedRef, type Ref } from 'vue'
import type { ReplayFrame, ReplaySeries } from '../types'
import { parseIntervalSeconds, resampleFrames } from '../utils/timeframeSeries'

export interface IntervalOption {
  interval: string
  /** Real data (either the base series or a matching entry in `series`) vs. client-resampled — see resampleFrames. */
  real: boolean
}

const ADDABLE_PRESETS = ['1m', '5m', '15m', '30m', '1h', '2h', '4h', '1d']

/**
 * Drives "one chart, switchable timeframe" for AnalysisChart: which interval is active, what
 * frames that resolves to (real per-interval series when available, otherwise a resampled
 * fallback), and the drill-in/back history. Callers own the actual candle position (selectedIndex
 * or equivalent) — after switchInterval/drillInto/goBack, re-resolve it against the new
 * `activeFrames` via findAnchorIndex so the same instant in time stays selected across timeframes.
 */
export function useMultiTimeframeSelection(
  baseInterval: Ref<string> | ComputedRef<string>,
  baseFrames: Ref<ReplayFrame[]> | ComputedRef<ReplayFrame[]>,
  series: Ref<ReplaySeries[] | undefined> | ComputedRef<ReplaySeries[] | undefined>,
) {
  const activeInterval = ref(baseInterval.value)
  const history = ref<{ interval: string; anchorAt: string | null }[]>([])

  watch(baseInterval, (next) => {
    activeInterval.value = next
    history.value = []
  })

  const baseSeconds = computed(() => parseIntervalSeconds(baseInterval.value))

  function resolveInterval(interval: string): { frames: ReplayFrame[]; real: boolean } {
    const realSeries = series.value?.find((item) => item.interval === interval)
    if (realSeries) return { frames: realSeries.frames, real: true }
    if (interval === baseInterval.value) return { frames: baseFrames.value, real: true }
    return {
      frames: resampleFrames(baseFrames.value, baseSeconds.value, parseIntervalSeconds(interval)),
      real: false,
    }
  }

  const availableIntervals = computed<IntervalOption[]>(() => {
    const real = series.value?.map((item) => item.interval) ?? []
    const resamplable = ADDABLE_PRESETS.filter((preset) => parseIntervalSeconds(preset) > baseSeconds.value)
    const all = [...new Set([...real, baseInterval.value, ...resamplable])]
    return all
      .map((interval) => ({ interval, real: resolveInterval(interval).real }))
      .sort((a, b) => parseIntervalSeconds(a.interval) - parseIntervalSeconds(b.interval))
  })

  const activeFrames = computed(() => resolveInterval(activeInterval.value).frames)
  const activeSynthetic = computed(() => !resolveInterval(activeInterval.value).real)
  const canGoBack = computed(() => history.value.length > 0)

  function switchInterval(interval: string) {
    if (interval === activeInterval.value) return
    activeInterval.value = interval
  }

  function drillCandidates(originInterval: string) {
    const originSeconds = parseIntervalSeconds(originInterval)
    return availableIntervals.value
      .filter((item) => item.interval !== originInterval)
      // A "lower" (finer) target only makes sense with genuine real data — resampling can only
      // aggregate into a coarser bucket, never manufacture a finer one.
      .filter((item) => parseIntervalSeconds(item.interval) >= originSeconds || item.real)
      .map((item) => ({ ...item, seconds: parseIntervalSeconds(item.interval) }))
  }

  /**
   * Returns the new anchor timestamp to resolve against activeFrames, or null if there was
   * nowhere to drill (e.g. already at the finest available interval with no coarser option).
   *
   * `targetInterval` drills to an explicitly chosen timeframe — used when the caller asks the user
   * which direction to go, and when a server-fetched window makes finer intervals reachable that
   * client-side resampling could never produce. Omitting it keeps the original auto-pick (prefer
   * finer, else the first candidate), which callers without a chooser still rely on.
   */
  function drillInto(
    candleAvailableAt: string,
    currentAnchorAt: string | null,
    targetInterval?: string,
  ): string | null {
    if (targetInterval !== undefined) {
      if (targetInterval === activeInterval.value) return null
      history.value.push({ interval: activeInterval.value, anchorAt: currentAnchorAt })
      activeInterval.value = targetInterval
      return candleAvailableAt
    }

    const candidates = drillCandidates(activeInterval.value)
    const originSeconds = parseIntervalSeconds(activeInterval.value)
    const preferred = candidates.find((item) => item.seconds < originSeconds) ?? candidates[0]
    if (!preferred) return null
    history.value.push({ interval: activeInterval.value, anchorAt: currentAnchorAt })
    activeInterval.value = preferred.interval
    return candleAvailableAt
  }

  /** Returns the anchor to restore, or null if there was no history to go back to. */
  function goBack(): string | null {
    const previous = history.value.pop()
    if (!previous) return null
    activeInterval.value = previous.interval
    return previous.anchorAt
  }

  return {
    activeInterval,
    availableIntervals,
    activeFrames,
    activeSynthetic,
    canGoBack,
    switchInterval,
    drillInto,
    goBack,
  }
}
