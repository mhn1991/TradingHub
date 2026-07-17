import { computed, onUnmounted, ref, watch, type Ref } from 'vue'
import type { ReplayFrame } from '../types'

export interface PlaybackRow {
  sequence: number
  availableAt: string
  openTime?: string
  open: number
  high: number
  low: number
  close: number
  volume?: number
  isWarmup?: boolean
  /** Additive (§7 multi-instrument clock); absent on rows from a single-instrument run. */
  instrument?: string
  analysis?: {
    indicators: ReplayFrame['indicators']
    swings: ReplayFrame['swings']
    priceZones: ReplayFrame['priceZones']
    trendlines: ReplayFrame['trendlines']
    channels: ReplayFrame['channels']
    marketStructure?: ReplayFrame['marketStructure']
    priceAction?: ReplayFrame['priceAction']
    marketRegime?: ReplayFrame['marketRegime']
    confidence: ReplayFrame['confidence']
  } | null
}

export function useSimulationPlayback(rows: Ref<PlaybackRow[]>) {
  const index = ref(0)
  const paused = ref(true)
  const speed = ref(10)
  const followLatest = ref(true)
  let raf = 0
  let lastTs = 0
  let residual = 0

  const current = computed(() => rows.value[index.value] ?? null)
  const length = computed(() => rows.value.length)

  function play() {
    paused.value = false
    lastTs = 0
    residual = 0
    schedule()
  }

  function pause() {
    paused.value = true
    if (raf) cancelAnimationFrame(raf)
    raf = 0
  }

  function step(delta: number) {
    followLatest.value = false
    index.value = Math.max(0, Math.min(rows.value.length - 1, index.value + delta))
  }

  function jumpToSequence(sequence: number) {
    followLatest.value = false
    const found = rows.value.findIndex((row) => row.sequence >= sequence)
    if (found >= 0) index.value = found
  }

  function jumpToEnd() {
    followLatest.value = true
    if (rows.value.length) index.value = rows.value.length - 1
  }

  function schedule() {
    if (paused.value) return
    raf = requestAnimationFrame((ts) => {
      if (!lastTs) lastTs = ts
      const elapsed = (ts - lastTs) / 1000
      lastTs = ts
      residual += elapsed * Math.max(1, speed.value)
      const steps = Math.floor(residual)
      if (steps > 0) {
        residual -= steps
        if (followLatest.value) {
          index.value = Math.max(0, rows.value.length - 1)
        } else {
          index.value = Math.min(rows.value.length - 1, index.value + steps)
          if (index.value >= rows.value.length - 1) followLatest.value = true
        }
      }
      schedule()
    })
  }

  watch(rows, (value) => {
    if (followLatest.value && value.length) {
      index.value = value.length - 1
    } else if (index.value >= value.length) {
      index.value = Math.max(0, value.length - 1)
    }
  })

  onUnmounted(() => {
    if (raf) cancelAnimationFrame(raf)
  })

  return {
    index,
    paused,
    speed,
    followLatest,
    current,
    length,
    play,
    pause,
    step,
    jumpToSequence,
    jumpToEnd,
  }
}
