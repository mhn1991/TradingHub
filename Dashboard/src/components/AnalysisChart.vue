<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { compact, price, shortTime, timestamp } from '../format'
import type {
  ChartLayers,
  PriceChannel,
  ReplayFrame,
  RsiRelationshipSnapshot,
  SwingPoint,
  Trendline,
  ReplayTrade,
} from '../types'

const props = defineProps<{
  frames: ReplayFrame[]
  selectedIndex: number
  windowSize: number
  layers: ChartLayers
  trades?: ReplayTrade[]
}>()

const width = 1240
// Slightly taller canvas to fit CCI without crowding price/RSI/ATR/ER.
const height = 700
const plotLeft = 24
const plotRight = 92
const plotWidth = width - plotLeft - plotRight
const regimeTop = 12
const regimeHeight = 8
const priceTop = 24
const priceHeight = 260
const volumeTop = 292
const volumeHeight = 40
const rsiTop = 340
const rsiHeight = 56
const cciTop = 404
const cciHeight = 56
const atrTop = 468
const atrHeight = 52
const erTop = 528
const erHeight = 50
const erDomain = { min: 0, max: 1 }
const minimumWindowSize = 20

const chartShell = ref<HTMLElement | null>(null)
const chartSvg = ref<SVGSVGElement | null>(null)
const hoveredIndex = ref<number | null>(null)
const localWindowSize = ref(props.windowSize)
const panOffset = ref(0)
const dragging = ref(false)

let dragStartClientX = 0
let dragStartOffset = 0
let svgBoundsLeft = 0
let svgBoundsWidth = 1
let pendingPointerX: number | null = null
let pointerAnimationFrame: number | null = null
let pendingWheelX = 0
let pendingWheelY = 0
let pendingWheelShift = false
let wheelAnimationFrame: number | null = null
let pendingSliderOffset: number | null = null
let sliderAnimationFrame: number | null = null
let resizeObserver: ResizeObserver | null = null

const availableFrameCount = computed(() => Math.max(0, Math.min(props.frames.length, props.selectedIndex + 1)))
const requestedWindowCount = computed(() => {
  const available = availableFrameCount.value
  if (available === 0) return 0
  if (localWindowSize.value <= 0) return available
  return Math.max(1, Math.min(localWindowSize.value, available))
})
const maximumPanOffset = computed(() =>
  Math.max(0, props.selectedIndex - requestedWindowCount.value + 1),
)
const viewportEndIndex = computed(() =>
  Math.max(0, props.selectedIndex - Math.min(panOffset.value, maximumPanOffset.value)),
)
const viewportStartIndex = computed(() =>
  Math.max(0, viewportEndIndex.value - requestedWindowCount.value + 1),
)
const visibleFrames = computed(() =>
  props.frames.slice(viewportStartIndex.value, viewportEndIndex.value + 1),
)
const visibleOpenTimes = computed(() =>
  visibleFrames.value.map((frame) => new Date(frame.candle.openTime).getTime()),
)
const analysisFrame = computed(() => props.frames[viewportEndIndex.value])
const isFollowingLatest = computed(() => panOffset.value === 0)
const step = computed(() => plotWidth / Math.max(visibleFrames.value.length, 1))
// Grow with zoom so candle bodies fill most of each slot instead of leaving large gaps.
const candleWidth = computed(() => {
  const spacing = step.value
  if (spacing <= 1.5) return 1
  // Keep a thin gap between neighbors; never wider than the slot itself.
  return Math.max(1.5, Math.min(spacing * 0.78, spacing - 1.25))
})
const viewportLabel = computed(() => {
  if (!visibleFrames.value.length) return 'No candles'
  return `${viewportStartIndex.value + 1}–${viewportEndIndex.value + 1} of ${availableFrameCount.value}`
})
const viewportPosition = computed({
  get: () => Math.max(0, maximumPanOffset.value - panOffset.value),
  set: (value: number) => {
    pendingSliderOffset = Math.max(0, maximumPanOffset.value - Number(value))
    if (sliderAnimationFrame != null) return
    sliderAnimationFrame = requestAnimationFrame(() => {
      sliderAnimationFrame = null
      if (pendingSliderOffset == null) return
      panOffset.value = pendingSliderOffset
      pendingSliderOffset = null
    })
  },
})

watch(() => props.windowSize, (value) => {
  localWindowSize.value = value
  clampPanOffset()
})

watch(() => props.selectedIndex, (next, previous) => {
  const difference = next - previous
  if (difference === 1 && panOffset.value > 0) {
    // Keep the historical viewport anchored while live/replay data advances.
    panOffset.value = Math.min(maximumPanOffset.value, panOffset.value + 1)
  } else if (difference < 0 || Math.abs(difference) > 1) {
    panOffset.value = 0
  }
  clampPanOffset()
})

watch([viewportStartIndex, viewportEndIndex], () => {
  hoveredIndex.value = null
})

const priceDomain = computed(() => {
  let min = Number.POSITIVE_INFINITY
  let max = Number.NEGATIVE_INFINITY
  const include = (value: number | null | undefined) => {
    if (value == null || !Number.isFinite(value)) return
    min = Math.min(min, value)
    max = Math.max(max, value)
  }

  for (const frame of visibleFrames.value) {
    include(frame.candle.high)
    include(frame.candle.low)
    if (props.layers.bollinger) {
      include(frame.indicators.bollingerUpper)
      include(frame.indicators.bollingerLower)
    }
    if (props.layers.movingAverages) {
      include(frame.indicators.sma50)
      include(frame.indicators.sma200)
    }
    if (props.layers.donchian) {
      include(frame.indicators.donchian?.upper)
      include(frame.indicators.donchian?.lower)
    }
  }

  if (!Number.isFinite(min) || !Number.isFinite(max)) return { min: 0, max: 1 }
  const padding = Math.max((max - min) * 0.08, Math.abs(max) * 0.0004, 0.000001)
  return { min: min - padding, max: max + padding }
})

const atrDomain = computed(() => {
  let min = Number.POSITIVE_INFINITY
  let max = Number.NEGATIVE_INFINITY
  for (const frame of visibleFrames.value) {
    const value = frame.indicators.atrAnalysis?.normalizedPercent
    if (value == null || !Number.isFinite(value)) continue
    min = Math.min(min, value)
    max = Math.max(max, value)
  }
  if (!Number.isFinite(min) || !Number.isFinite(max)) return { min: 0, max: 1 }
  const span = Math.max(max - min, Math.abs(max) * 0.08, 0.0001)
  return { min: Math.max(0, min - span * 0.12), max: max + span * 0.12 }
})

const priceTicks = computed(() =>
  Array.from({ length: 6 }, (_, index) => {
    const ratio = index / 5
    const value = priceDomain.value.max -
      (priceDomain.value.max - priceDomain.value.min) * ratio
    return { value, y: priceTop + priceHeight * ratio }
  }),
)

const atrTicks = computed(() =>
  Array.from({ length: 3 }, (_, index) => {
    const ratio = index / 2
    const value = atrDomain.value.max -
      (atrDomain.value.max - atrDomain.value.min) * ratio
    return { value, y: atrTop + atrHeight * ratio }
  }),
)

const erTicks = computed(() =>
  Array.from({ length: 3 }, (_, index) => {
    const ratio = index / 2
    const value = erDomain.max - (erDomain.max - erDomain.min) * ratio
    return { value, y: erTop + erHeight * ratio }
  }),
)

const timeTicks = computed(() => {
  const frames = visibleFrames.value
  if (!frames.length) return []
  const positions = [0, 0.2, 0.4, 0.6, 0.8, 1]
  return [...new Set(positions.map((ratio) =>
    Math.min(frames.length - 1, Math.round((frames.length - 1) * ratio)),
  ))].map((index) => ({
    index,
    x: xAt(index),
    label: shortTime(frames[index].availableAt),
  }))
})

const candlePaths = computed(() => {
  const upWicks: string[] = []
  const downWicks: string[] = []
  const upBodies: string[] = []
  const downBodies: string[] = []
  const bodyWidth = candleWidth.value

  visibleFrames.value.forEach((frame, index) => {
    const x = xAt(index)
    const openY = yPrice(frame.candle.open)
    const closeY = yPrice(frame.candle.close)
    const wick = `M${pathNumber(x)} ${pathNumber(yPrice(frame.candle.high))}V${pathNumber(yPrice(frame.candle.low))}`
    const body = rectanglePath(
      x - bodyWidth / 2,
      bodyY(openY, closeY),
      bodyWidth,
      bodyHeight(openY, closeY),
    )
    if (frame.candle.close >= frame.candle.open) {
      upWicks.push(wick)
      upBodies.push(body)
    } else {
      downWicks.push(wick)
      downBodies.push(body)
    }
  })

  return {
    upWicks: upWicks.join(''),
    downWicks: downWicks.join(''),
    upBodies: upBodies.join(''),
    downBodies: downBodies.join(''),
  }
})

const volumePaths = computed(() => {
  let maximumVolume = 1
  for (const frame of visibleFrames.value) {
    maximumVolume = Math.max(maximumVolume, frame.candle.volume)
  }

  const up: string[] = []
  const down: string[] = []
  const bodyWidth = candleWidth.value
  visibleFrames.value.forEach((frame, index) => {
    const barHeight = Math.max(1, volumeHeight * frame.candle.volume / maximumVolume)
    const bar = rectanglePath(
      xAt(index) - bodyWidth / 2,
      volumeTop + volumeHeight - barHeight,
      bodyWidth,
      barHeight,
    )
    ;(frame.candle.close >= frame.candle.open ? up : down).push(bar)
  })
  return { up: up.join(''), down: down.join('') }
})

const bollingerBand = computed(() => {
  const upper: string[] = []
  const lower: string[] = []
  visibleFrames.value.forEach((frame, index) => {
    if (frame.indicators.bollingerUpper == null || frame.indicators.bollingerLower == null) return
    upper.push(`${xAt(index)},${yPrice(frame.indicators.bollingerUpper)}`)
    lower.push(`${xAt(index)},${yPrice(frame.indicators.bollingerLower)}`)
  })
  if (upper.length < 2) return ''
  lower.reverse()
  return [...upper, ...lower].join(' ')
})

const bollingerUpper = computed(() => indicatorPath('bollingerUpper'))
const bollingerMiddle = computed(() => indicatorPath('bollingerMiddle'))
const bollingerLower = computed(() => indicatorPath('bollingerLower'))
const sma50Path = computed(() => numericPath(
  (frame) => frame.indicators.sma50 ?? null,
  yPrice,
))
const sma200Path = computed(() => numericPath(
  (frame) => frame.indicators.sma200 ?? null,
  yPrice,
))
const rsiPath = computed(() => numericPath(
  (frame) => frame.indicators.rsi,
  yRsi,
))
const cciPath = computed(() => numericPath(
  (frame) => frame.indicators.cci ?? null,
  yCci,
))
const atrPath = computed(() => numericPath(
  (frame) => frame.indicators.atrAnalysis?.normalizedPercent ?? null,
  yAtr,
))

const cciDomain = computed(() => {
  let min = Number.POSITIVE_INFINITY
  let max = Number.NEGATIVE_INFINITY
  for (const frame of visibleFrames.value) {
    const value = frame.indicators.cci
    if (value == null || !Number.isFinite(value)) continue
    min = Math.min(min, value)
    max = Math.max(max, value)
  }
  if (!Number.isFinite(min) || !Number.isFinite(max)) return { min: -200, max: 200 }
  // Keep classic ±100 bands visible even when CCI is quiet.
  min = Math.min(min, -100)
  max = Math.max(max, 100)
  const padding = Math.max((max - min) * 0.08, 10)
  return { min: min - padding, max: max + padding }
})

const donchianBand = computed(() => {
  const upper: string[] = []
  const lower: string[] = []
  visibleFrames.value.forEach((frame, index) => {
    const donchianUpperValue = frame.indicators.donchian?.upper
    const donchianLowerValue = frame.indicators.donchian?.lower
    if (donchianUpperValue == null || donchianLowerValue == null) return
    upper.push(`${xAt(index)},${yPrice(donchianUpperValue)}`)
    lower.push(`${xAt(index)},${yPrice(donchianLowerValue)}`)
  })
  if (upper.length < 2) return ''
  lower.reverse()
  return [...upper, ...lower].join(' ')
})
const donchianUpper = computed(() => numericPath(
  (frame) => frame.indicators.donchian?.upper ?? null,
  yPrice,
))
const donchianLower = computed(() => numericPath(
  (frame) => frame.indicators.donchian?.lower ?? null,
  yPrice,
))

const erPath = computed(() => numericPath(
  (frame) => frame.indicators.efficiencyRatio,
  yEr,
))
const erStatePaths = computed(() => groupedRectanglePaths(
  (frame) => frame.indicators.efficiencyAnalysis?.state ?? 'Unknown',
  erTop,
  erHeight,
))

const regimeBandPaths = computed(() => groupedRectanglePaths(
  (frame) => frame.marketRegime?.regime ?? 'Unknown',
  regimeTop,
  regimeHeight,
))

const narrowSpans = computed(() => buildSpans((frame) =>
  frame.indicators.bollingerAnalysis?.widthRegime === 'Narrow',
))
const squeezeSpans = computed(() => buildSpans((frame) =>
  frame.indicators.bollingerAnalysis?.isSqueeze === true,
))
const wideSpans = computed(() => buildSpans((frame) =>
  frame.indicators.bollingerAnalysis?.widthRegime === 'Wide',
))
const expansionSpans = computed(() => buildSpans((frame) =>
  frame.indicators.bollingerAnalysis?.isExpansion === true,
))
const bollingerDirectionPaths = computed(() => groupedRectanglePaths(
  (frame) => frame.indicators.bollingerAnalysis?.widthDirection ?? 'Unknown',
  priceTop,
  5,
))
const squeezeReleaseMarkers = computed(() => visibleFrames.value
  .map((frame, index) => ({ frame, index }))
  .filter(({ frame }) => frame.indicators.bollingerAnalysis?.squeezeReleased === true)
  .map(({ frame, index }) => ({ frame, x: xAt(index) })))
const atrRegimePaths = computed(() => groupedRectanglePaths(
  (frame) => frame.indicators.atrAnalysis?.regime ?? 'Unknown',
  atrTop,
  atrHeight,
))

const rsiRelationshipVisuals = computed(() => {
  const unique = new Map<string, RsiRelationshipSnapshot>()
  for (const frame of visibleFrames.value) {
    const relationship = frame.indicators.rsiAnalysis?.latestRelationship
    if (!relationship || relationship.type === 'None') continue
    const key = [
      relationship.type,
      relationship.firstPivotTime,
      relationship.secondPivotTime,
      relationship.confirmedAt,
    ].join('|')
    unique.set(key, relationship)
  }

  const firstVisible = new Date(visibleFrames.value[0]?.candle.openTime ?? 0).getTime()
  const lastVisible = new Date(visibleFrames.value.at(-1)?.candle.closeTime ?? 0).getTime()
  return [...unique.values()]
    .filter((relationship) => {
      const first = new Date(relationship.firstPivotTime).getTime()
      const second = new Date(relationship.secondPivotTime).getTime()
      return first >= firstVisible && second <= lastVisible
    })
    .map((relationship) => ({
      relationship,
      x1: xForTime(relationship.firstPivotTime),
      x2: xForTime(relationship.secondPivotTime),
      priceY1: yPrice(relationship.firstPrice),
      priceY2: yPrice(relationship.secondPrice),
      rsiY1: yRsi(relationship.firstRsi),
      rsiY2: yRsi(relationship.secondRsi),
      className: relationshipClass(relationship),
      label: relationshipLabel(relationship.type),
    }))
})

const zoneVisuals = computed(() => (analysisFrame.value?.priceZones ?? [])
  .filter((zone) =>
    zone.upperPrice >= priceDomain.value.min && zone.lowerPrice <= priceDomain.value.max,
  )
  .sort((left, right) => right.strength - left.strength)
  .slice(0, 8)
  .map((zone) => {
    const upper = Math.min(priceDomain.value.max, zone.upperPrice)
    const lower = Math.max(priceDomain.value.min, zone.lowerPrice)
    return {
      ...zone,
      y: yPrice(upper),
      centreY: yPrice(Math.max(lower, Math.min(upper, zone.centrePrice))),
      height: Math.max(2, yPrice(lower) - yPrice(upper)),
    }
  }))

const supplyDemandZoneVisuals = computed(() => {
  const snapshot = analysisFrame.value?.supplyDemand
  if (!snapshot?.isEnabled) return []
  const zones = snapshot.zones ?? snapshot.activeZones ?? []
  const events = snapshot.recentEvents ?? []
  return zones
    .filter((zone) => Math.max(zone.proximalPrice, zone.distalPrice) >= priceDomain.value.min
      && Math.min(zone.proximalPrice, zone.distalPrice) <= priceDomain.value.max)
    .sort((left, right) => right.qualityScore - left.qualityScore)
    .slice(0, 32)
    .map((zone) => {
      const upper = Math.min(priceDomain.value.max, Math.max(zone.proximalPrice, zone.distalPrice))
      const lower = Math.max(priceDomain.value.min, Math.min(zone.proximalPrice, zone.distalPrice))
      const terminal = events
        .filter((event) => event.zoneId === zone.zoneId
          && ['Invalidated', 'Expired', 'Merged'].includes(event.eventType))
        .at(-1)
      const startX = xForTime(zone.availableAt)
      const endX = terminal ? xForTime(terminal.availableAt) : plotLeft + plotWidth
      const confluenceCount = analysisFrame.value?.supplyDemandLiquidityConfluence?.relationships
        ?.filter((item) => item.zoneId === zone.zoneId).length ?? 0
      return {
        ...zone,
        x: startX,
        width: Math.max(1, endX - startX),
        y: yPrice(upper),
        height: Math.max(2, yPrice(lower) - yPrice(upper)),
        confluenceCount,
      }
    })
})

const liquidityPoolVisuals = computed(() => {
  const snapshot = analysisFrame.value?.liquidity
  if (!snapshot?.isEnabled) return []
  const events = snapshot.recentEvents ?? []
  return (snapshot.pools ?? snapshot.activePools ?? [])
    .filter((pool) => pool.upperPrice >= priceDomain.value.min && pool.lowerPrice <= priceDomain.value.max)
    .sort((left, right) => right.qualityScore - left.qualityScore)
    .slice(0, 40)
    .map((pool) => {
      const upper = Math.min(priceDomain.value.max, Math.max(pool.upperPrice, pool.lowerPrice))
      const lower = Math.max(priceDomain.value.min, Math.min(pool.upperPrice, pool.lowerPrice))
      const terminal = events
        .filter((event) => event.poolId === pool.poolId
          && ['Sweep', 'AcceptedBreak', 'Consumption', 'Failure'].includes(event.eventType))
        .at(-1)
      const startX = xForTime(pool.availableAt)
      const endX = terminal ? xForTime(terminal.availableAt) : plotLeft + plotWidth
      return {
        ...pool,
        x: startX,
        width: Math.max(1, endX - startX),
        y: yPrice(upper),
        height: Math.max(3, yPrice(lower) - yPrice(upper)),
        centreY: yPrice(pool.referencePrice),
      }
    })
})

const liquidityEventVisuals = computed(() => {
  const snapshot = analysisFrame.value?.liquidity
  if (!snapshot?.isEnabled) return []
  return (snapshot.recentEvents ?? [])
    .filter((event) => isTimeVisible(event.availableAt)
      && ['Sweep', 'AcceptedBreak', 'Consumption'].includes(event.eventType))
    .map((event) => ({ ...event, x: xForTime(event.availableAt), y: yPrice(event.price) }))
})

const trendVisuals = computed(() => {
  const frames = visibleFrames.value
  if (!frames.length) return []
  const viewportStart = new Date(frames[0].candle.openTime).getTime()
  const viewportEnd = new Date(frames.at(-1)!.candle.closeTime).getTime()

  return (analysisFrame.value?.trendlines ?? []).flatMap((line, index) => {
    const lineStart = trendStartTime(line)
    const lineEnd = trendEndTime(line)
    if (lineStart > viewportEnd) return []

    const observedStart = Math.max(viewportStart, lineStart)
    const observedEnd = Math.min(viewportEnd, lineEnd)
    const projectedStart = Math.max(viewportStart, lineEnd)
    const key = `${line.type}-${lineStart}-${lineEnd}-${index}`
    return [{
      key,
      line,
      observed: observedEnd > observedStart
        ? trendSegment(line, observedStart, observedEnd)
        : null,
      projected: viewportEnd > projectedStart
        ? trendSegment(line, projectedStart, viewportEnd)
        : null,
      startPoint: lineStart >= viewportStart && lineStart <= viewportEnd
        ? trendPoint(line, lineStart)
        : null,
      endPoint: lineEnd >= viewportStart && lineEnd <= viewportEnd
        ? trendPoint(line, lineEnd)
        : null,
    }]
  })
})

const channelVisuals = computed(() => {
  const frames = visibleFrames.value
  if (!frames.length) return []
  const viewportStart = new Date(frames[0].candle.openTime).getTime()
  const viewportEnd = new Date(frames.at(-1)!.candle.closeTime).getTime()

  return (analysisFrame.value?.channels ?? []).slice(0, 6).flatMap((channel, index) => {
    const channelStart = channelStartTime(channel)
    const channelEnd = channelEndTime(channel)
    if (channelStart > viewportEnd) return []

    const observedStart = Math.max(viewportStart, channelStart)
    const observedEnd = Math.min(viewportEnd, channelEnd)
    const projectedStart = Math.max(viewportStart, channelEnd)
    return [{
      key: `${channelStart}-${channelEnd}-${index}`,
      channel,
      observedPoints: observedEnd > observedStart
        ? channelPolygon(channel, observedStart, observedEnd)
        : null,
      projectedPoints: viewportEnd > projectedStart
        ? channelPolygon(channel, projectedStart, viewportEnd)
        : null,
    }]
  })
})

const swingVisuals = computed(() => (analysisFrame.value?.swings ?? [])
  .filter((swing) => isTimeVisible(swing.pivotTime))
  .map((swing) => ({
    swing,
    x: xForTime(swing.pivotTime),
    y: yPrice(swing.price),
    fresh: swing.confirmedAt === analysisFrame.value?.availableAt,
  })))

const neoWaveVisuals = computed(() => {
  if (!props.layers.neoWave) return []
  const waveSnapshot = analysisFrame.value?.neoWave
  if (!waveSnapshot?.enabled) return []
  const preferred = waveSnapshot.hypotheses.find(item => item.hypothesisId === waveSnapshot.preferredHypothesisId)
  const preferredIds = new Set(preferred?.componentWaveIds ?? [])
  // Number monowaves by full history order so labels stay stable when the viewport clips older legs.
  return (waveSnapshot.confirmedMonoWaves ?? [])
    .map((wave, index) => ({ wave, index: index + 1 }))
    .filter(item => intersectsVisibleTimeRange(item.wave.startTime, item.wave.endTime))
    .map((item) => ({
      wave: item.wave,
      index: item.index,
      x1: xForTime(item.wave.startTime),
      y1: yPrice(item.wave.startPrice),
      x2: xForTime(item.wave.endTime),
      y2: yPrice(item.wave.endPrice),
      preferred: preferredIds.has(item.wave.waveId),
    }))
})

const provisionalNeoWaveVisual = computed(() => {
  if (!props.layers.neoWave) return null
  const wave = analysisFrame.value?.neoWave?.provisionalWave
  if (!wave) return null
  return {
    wave,
    x1: xForTime(wave.startTime),
    y1: yPrice(wave.startPrice),
    x2: xForTime(wave.currentTime),
    y2: yPrice(wave.currentPrice),
  }
})

const priceActionVisuals = computed(() => {
  if (props.layers.priceAction === false) return []
  const events = new Map<string, NonNullable<ReplayFrame['priceAction']>['events'][number]>()
  for (const frame of visibleFrames.value) {
    for (const event of frame.priceAction?.events ?? []) {
      if (event.confidence < 55) continue
      if (event.type.endsWith('Impulse') || event.type.endsWith('Pullback')) continue
      events.set(event.eventId, event)
    }
  }

  return [...events.values()]
    .filter(event => isTimeVisible(event.confirmedAt))
    .map(event => {
      const bullish = event.direction === 'Bullish'
      const reference = event.referenceLevel ?? event.brokenLevel ?? priceForTime(event.confirmedAt)
      const y = yPrice(reference)
      return {
        event,
        bullish,
        x: xForTime(event.confirmedAt),
        y,
        points: priceActionPoints(bullish, xForTime(event.confirmedAt), y),
      }
    })
})

function stopVisual(trade: ReplayTrade) {
  if (!trade.openedAt) return { path: '', accepted: [], rejected: [] }
  const initial = trade.initialStopLossPrice ?? trade.stopLossPrice
  if (initial == null) return { path: '', accepted: [], rejected: [] }
  const endTime = trade.closedAt ?? visibleFrames.value.at(-1)?.availableAt ?? trade.openedAt
  const amendments = [...(trade.stopAmendments ?? [])]
    .sort((left, right) => new Date(left.requestedAt).getTime() - new Date(right.requestedAt).getTime())
  const accepted = amendments.filter(item => item.status === 'Accepted' || item.status === 'Replaced')
  const rejected = amendments.filter(item => item.status === 'Rejected' || item.status === 'Unsupported')
  let current = initial
  let path = `M${xForTime(trade.openedAt)} ${yPrice(current)}`
  const acceptedMarkers = [] as Array<{ amendment: (typeof amendments)[number]; x: number; y: number }>
  for (const amendment of accepted) {
    const stamp = amendment.acceptedAt ?? amendment.requestedAt
    const next = amendment.acceptedStopPrice ?? amendment.proposedStopPrice
    const x = xForTime(stamp)
    path += ` L${x} ${yPrice(current)} L${x} ${yPrice(next)}`
    current = next
    acceptedMarkers.push({ amendment, x, y: yPrice(next) })
  }
  path += ` L${xForTime(endTime)} ${yPrice(current)}`
  return {
    path,
    accepted: acceptedMarkers,
    rejected: rejected.map(amendment => ({
      amendment,
      x: xForTime(amendment.requestedAt),
      y: yPrice(amendment.proposedStopPrice),
    })),
  }
}

const tradeVisuals = computed(() => (props.trades ?? [])
  .filter((trade) =>
    isTimeVisible(trade.setupStartedAt) ||
    (trade.confirmationAt && isTimeVisible(trade.confirmationAt)) ||
    (trade.openedAt && isTimeVisible(trade.openedAt)) ||
    (trade.closedAt && isTimeVisible(trade.closedAt)) ||
    (trade.partialExits ?? []).some(exit => isTimeVisible(exit.executedAt)) ||
    isTimeVisible(trade.signalCreatedAt))
  .map((trade) => ({
    trade,
    setupVisible: isTimeVisible(trade.setupStartedAt),
    setupX: xForTime(trade.setupStartedAt),
    setupY: yPrice(priceForTime(trade.setupStartedAt)),
    confirmationVisible: trade.confirmationAt ? isTimeVisible(trade.confirmationAt) : false,
    confirmationX: trade.confirmationAt ? xForTime(trade.confirmationAt) : null,
    confirmationY: trade.confirmationAt ? yPrice(priceForTime(trade.confirmationAt)) : null,
    signalVisible: isTimeVisible(trade.signalCreatedAt),
    signalX: xForTime(trade.signalCreatedAt),
    signalY: yPrice(trade.signalPrice ?? trade.entryPrice ?? priceForTime(trade.signalCreatedAt)),
    entryVisible: trade.openedAt ? isTimeVisible(trade.openedAt) : false,
    entryX: trade.openedAt ? xForTime(trade.openedAt) : null,
    entryY: trade.entryPrice == null ? null : yPrice(trade.entryPrice),
    exitVisible: trade.closedAt ? isTimeVisible(trade.closedAt) : false,
    exitX: trade.closedAt ? xForTime(trade.closedAt) : null,
    exitY: trade.exitPrice == null ? null : yPrice(trade.exitPrice),
    partialExits: (trade.partialExits ?? [])
      .filter(exit => isTimeVisible(exit.executedAt))
      .map(exit => ({ exit, x: xForTime(exit.executedAt), y: yPrice(exit.exitPrice) })),
    stop: stopVisual(trade),
    targetY: trade.takeProfitPrice == null ? null : yPrice(trade.takeProfitPrice),
    profitable: trade.netProfitLoss >= 0,
  })))

const isHovering = computed(() => hoveredIndex.value != null)
const focusIndex = computed(() =>
  hoveredIndex.value ?? Math.max(0, visibleFrames.value.length - 1),
)
const focusFrame = computed(() => visibleFrames.value[focusIndex.value] ?? null)
const hoverX = computed(() => xAt(focusIndex.value))
const lastPrice = computed(() => analysisFrame.value?.candle.close ?? null)
const lastPriceY = computed(() => lastPrice.value == null ? null : yPrice(lastPrice.value))
const focusCandle = computed(() => focusFrame.value?.candle ?? null)
const focusChange = computed(() => {
  const candle = focusCandle.value
  if (!candle || !Number.isFinite(candle.open) || candle.open === 0) {
    return { delta: 0, percent: 0, bullish: true, label: '—' }
  }
  const delta = candle.close - candle.open
  const percent = (delta / candle.open) * 100
  const sign = percent >= 0 ? '+' : ''
  return {
    delta,
    percent,
    bullish: delta >= 0,
    label: `${sign}${percent.toFixed(3)}%`,
  }
})
const viewportRange = computed(() => {
  const frames = visibleFrames.value
  if (!frames.length) return null
  let high = Number.NEGATIVE_INFINITY
  let low = Number.POSITIVE_INFINITY
  let highIndex = 0
  let lowIndex = 0
  frames.forEach((frame, index) => {
    if (frame.candle.high > high) {
      high = frame.candle.high
      highIndex = index
    }
    if (frame.candle.low < low) {
      low = frame.candle.low
      lowIndex = index
    }
  })
  if (!Number.isFinite(high) || !Number.isFinite(low)) return null
  const open = frames[0].candle.open
  const close = frames.at(-1)!.candle.close
  const movePercent = open !== 0 ? ((close - open) / open) * 100 : 0
  return {
    high,
    low,
    highIndex,
    lowIndex,
    open,
    close,
    movePercent,
    moveLabel: `${movePercent >= 0 ? '+' : ''}${movePercent.toFixed(3)}%`,
    bullish: movePercent >= 0,
  }
})
const focusIndicators = computed(() => {
  const frame = focusFrame.value
  if (!frame) return null
  const donchian = frame.indicators.donchian
  const cciAnalysis = frame.indicators.cciAnalysis
  const cciRelationship = cciAnalysis?.latestRelationship
  const cciCrossings = [
    cciAnalysis?.crossedUpFromExtremeNegative ? '↑ from -extreme' : null,
    cciAnalysis?.crossedDownFromExtremePositive ? '↓ from +extreme' : null,
    cciAnalysis?.crossedUpZero ? '↑ zero' : null,
    cciAnalysis?.crossedDownZero ? '↓ zero' : null,
  ].filter((value): value is string => value != null)
  return {
    time: timestamp(frame.availableAt),
    rsi: frame.indicators.rsi?.toFixed(1) ?? '—',
    cci: frame.indicators.cci?.toFixed(1) ?? '—',
    cciState: cciAnalysis
      ? `${cciAnalysis.zone} · ${cciAnalysis.momentumDirection}${cciAnalysis.momentumChange != null ? ` ${cciAnalysis.momentumChange >= 0 ? '+' : ''}${cciAnalysis.momentumChange.toFixed(1)}` : ''}`
      : '—',
    cciCrossing: cciCrossings.length ? cciCrossings.join(', ') : '—',
    cciRelationship: cciRelationship
      ? `${cciRelationship.type} · age ${cciRelationship.ageCandles} · strength ${cciRelationship.strength.toFixed(2)}`
      : '—',
    sma50: frame.indicators.sma50 != null ? price(frame.indicators.sma50) : '—',
    sma200: frame.indicators.sma200 != null ? price(frame.indicators.sma200) : '—',
    atr: frame.indicators.atrAnalysis?.normalizedPercent?.toFixed(3) ?? '—',
    atrRegime: frame.indicators.atrAnalysis?.regime ?? 'Unknown',
    bb: frame.indicators.bollingerAnalysis?.widthRegime ?? '—',
    bbDirection: frame.indicators.bollingerAnalysis?.widthDirection ?? '—',
    adx: frame.indicators.adxAnalysis?.adx?.toFixed(1) ?? '—',
    er: frame.indicators.efficiencyRatio?.toFixed(2) ?? '—',
    erState: frame.indicators.efficiencyAnalysis?.state ?? 'Unknown',
    pa: frame.priceAction?.bias ?? 'Neutral',
    regime: frame.marketRegime?.regime ?? 'Unknown',
    regimeConfidence: frame.marketRegime?.confidence?.toFixed(0) ?? '0',
    structure: frame.marketStructure?.direction ?? 'Unknown',
    donchian: donchian?.upper != null && donchian.lower != null
      ? `${price(donchian.lower)}–${price(donchian.upper)}`
      : '—',
  }
})
const panelBadges = computed(() => {
  const frame = analysisFrame.value
  if (!frame) return null
  return {
    rsi: frame.indicators.rsi?.toFixed(1) ?? null,
    cci: frame.indicators.cci?.toFixed(1) ?? null,
    atr: frame.indicators.atrAnalysis?.normalizedPercent?.toFixed(3) ?? null,
    er: frame.indicators.efficiencyRatio?.toFixed(2) ?? null,
    volume: compact(frame.candle.volume),
  }
})

function clampPanOffset() {
  panOffset.value = Math.max(0, Math.min(panOffset.value, maximumPanOffset.value))
}

function panBy(candles: number) {
  panOffset.value = Math.max(0, Math.min(maximumPanOffset.value, panOffset.value + candles))
}

function panPage(direction: 'older' | 'newer') {
  const amount = Math.max(1, Math.round(Math.max(visibleFrames.value.length, 1) * 0.35))
  panBy(direction === 'older' ? amount : -amount)
}

function goToLatest() {
  panOffset.value = 0
}

function fitAll() {
  localWindowSize.value = 0
  panOffset.value = 0
}

function zoom(multiplier: number) {
  const available = availableFrameCount.value
  if (available <= 1) return
  const current = requestedWindowCount.value || available
  const next = Math.max(
    Math.min(minimumWindowSize, available),
    Math.min(available, Math.round(current * multiplier)),
  )
  localWindowSize.value = next >= available ? 0 : next
  clampPanOffset()
}

function handleWheel(event: WheelEvent) {
  event.preventDefault()
  pendingWheelX += event.deltaX
  pendingWheelY += event.deltaY
  pendingWheelShift ||= event.shiftKey
  if (wheelAnimationFrame != null) return

  wheelAnimationFrame = requestAnimationFrame(() => {
    wheelAnimationFrame = null
    const deltaX = pendingWheelX
    const deltaY = pendingWheelY
    const shiftKey = pendingWheelShift
    pendingWheelX = 0
    pendingWheelY = 0
    pendingWheelShift = false

    // Horizontal wheel / shift+wheel pans; vertical wheel zooms candle count.
    if (shiftKey || Math.abs(deltaX) > Math.abs(deltaY)) {
      const delta = deltaX !== 0 ? deltaX : deltaY
      if (delta === 0) return
      const amount = Math.max(1, Math.round(Math.abs(delta) / 40))
      panBy(delta > 0 ? -amount : amount)
      return
    }
    if (deltaY !== 0) zoom(deltaY < 0 ? 0.8 : 1.25)
  })
}

function refreshSvgBounds(target?: SVGSVGElement | null) {
  const element = target ?? chartSvg.value
  if (!element) return
  const bounds = element.getBoundingClientRect()
  svgBoundsLeft = bounds.left
  svgBoundsWidth = Math.max(bounds.width, 1)
}

function handlePointerEnter(event: PointerEvent) {
  refreshSvgBounds(event.currentTarget as SVGSVGElement)
}

function handlePointerDown(event: PointerEvent) {
  if (event.button !== 0 || visibleFrames.value.length <= 1) return
  const target = event.currentTarget as SVGSVGElement
  refreshSvgBounds(target)
  cancelPendingPointerMove()
  dragging.value = true
  dragStartClientX = event.clientX
  dragStartOffset = panOffset.value
  target.setPointerCapture(event.pointerId)
}

function applyPointerPosition(clientX: number) {
  if (dragging.value) {
    const deltaSvg = ((clientX - dragStartClientX) / svgBoundsWidth) * width
    const deltaCandles = Math.round(deltaSvg / Math.max(step.value, 0.0001))
    const nextOffset = Math.max(
      0,
      Math.min(maximumPanOffset.value, dragStartOffset + deltaCandles),
    )
    if (nextOffset !== panOffset.value) panOffset.value = nextOffset
    return
  }

  const relativeX = ((clientX - svgBoundsLeft) / svgBoundsWidth) * width
  const nextIndex = Math.max(
    0,
    Math.min(
      visibleFrames.value.length - 1,
      Math.floor((relativeX - plotLeft) / Math.max(step.value, 0.0001)),
    ),
  )
  if (nextIndex !== hoveredIndex.value) hoveredIndex.value = nextIndex
}

function cancelPendingPointerMove() {
  if (pointerAnimationFrame != null) cancelAnimationFrame(pointerAnimationFrame)
  pointerAnimationFrame = null
  pendingPointerX = null
}

function handlePointerLeave() {
  if (dragging.value) return
  cancelPendingPointerMove()
  hoveredIndex.value = null
}

function handlePointerMove(event: PointerEvent) {
  pendingPointerX = event.clientX
  if (pointerAnimationFrame != null) return
  pointerAnimationFrame = requestAnimationFrame(() => {
    pointerAnimationFrame = null
    if (pendingPointerX == null) return
    const clientX = pendingPointerX
    pendingPointerX = null
    applyPointerPosition(clientX)
  })
}

function handlePointerUp(event: PointerEvent) {
  if (dragging.value) applyPointerPosition(event.clientX)
  cancelPendingPointerMove()
  dragging.value = false
  const target = event.currentTarget as SVGSVGElement
  if (target.hasPointerCapture(event.pointerId)) target.releasePointerCapture(event.pointerId)
}

onMounted(() => {
  refreshSvgBounds()
  // Non-passive so wheel zoom/pan can preventDefault without browser scroll interference.
  chartSvg.value?.addEventListener('wheel', handleWheel, { passive: false })
  if (typeof ResizeObserver === 'undefined') return
  resizeObserver = new ResizeObserver(() => {
    refreshSvgBounds()
  })
  if (chartSvg.value) resizeObserver.observe(chartSvg.value)
  else if (chartShell.value) resizeObserver.observe(chartShell.value)
})

onBeforeUnmount(() => {
  chartSvg.value?.removeEventListener('wheel', handleWheel)
  cancelPendingPointerMove()
  if (wheelAnimationFrame != null) cancelAnimationFrame(wheelAnimationFrame)
  if (sliderAnimationFrame != null) cancelAnimationFrame(sliderAnimationFrame)
  resizeObserver?.disconnect()
  resizeObserver = null
})

function xAt(index: number): number {
  return plotLeft + step.value * (index + 0.5)
}

function xForTime(value: string): number {
  const times = visibleOpenTimes.value
  if (!times.length) return plotLeft
  const target = new Date(value).getTime()
  let low = 0
  let high = times.length - 1

  while (low <= high) {
    const middle = (low + high) >>> 1
    const candidate = times[middle]
    if (candidate === target) return xAt(middle)
    if (candidate < target) low = middle + 1
    else high = middle - 1
  }

  if (low <= 0) return xAt(0)
  if (low >= times.length) return xAt(times.length - 1)
  return xAt(target - times[low - 1] <= times[low] - target ? low - 1 : low)
}

function isTimeVisible(value: string): boolean {
  const times = visibleOpenTimes.value
  if (!times.length) return false
  const candidate = new Date(value).getTime()
  const lastClose = new Date(visibleFrames.value.at(-1)!.candle.closeTime).getTime()
  return candidate >= times[0] && candidate <= lastClose
}

/** True when [start, end] overlaps the visible candle window (including legs that fully span it). */
function intersectsVisibleTimeRange(start: string, end: string): boolean {
  const times = visibleOpenTimes.value
  if (!times.length) return false
  const rangeStart = Math.min(new Date(start).getTime(), new Date(end).getTime())
  const rangeEnd = Math.max(new Date(start).getTime(), new Date(end).getTime())
  const viewportStart = times[0]
  const viewportEnd = new Date(visibleFrames.value.at(-1)!.candle.closeTime).getTime()
  return rangeEnd >= viewportStart && rangeStart <= viewportEnd
}

function priceForTime(value: string): number {
  const frames = visibleFrames.value
  if (!frames.length) return 0
  const target = new Date(value).getTime()
  let best = frames[0]
  let bestDistance = Number.POSITIVE_INFINITY
  for (const frame of frames) {
    const open = new Date(frame.candle.openTime).getTime()
    const close = new Date(frame.candle.closeTime).getTime()
    if (target >= open && target <= close) return frame.candle.close
    const distance = Math.min(Math.abs(target - open), Math.abs(target - close))
    if (distance < bestDistance) {
      bestDistance = distance
      best = frame
    }
  }
  return best.candle.close
}

function yPrice(value: number): number {
  const domain = priceDomain.value
  return priceTop + ((domain.max - value) / (domain.max - domain.min)) * priceHeight
}

function yRsi(value: number): number {
  return rsiTop + ((100 - value) / 100) * rsiHeight
}

function yCci(value: number): number {
  const domain = cciDomain.value
  return cciTop + ((domain.max - value) / (domain.max - domain.min)) * cciHeight
}

function yAtr(value: number): number {
  const domain = atrDomain.value
  return atrTop + ((domain.max - value) / (domain.max - domain.min)) * atrHeight
}

function yEr(value: number): number {
  return erTop + ((erDomain.max - value) / (erDomain.max - erDomain.min)) * erHeight
}

function numericPath(
  selector: (frame: ReplayFrame) => number | null | undefined,
  yMapper: (value: number) => number,
): string {
  const commands: string[] = []
  for (let index = 0; index < visibleFrames.value.length; index += 1) {
    const value = selector(visibleFrames.value[index])
    if (value == null || !Number.isFinite(value)) continue
    commands.push(`${commands.length === 0 ? 'M' : 'L'}${pathNumber(xAt(index))} ${pathNumber(yMapper(value))}`)
  }
  return commands.join('')
}

function indicatorPath(
  property: 'bollingerUpper' | 'bollingerMiddle' | 'bollingerLower' | 'sma50' | 'sma200',
): string {
  return numericPath((frame) => frame.indicators[property] ?? null, yPrice)
}

function pathNumber(value: number): string {
  return value.toFixed(2)
}

function rectanglePath(x: number, y: number, rectangleWidth: number, rectangleHeight: number): string {
  return `M${pathNumber(x)} ${pathNumber(y)}h${pathNumber(rectangleWidth)}v${pathNumber(rectangleHeight)}h-${pathNumber(rectangleWidth)}Z`
}

function groupedRectanglePaths(
  classifier: (frame: ReplayFrame) => string,
  y: number,
  rectangleHeight: number,
) {
  const paths = new Map<string, string[]>()
  const barWidth = step.value
  visibleFrames.value.forEach((frame, index) => {
    const className = classifier(frame).toLowerCase()
    const commands = paths.get(className) ?? []
    commands.push(rectanglePath(xAt(index) - barWidth / 2, y, barWidth, rectangleHeight))
    paths.set(className, commands)
  })
  return [...paths].map(([className, commands]) => ({ className, path: commands.join('') }))
}

function buildSpans(predicate: (frame: ReplayFrame) => boolean) {
  const spans: { startIndex: number; endIndex: number; x: number; width: number }[] = []
  let start: number | null = null
  visibleFrames.value.forEach((frame, index) => {
    const matches = predicate(frame)
    if (matches && start == null) start = index
    const atEnd = index === visibleFrames.value.length - 1
    if (start != null && (!matches || atEnd)) {
      const end = matches && atEnd ? index : index - 1
      spans.push({
        startIndex: start,
        endIndex: end,
        x: Math.max(plotLeft, xAt(start) - step.value / 2),
        width: Math.max(step.value, (end - start + 1) * step.value),
      })
      start = null
    }
  })
  return spans
}

function relationshipClass(relationship: RsiRelationshipSnapshot): string[] {
  const bullish = relationship.type.includes('Bullish')
  return [
    bullish ? 'relationship-bullish' : 'relationship-bearish',
    relationship.type.startsWith('Hidden') ? 'relationship-hidden' : '',
    relationship.isConvergence ? 'relationship-convergence' : '',
  ].filter(Boolean)
}

function relationshipLabel(type: RsiRelationshipSnapshot['type']): string {
  const labels: Record<RsiRelationshipSnapshot['type'], string> = {
    None: '',
    RegularBullishDivergence: 'Regular bullish divergence',
    RegularBearishDivergence: 'Regular bearish divergence',
    HiddenBullishDivergence: 'Hidden bullish divergence',
    HiddenBearishDivergence: 'Hidden bearish divergence',
    BullishConvergence: 'Bullish convergence',
    BearishConvergence: 'Bearish convergence',
  }
  return labels[type]
}

function linePriceAtTime(line: Trendline, value: number): number {
  const elapsedSeconds = (value - new Date(line.originTime).getTime()) / 1_000
  return line.originPrice + line.slopePerSecond * elapsedSeconds
}

function trendStartTime(line: Trendline): number {
  return timestampValue(line.startTime, timestampValue(line.originTime, 0))
}

function trendEndTime(line: Trendline): number {
  const start = trendStartTime(line)
  return Math.max(start, timestampValue(line.endTime, start))
}

function channelStartTime(channel: PriceChannel): number {
  const inferred = Math.max(trendStartTime(channel.lowerLine), trendStartTime(channel.upperLine))
  return timestampValue(channel.startTime, inferred)
}

function channelEndTime(channel: PriceChannel): number {
  const start = channelStartTime(channel)
  const inferred = Math.min(trendEndTime(channel.lowerLine), trendEndTime(channel.upperLine))
  return Math.max(start, timestampValue(channel.endTime, inferred))
}

function timestampValue(value: string | undefined, fallback: number): number {
  if (!value) return fallback
  const parsed = new Date(value).getTime()
  return Number.isFinite(parsed) ? parsed : fallback
}

function xForProjectedTime(value: number): number {
  const frames = visibleFrames.value
  if (!frames.length) return plotLeft
  const start = new Date(frames[0].candle.openTime).getTime()
  const end = new Date(frames.at(-1)!.candle.closeTime).getTime()
  if (end <= start) return plotLeft
  const ratio = Math.max(0, Math.min(1, (value - start) / (end - start)))
  return plotLeft + ratio * plotWidth
}

function trendPoint(line: Trendline, at: number) {
  return { x: xForProjectedTime(at), y: yPrice(linePriceAtTime(line, at)) }
}

function trendSegment(line: Trendline, start: number, end: number) {
  const first = trendPoint(line, start)
  const last = trendPoint(line, end)
  return { x1: first.x, y1: first.y, x2: last.x, y2: last.y }
}

function channelPolygon(channel: PriceChannel, start: number, end: number): string {
  const x1 = xForProjectedTime(start)
  const x2 = xForProjectedTime(end)
  const lowerStart = yPrice(linePriceAtTime(channel.lowerLine, start))
  const lowerEnd = yPrice(linePriceAtTime(channel.lowerLine, end))
  const upperStart = yPrice(linePriceAtTime(channel.upperLine, start))
  const upperEnd = yPrice(linePriceAtTime(channel.upperLine, end))
  return `${x1},${upperStart} ${x2},${upperEnd} ${x2},${lowerEnd} ${x1},${lowerStart}`
}

function bodyY(openY: number, closeY: number): number {
  return Math.min(openY, closeY)
}

function bodyHeight(openY: number, closeY: number): number {
  return Math.max(1.5, Math.abs(closeY - openY))
}

// Marker size tracks candle spacing so triangles stay small relative to bars.
const markerHalfWidth = computed(() => {
  const spacing = step.value
  // Readable minimum, never dominate the candle slot.
  return Math.max(2.5, Math.min(4.5, spacing * 0.38))
})

function priceActionPoints(bullish: boolean, x: number, y: number): string {
  const half = markerHalfWidth.value
  const height = half * 1.7
  const base = half * 0.35
  return bullish
    ? `${x},${y - height} ${x - half},${y + base} ${x + half},${y + base}`
    : `${x},${y + height} ${x - half},${y - base} ${x + half},${y - base}`
}

function swingPoints(swing: SwingPoint, x: number, y: number): string {
  const half = markerHalfWidth.value
  const height = half * 1.55
  const tip = Math.max(1, half * 0.3)
  return swing.type === 'High'
    ? `${x - half},${y - height} ${x + half},${y - height} ${x},${y - tip}`
    : `${x - half},${y + height} ${x + half},${y + height} ${x},${y + tip}`
}
</script>

<template>
  <div ref="chartShell" class="chart-shell">
    <div class="chart-navigation" aria-label="Chart navigation">
      <div class="chart-navigation-buttons">
        <button type="button" title="Older candles" :disabled="maximumPanOffset === 0 || panOffset >= maximumPanOffset" @click="panPage('older')">‹ Older</button>
        <button type="button" title="Newer candles" :disabled="panOffset === 0" @click="panPage('newer')">Newer ›</button>
        <button type="button" title="Return to the newest available candle" :disabled="isFollowingLatest" @click="goToLatest">Latest</button>
        <span class="chart-navigation-divider"></span>
        <button type="button" title="Show fewer candles" @click="zoom(0.8)">＋</button>
        <button type="button" title="Show more candles" @click="zoom(1.25)">−</button>
        <button type="button" title="Fit every available candle" @click="fitAll">Fit</button>
      </div>
      <div class="chart-viewport-status">
        <strong>{{ viewportLabel }}</strong>
        <span>{{ isFollowingLatest ? 'Following latest' : 'Historical viewport' }}</span>
        <small>Drag to pan · mouse wheel zooms · shift+wheel pans</small>
      </div>
    </div>

    <div
      v-if="focusFrame && focusCandle && focusIndicators"
      class="chart-readout"
      :class="{ hovering: isHovering, bullish: focusChange.bullish, bearish: !focusChange.bullish }"
      aria-live="polite"
    >
      <div class="chart-readout-primary">
        <span class="chart-readout-mode">{{ isHovering ? 'Hover' : 'Latest' }}</span>
        <time class="chart-readout-time">{{ focusIndicators.time }} UTC</time>
        <dl class="chart-ohlc">
          <div><dt>O</dt><dd>{{ price(focusCandle.open) }}</dd></div>
          <div><dt>H</dt><dd>{{ price(focusCandle.high) }}</dd></div>
          <div><dt>L</dt><dd>{{ price(focusCandle.low) }}</dd></div>
          <div><dt>C</dt><dd :class="focusChange.bullish ? 'positive-text' : 'negative-text'">{{ price(focusCandle.close) }}</dd></div>
        </dl>
        <span class="chart-readout-change" :class="focusChange.bullish ? 'positive-text' : 'negative-text'">
          {{ focusChange.label }}
        </span>
        <span class="chart-readout-volume">Vol {{ compact(focusCandle.volume) }}</span>
        <span
          v-if="viewportRange"
          class="chart-readout-range"
          :class="viewportRange.bullish ? 'positive-text' : 'negative-text'"
          :title="`Viewport open ${price(viewportRange.open)} → close ${price(viewportRange.close)}`"
        >
          View {{ viewportRange.moveLabel }}
        </span>
      </div>
      <div class="chart-readout-metrics">
        <span><em>RSI</em>{{ focusIndicators.rsi }}</span>
        <span><em>CCI</em>{{ focusIndicators.cci }}</span>
        <span><em>CCI state</em>{{ focusIndicators.cciState }}</span>
        <span><em>CCI cross</em>{{ focusIndicators.cciCrossing }}</span>
        <span><em>CCI relation</em>{{ focusIndicators.cciRelationship }}</span>
        <span><em>SMA50</em>{{ focusIndicators.sma50 }}</span>
        <span><em>SMA200</em>{{ focusIndicators.sma200 }}</span>
        <span><em>ATR%</em>{{ focusIndicators.atr }}</span>
        <span><em>BB</em>{{ focusIndicators.bb }}</span>
        <span><em>ADX</em>{{ focusIndicators.adx }}</span>
        <span><em>ER</em>{{ focusIndicators.er }}</span>
        <span><em>PA</em>{{ focusIndicators.pa }}</span>
        <span><em>Struct</em>{{ focusIndicators.structure }}</span>
        <span><em>Regime</em>{{ focusIndicators.regime }} {{ focusIndicators.regimeConfidence }}%</span>
      </div>
    </div>

    <label class="chart-pan-control">
      <span>Older</span>
      <input
        v-model.number="viewportPosition"
        type="range"
        min="0"
        :max="maximumPanOffset"
        step="1"
        :disabled="maximumPanOffset === 0"
        aria-label="Horizontal chart position"
      />
      <span>Latest</span>
      <small>Horizontal position · zoom stays unchanged</small>
    </label>

    <svg
      ref="chartSvg"
      :class="['analysis-chart', { 'is-dragging': dragging, 'is-hovering': isHovering }]"
      :viewBox="`0 0 ${width} ${height}`"
      preserveAspectRatio="xMidYMid meet"
      role="img"
      aria-label="Interactive candlestick chart with Bollinger and Donchian regimes, RSI relationships, ATR and Efficiency Ratio context, price action, market structure and market regime annotations"
      @pointerdown="handlePointerDown"
      @pointerenter="handlePointerEnter"
      @pointermove="handlePointerMove"
      @pointerup="handlePointerUp"
      @pointercancel="handlePointerUp"
      @pointerleave="handlePointerLeave"
    >
      <defs>
        <linearGradient id="band-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#8ba4ff" stop-opacity="0.18" />
          <stop offset="100%" stop-color="#8ba4ff" stop-opacity="0.025" />
        </linearGradient>
        <linearGradient id="channel-fill" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stop-color="#47d7ac" stop-opacity="0.035" />
          <stop offset="100%" stop-color="#47d7ac" stop-opacity="0.13" />
        </linearGradient>
        <linearGradient id="donchian-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#efb85b" stop-opacity="0.14" />
          <stop offset="100%" stop-color="#efb85b" stop-opacity="0.02" />
        </linearGradient>
        <clipPath id="price-clip">
          <rect :x="plotLeft" :y="priceTop" :width="plotWidth" :height="priceHeight" />
        </clipPath>
        <clipPath id="rsi-clip">
          <rect :x="plotLeft" :y="rsiTop" :width="plotWidth" :height="rsiHeight" />
        </clipPath>
        <clipPath id="cci-clip">
          <rect :x="plotLeft" :y="cciTop" :width="plotWidth" :height="cciHeight" />
        </clipPath>
        <clipPath id="atr-clip">
          <rect :x="plotLeft" :y="atrTop" :width="plotWidth" :height="atrHeight" />
        </clipPath>
        <clipPath id="er-clip">
          <rect :x="plotLeft" :y="erTop" :width="plotWidth" :height="erHeight" />
        </clipPath>
        <clipPath id="regime-clip">
          <rect :x="plotLeft" :y="regimeTop" :width="plotWidth" :height="regimeHeight" />
        </clipPath>
      </defs>

      <rect class="chart-panel" :x="plotLeft" :y="priceTop" :width="plotWidth" :height="priceHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="volumeTop" :width="plotWidth" :height="volumeHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="rsiTop" :width="plotWidth" :height="rsiHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="cciTop" :width="plotWidth" :height="cciHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="atrTop" :width="plotWidth" :height="atrHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="erTop" :width="plotWidth" :height="erHeight" rx="4" />

      <g v-if="layers.marketRegime" clip-path="url(#regime-clip)" class="regime-band">
        <path
          v-for="item in regimeBandPaths"
          :key="`regime-${item.className}`"
          :class="`regime regime-${item.className}`"
          :d="item.path"
        />
      </g>

      <g class="grid-lines">
        <line
          v-for="tick in priceTicks"
          :key="tick.value"
          :x1="plotLeft"
          :x2="plotLeft + plotWidth"
          :y1="tick.y"
          :y2="tick.y"
        />
        <line
          v-for="tick in timeTicks"
          :key="tick.index"
          :x1="tick.x"
          :x2="tick.x"
          :y1="priceTop"
          :y2="erTop + erHeight"
        />
      </g>

      <g class="axis-labels">
        <text
          v-for="tick in priceTicks"
          :key="`price-${tick.value}`"
          :x="plotLeft + plotWidth + 10"
          :y="tick.y + 4"
        >{{ price(tick.value) }}</text>
        <text
          v-for="tick in atrTicks"
          :key="`atr-${tick.value}`"
          :x="plotLeft + plotWidth + 10"
          :y="tick.y + 4"
        >{{ tick.value.toFixed(3) }}%</text>
        <text
          v-for="tick in erTicks"
          :key="`er-${tick.value}`"
          :x="plotLeft + plotWidth + 10"
          :y="tick.y + 4"
        >{{ tick.value.toFixed(2) }}</text>
        <text
          v-for="tick in timeTicks"
          :key="`time-${tick.index}`"
          :x="tick.x"
          :y="height - 12"
          text-anchor="middle"
        >{{ tick.label }}</text>
        <text :x="plotLeft + 10" :y="rsiTop + 15" class="panel-label">RSI · 14</text>
        <text v-if="panelBadges?.rsi" :x="plotLeft + plotWidth - 8" :y="rsiTop + 15" text-anchor="end" class="panel-value">{{ panelBadges.rsi }}</text>
        <text :x="plotLeft + 10" :y="cciTop + 15" class="panel-label">CCI · 20</text>
        <text v-if="panelBadges?.cci" :x="plotLeft + plotWidth - 8" :y="cciTop + 15" text-anchor="end" class="panel-value">{{ panelBadges.cci }}</text>
        <text :x="plotLeft + 10" :y="volumeTop + 15" class="panel-label">TICK VOLUME</text>
        <text v-if="panelBadges?.volume" :x="plotLeft + plotWidth - 8" :y="volumeTop + 15" text-anchor="end" class="panel-value">{{ panelBadges.volume }}</text>
        <text :x="plotLeft + 10" :y="atrTop + 15" class="panel-label">ATR · NORMALIZED %</text>
        <text v-if="panelBadges?.atr" :x="plotLeft + plotWidth - 8" :y="atrTop + 15" text-anchor="end" class="panel-value">{{ panelBadges.atr }}%</text>
        <text :x="plotLeft + 10" :y="erTop + 15" class="panel-label">EFFICIENCY RATIO</text>
        <text v-if="panelBadges?.er" :x="plotLeft + plotWidth - 8" :y="erTop + 15" text-anchor="end" class="panel-value">{{ panelBadges.er }}</text>
      </g>

      <g clip-path="url(#price-clip)">
        <g v-if="layers.bollingerRegimes" class="bollinger-regimes">
          <rect
            v-for="span in narrowSpans"
            :key="`narrow-${span.startIndex}-${span.endIndex}`"
            class="bollinger-narrow-span"
            :x="span.x"
            :y="priceTop"
            :width="span.width"
            :height="priceHeight"
          />
          <rect
            v-for="span in squeezeSpans"
            :key="`squeeze-${span.startIndex}-${span.endIndex}`"
            class="bollinger-squeeze-span"
            :x="span.x"
            :y="priceTop"
            :width="span.width"
            :height="priceHeight"
          />
          <rect
            v-for="span in wideSpans"
            :key="`wide-${span.startIndex}-${span.endIndex}`"
            class="bollinger-wide-span"
            :x="span.x"
            :y="priceTop"
            :width="span.width"
            :height="priceHeight"
          />
          <rect
            v-for="span in expansionSpans"
            :key="`expansion-${span.startIndex}-${span.endIndex}`"
            class="bollinger-expansion-span"
            :x="span.x"
            :y="priceTop"
            :width="span.width"
            :height="priceHeight"
          />
          <path
            v-for="item in bollingerDirectionPaths"
            :key="`bb-direction-${item.className}`"
            :class="`bollinger-direction bollinger-direction-${item.className}`"
            :d="item.path"
          />
          <g
            v-for="marker in squeezeReleaseMarkers"
            :key="`release-${marker.frame.index}`"
            class="squeeze-release-marker"
          >
            <line :x1="marker.x" :x2="marker.x" :y1="priceTop" :y2="priceTop + priceHeight" />
            <path :d="`M ${marker.x - 5} ${priceTop + 7} L ${marker.x + 5} ${priceTop + 7} L ${marker.x} ${priceTop + 16} Z`" />
            <title>Squeeze released at {{ timestamp(marker.frame.availableAt) }}</title>
          </g>
        </g>

        <g v-if="layers.zones" class="price-zones">
          <g
            v-for="(zone, index) in zoneVisuals"
            :key="`${zone.centrePrice}-${index}`"
            :class="`zone-${zone.type.toLowerCase()}`"
          >
            <rect
              :x="plotLeft"
              :y="zone.y"
              :width="plotWidth"
              :height="zone.height"
            />
            <line
              :x1="plotLeft"
              :x2="plotLeft + plotWidth"
              :y1="zone.centreY"
              :y2="zone.centreY"
            />
            <text
              :x="plotLeft + plotWidth - 7"
              :y="zone.centreY - 4"
              text-anchor="end"
            >{{ zone.type }} · {{ price(zone.centrePrice) }}</text>
            <title>{{ zone.type }} zone · {{ zone.touchCount }} touches · {{ zone.strength.toFixed(0) }} strength</title>
          </g>
        </g>

        <g v-if="layers.supplyDemand" class="supply-demand-zones" clip-path="url(#price-clip)">
          <g
            v-for="zone in supplyDemandZoneVisuals"
            :key="zone.zoneId"
            :class="[`supply-demand-${zone.type.toLowerCase()}`, `lifecycle-${zone.state.toLowerCase()}`]"
          >
            <rect :x="zone.x" :y="zone.y" :width="zone.width" :height="zone.height" />
            <text :x="zone.x + 4" :y="zone.y + 11">{{ zone.type }} · {{ zone.state }}</text>
            <title>ID {{ zone.zoneId }} · {{ zone.pattern }} · {{ zone.state }} · {{ price(zone.distalPrice) }}–{{ price(zone.proximalPrice) }} · available {{ timestamp(zone.availableAt) }} · confirmed {{ timestamp(zone.confirmedAt) }} · quality {{ zone.qualityScore.toFixed(2) }} · freshness {{ zone.freshnessScore.toFixed(2) }} · touches {{ zone.touchCount }} · penetration {{ zone.penetrationRatio.toFixed(2) }} · profile {{ zone.profileHash }} · confluence {{ zone.confluenceCount }}</title>
          </g>
        </g>

        <g v-if="layers.liquidity" class="liquidity-pools" clip-path="url(#price-clip)">
          <g
            v-for="pool in liquidityPoolVisuals"
            :key="pool.poolId"
            :class="[`liquidity-${pool.side.toLowerCase()}`, `liquidity-state-${pool.state.toLowerCase()}`]"
          >
            <rect :x="pool.x" :y="pool.y" :width="pool.width" :height="pool.height" class="liquidity-band" />
            <line :x1="pool.x" :x2="pool.x + pool.width" :y1="pool.centreY" :y2="pool.centreY" />
            <text :x="pool.x + 4" :y="pool.y + 11">{{ pool.type }} · {{ pool.state }}</text>
            <title>ID {{ pool.poolId }} · {{ pool.side }} {{ pool.type }} · {{ pool.state }} · {{ price(pool.lowerPrice) }}–{{ price(pool.upperPrice) }} · available {{ timestamp(pool.availableAt) }} · confirmed {{ timestamp(pool.confirmedAt) }} · quality {{ pool.qualityScore.toFixed(2) }} · equalness {{ pool.equalnessScore.toFixed(2) }} · visibility {{ pool.visibilityScore.toFixed(2) }} · compression {{ pool.compressionScore.toFixed(2) }} · prominence {{ pool.prominenceScore.toFixed(2) }} · freshness {{ pool.freshnessScore.toFixed(2) }} · touches {{ pool.touchCount }} · profile {{ pool.profileHash }}</title>
          </g>
          <g
            v-for="event in liquidityEventVisuals"
            :key="`${event.poolId}-${event.availableAt}-${event.eventType}`"
            :class="`liquidity-event liquidity-event-${event.eventType.toLowerCase()}`"
          >
            <path :d="`M ${event.x - 5} ${event.y - 8} L ${event.x + 5} ${event.y - 8} L ${event.x} ${event.y} Z`" />
            <title>{{ event.eventType }} · pool {{ event.poolId }} · {{ price(event.price) }} · {{ timestamp(event.availableAt) }}</title>
          </g>
        </g>

        <g v-if="layers.channels" class="channel-areas">
          <g v-for="item in channelVisuals" :key="item.key">
            <polygon
              v-if="item.observedPoints"
              :points="item.observedPoints"
              class="channel-area channel-observed"
            />
            <polygon
              v-if="item.projectedPoints"
              :points="item.projectedPoints"
              class="channel-area channel-projected"
            />
            <title>{{ item.channel.direction }} channel · {{ item.channel.confidence.toFixed(0) }} confidence</title>
          </g>
        </g>

        <polygon
          v-if="layers.bollinger && bollingerBand"
          :points="bollingerBand"
          fill="url(#band-fill)"
        />
        <path v-if="layers.bollinger" :d="bollingerUpper" class="indicator-line bollinger-edge" />
        <path v-if="layers.bollinger" :d="bollingerMiddle" class="indicator-line bollinger-middle" />
        <path v-if="layers.bollinger" :d="bollingerLower" class="indicator-line bollinger-edge" />

        <path v-if="layers.movingAverages" :d="sma50Path" class="indicator-line sma-fast" />
        <path v-if="layers.movingAverages" :d="sma200Path" class="indicator-line sma-slow" />

        <polygon
          v-if="layers.donchian && donchianBand"
          :points="donchianBand"
          fill="url(#donchian-fill)"
        />
        <path v-if="layers.donchian" :d="donchianUpper" class="indicator-line donchian-edge" />
        <path v-if="layers.donchian" :d="donchianLower" class="indicator-line donchian-edge" />

        <g v-if="layers.trendlines" class="trend-lines">
          <g v-for="item in trendVisuals" :key="item.key">
            <line
              v-if="item.observed"
              :class="[item.line.type === 'Support' ? 'trend-support' : 'trend-resistance', 'trend-observed']"
              :x1="item.observed.x1"
              :y1="item.observed.y1"
              :x2="item.observed.x2"
              :y2="item.observed.y2"
            />
            <line
              v-if="item.projected"
              :class="[item.line.type === 'Support' ? 'trend-support' : 'trend-resistance', 'trend-projected']"
              :x1="item.projected.x1"
              :y1="item.projected.y1"
              :x2="item.projected.x2"
              :y2="item.projected.y2"
            />
            <circle
              v-if="item.startPoint"
              :class="item.line.type === 'Support' ? 'trend-support' : 'trend-resistance'"
              :cx="item.startPoint.x"
              :cy="item.startPoint.y"
              r="3"
            />
            <circle
              v-if="item.endPoint"
              :class="item.line.type === 'Support' ? 'trend-support' : 'trend-resistance'"
              :cx="item.endPoint.x"
              :cy="item.endPoint.y"
              r="3"
            />
            <title>{{ item.line.type }} trendline · {{ item.line.inlierCount }} pivots · {{ item.line.fitScore.toFixed(0) }} fit</title>
          </g>
        </g>

        <g class="candles">
          <path v-if="candlePaths.upWicks" :d="candlePaths.upWicks" class="candle-wicks candle-up" />
          <path v-if="candlePaths.downWicks" :d="candlePaths.downWicks" class="candle-wicks candle-down" />
          <path v-if="candlePaths.upBodies" :d="candlePaths.upBodies" class="candle-bodies candle-up" />
          <path v-if="candlePaths.downBodies" :d="candlePaths.downBodies" class="candle-bodies candle-down" />
        </g>

        <g v-if="layers.rsiRelationships" class="rsi-relationships price-relationships">
          <g v-for="item in rsiRelationshipVisuals" :key="`price-${item.relationship.confirmedAt}-${item.relationship.type}`" :class="item.className">
            <line :x1="item.x1" :y1="item.priceY1" :x2="item.x2" :y2="item.priceY2" />
            <circle :cx="item.x1" :cy="item.priceY1" r="3.5" />
            <circle :cx="item.x2" :cy="item.priceY2" r="4.5" />
            <text :x="item.x2 + 7" :y="item.priceY2 - 7">{{ item.label }}</text>
            <title>{{ item.label }} · strength {{ item.relationship.strength.toFixed(1) }}</title>
          </g>
        </g>

        <g v-if="layers.neoWave" class="neo-wave-structure">
          <g
            v-for="item in neoWaveVisuals"
            :key="item.wave.waveId"
            :class="['neo-wave-leg', item.wave.direction.toLowerCase(), { preferred: item.preferred }]"
          >
            <line :x1="item.x1" :y1="item.y1" :x2="item.x2" :y2="item.y2" />
            <text :x="(item.x1 + item.x2) / 2" :y="(item.y1 + item.y2) / 2 - 6">{{ item.index }}</text>
            <title>
              Confirmed monowave {{ item.index }} · {{ item.wave.direction }} ·
              {{ price(item.wave.startPrice) }} → {{ price(item.wave.endPrice) }} ·
              confirmed {{ timestamp(item.wave.endConfirmedAt) }}
              {{ item.preferred ? ' · preferred hypothesis leg' : '' }}
            </title>
          </g>
          <g v-if="provisionalNeoWaveVisual" class="neo-wave-leg provisional">
            <line
              :x1="provisionalNeoWaveVisual.x1"
              :y1="provisionalNeoWaveVisual.y1"
              :x2="provisionalNeoWaveVisual.x2"
              :y2="provisionalNeoWaveVisual.y2"
            />
            <title>Provisional wave — not confirmed and never used as confirmed history.</title>
          </g>
        </g>

        <g v-if="layers.swings" class="swing-points">
          <polygon
            v-for="(item, index) in swingVisuals"
            :key="`${item.swing.pivotTime}-${item.swing.type}-${index}`"
            :points="swingPoints(item.swing, item.x, item.y)"
            :class="[
              item.swing.type === 'High' ? 'swing-high' : 'swing-low',
              { 'swing-fresh': item.fresh },
            ]"
          >
            <title>{{ item.swing.type }} pivot at {{ price(item.swing.price) }} · confirmed {{ timestamp(item.swing.confirmedAt) }}</title>
          </polygon>
        </g>

        <g v-if="layers.priceAction !== false" class="price-action-events">
          <polygon
            v-for="item in priceActionVisuals"
            :key="item.event.eventId"
            :points="item.points"
            :class="item.bullish ? 'price-action-bullish' : 'price-action-bearish'"
          >
            <title>{{ item.event.type }} · confidence {{ item.event.confidence.toFixed(1) }} · {{ item.event.explanation }}</title>
          </polygon>
        </g>

        <g v-if="viewportRange" class="viewport-extremes" pointer-events="none">
          <line
            class="viewport-extreme-line high"
            :x1="xAt(viewportRange.highIndex)"
            :x2="plotLeft + plotWidth"
            :y1="yPrice(viewportRange.high)"
            :y2="yPrice(viewportRange.high)"
          />
          <text
            class="viewport-extreme-label high"
            :x="plotLeft + plotWidth - 6"
            :y="yPrice(viewportRange.high) - 5"
            text-anchor="end"
          >H {{ price(viewportRange.high) }}</text>
          <line
            class="viewport-extreme-line low"
            :x1="xAt(viewportRange.lowIndex)"
            :x2="plotLeft + plotWidth"
            :y1="yPrice(viewportRange.low)"
            :y2="yPrice(viewportRange.low)"
          />
          <text
            class="viewport-extreme-label low"
            :x="plotLeft + plotWidth - 6"
            :y="yPrice(viewportRange.low) + 12"
            text-anchor="end"
          >L {{ price(viewportRange.low) }}</text>
        </g>

        <line
          v-if="analysisFrame && lastPriceY != null"
          class="last-price-line"
          :x1="plotLeft"
          :x2="plotLeft + plotWidth"
          :y1="lastPriceY"
          :y2="lastPriceY"
        />
      </g>

      <g
        v-if="analysisFrame && lastPrice != null && lastPriceY != null"
        class="last-price-tag"
        pointer-events="none"
      >
        <rect
          :x="plotLeft + plotWidth + 2"
          :y="lastPriceY - 10"
          width="84"
          height="20"
          rx="3"
        />
        <text
          :x="plotLeft + plotWidth + 44"
          :y="lastPriceY + 4"
          text-anchor="middle"
        >{{ price(lastPrice) }}</text>
      </g>

      <g v-if="layers.volume" class="volume-bars">
        <path v-if="volumePaths.up" :d="volumePaths.up" class="volume-up" />
        <path v-if="volumePaths.down" :d="volumePaths.down" class="volume-down" />
      </g>

      <g class="rsi-guides">
        <rect :x="plotLeft" :y="yRsi(70)" :width="plotWidth" :height="yRsi(30) - yRsi(70)" />
        <line v-for="level in [30, 50, 70]" :key="level" :x1="plotLeft" :x2="plotLeft + plotWidth" :y1="yRsi(level)" :y2="yRsi(level)" />
        <text v-for="level in [30, 50, 70]" :key="`rsi-${level}`" :x="plotLeft + plotWidth + 10" :y="yRsi(level) + 4">{{ level }}</text>
      </g>
      <g clip-path="url(#rsi-clip)">
        <path :d="rsiPath" class="rsi-line" />
        <g v-if="layers.rsiRelationships" class="rsi-relationships rsi-panel-relationships">
          <g v-for="item in rsiRelationshipVisuals" :key="`rsi-${item.relationship.confirmedAt}-${item.relationship.type}`" :class="item.className">
            <line :x1="item.x1" :y1="item.rsiY1" :x2="item.x2" :y2="item.rsiY2" />
            <circle :cx="item.x1" :cy="item.rsiY1" r="3" />
            <circle :cx="item.x2" :cy="item.rsiY2" r="4" />
          </g>
        </g>
      </g>

      <g v-if="layers.cci" class="cci-guides">
        <rect :x="plotLeft" :y="yCci(100)" :width="plotWidth" :height="yCci(-100) - yCci(100)" />
        <line v-for="level in [-100, 0, 100]" :key="`cci-guide-${level}`" :x1="plotLeft" :x2="plotLeft + plotWidth" :y1="yCci(level)" :y2="yCci(level)" />
        <text v-for="level in [-100, 0, 100]" :key="`cci-${level}`" :x="plotLeft + plotWidth + 10" :y="yCci(level) + 4">{{ level }}</text>
      </g>
      <g v-if="layers.cci" clip-path="url(#cci-clip)" class="cci-panel">
        <path :d="cciPath" class="cci-line" />
      </g>

      <g v-if="layers.atr" clip-path="url(#atr-clip)" class="atr-panel">
        <path
          v-for="item in atrRegimePaths"
          :key="`atr-regime-${item.className}`"
          :class="`atr-regime atr-regime-${item.className}`"
          :d="item.path"
        />
        <path :d="atrPath" class="atr-line" />
      </g>

      <g v-if="layers.efficiencyRatio" clip-path="url(#er-clip)" class="er-panel">
        <path
          v-for="item in erStatePaths"
          :key="`er-state-${item.className}`"
          :class="`efficiency efficiency-${item.className}`"
          :d="item.path"
        />
        <path :d="erPath" class="er-line" />
      </g>

      <g v-if="isHovering && focusFrame" class="chart-hover" pointer-events="none">
        <line :x1="hoverX" :x2="hoverX" :y1="priceTop" :y2="erTop + erHeight" />
        <circle :cx="hoverX" :cy="yPrice(focusFrame.candle.close)" r="4" />
        <circle :cx="hoverX" :cy="yPrice(focusFrame.candle.high)" r="2.5" class="hover-extreme" />
        <circle :cx="hoverX" :cy="yPrice(focusFrame.candle.low)" r="2.5" class="hover-extreme" />
      </g>

      <g class="trade-overlays" clip-path="url(#price-clip)">
        <g v-for="item in tradeVisuals" :key="item.trade.setupId">
          <line
            v-if="item.entryX != null && item.exitX != null && item.entryY != null && item.exitY != null"
            class="trade-path"
            :class="item.profitable ? 'trade-profit' : 'trade-loss'"
            :x1="item.entryX" :y1="item.entryY" :x2="item.exitX" :y2="item.exitY"
          />
          <path v-if="item.stop.path" class="trade-stop-line trade-stop-step" :d="item.stop.path" />
          <circle
            v-for="marker in item.stop.accepted"
            :key="`accepted-${marker.amendment.requestedSequence}`"
            class="trade-stop-accepted"
            :cx="marker.x" :cy="marker.y" r="3.5"
          >
            <title>{{ marker.amendment.acceptedAt ?? marker.amendment.requestedAt }} · {{ marker.amendment.previousStopPrice }} → {{ marker.amendment.acceptedStopPrice ?? marker.amendment.proposedStopPrice }} · open {{ marker.amendment.openProfitR }}R · locked {{ marker.amendment.lockedProfitR }}R · {{ marker.amendment.reason }} · {{ marker.amendment.structureSource ?? 'no structure source' }} · ATR {{ marker.amendment.atr ?? '—' }} · {{ marker.amendment.status }}</title>
          </circle>
          <path
            v-for="marker in item.stop.rejected"
            :key="`rejected-${marker.amendment.requestedSequence}`"
            class="trade-stop-rejected"
            :d="`M${marker.x - 4} ${marker.y - 4} L${marker.x + 4} ${marker.y + 4} M${marker.x + 4} ${marker.y - 4} L${marker.x - 4} ${marker.y + 4}`"
          >
            <title>{{ marker.amendment.requestedAt }} · {{ marker.amendment.previousStopPrice }} → {{ marker.amendment.proposedStopPrice }} · open {{ marker.amendment.openProfitR }}R · locked {{ marker.amendment.lockedProfitR }}R · {{ marker.amendment.reason }} · {{ marker.amendment.structureSource ?? 'no structure source' }} · ATR {{ marker.amendment.atr ?? '—' }} · {{ marker.amendment.status }} · {{ marker.amendment.rejectionReason ?? marker.amendment.explanation }}</title>
          </path>
          <line
            v-if="item.entryX != null && item.exitX != null && item.targetY != null"
            class="trade-target-line" :x1="item.entryX" :x2="item.exitX" :y1="item.targetY" :y2="item.targetY"
          />
          <path
            v-if="item.setupVisible"
            class="trade-setup"
            :d="`M${item.setupX} ${item.setupY - 6} L${item.setupX + 6} ${item.setupY} L${item.setupX} ${item.setupY + 6} L${item.setupX - 6} ${item.setupY} Z`"
          >
            <title>{{ item.trade.strategyName }} higher-timeframe setup started</title>
          </path>
          <rect
            v-if="item.confirmationVisible && item.confirmationX != null && item.confirmationY != null"
            class="trade-confirmation"
            :x="item.confirmationX - 4" :y="item.confirmationY - 4" width="8" height="8" rx="1"
          >
            <title>Confirmation timeframe accepted the setup</title>
          </rect>
          <circle v-if="item.signalVisible" class="trade-signal" :cx="item.signalX" :cy="item.signalY" r="4">
            <title>{{ item.trade.strategyName }} entry signal: {{ item.trade.setupReason }}</title>
          </circle>
          <path
            v-if="item.entryVisible && item.entryX != null && item.entryY != null"
            :class="['trade-entry', item.trade.side.toLowerCase()]"
            :d="item.trade.side === 'Buy'
              ? `M${item.entryX} ${item.entryY - 9} L${item.entryX - 7} ${item.entryY + 5} L${item.entryX + 7} ${item.entryY + 5} Z`
              : `M${item.entryX} ${item.entryY + 9} L${item.entryX - 7} ${item.entryY - 5} L${item.entryX + 7} ${item.entryY - 5} Z`"
          >
            <title>{{ item.trade.strategyName }} {{ item.trade.side }} at {{ item.trade.entryPrice }}</title>
          </path>
          <rect
            v-for="partial in item.partialExits"
            :key="partial.exit.exitId"
            class="trade-partial-exit"
            :x="partial.x - 5" :y="partial.y - 5" width="10" height="10" rx="2"
          >
            <title>{{ partial.exit.reason }} · closed {{ partial.exit.quantityClosed }} · remaining {{ partial.exit.quantityRemaining }} · P/L {{ partial.exit.netProfitLoss }} · realised {{ partial.exit.realizedR }}R</title>
          </rect>
          <circle
            v-if="item.exitVisible && item.exitX != null && item.exitY != null"
            :class="['trade-exit', item.profitable ? 'trade-profit' : 'trade-loss']"
            :cx="item.exitX" :cy="item.exitY" r="6"
          >
            <title>{{ item.trade.exitReason }} · P/L {{ item.trade.netProfitLoss }} · R {{ item.trade.rMultiple }}</title>
          </circle>
        </g>
      </g>
    </svg>
  </div>
</template>
