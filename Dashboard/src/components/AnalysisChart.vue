<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
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
const height = 820
const plotLeft = 24
const plotRight = 92
const plotWidth = width - plotLeft - plotRight
const priceTop = 34
const priceHeight = 390
const volumeTop = 438
const volumeHeight = 60
const rsiTop = 526
const rsiHeight = 112
const atrTop = 666
const atrHeight = 100
const minimumWindowSize = 20

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
const candleWidth = computed(() => Math.max(1.5, Math.min(11, step.value * 0.62)))
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
const rsiPath = computed(() => numericPath(
  (frame) => frame.indicators.rsi,
  yRsi,
))
const atrPath = computed(() => numericPath(
  (frame) => frame.indicators.atrAnalysis?.normalizedPercent ?? null,
  yAtr,
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

const displayedHoverIndex = computed(() =>
  hoveredIndex.value ?? Math.max(0, visibleFrames.value.length - 1),
)
const hoveredFrame = computed(() => visibleFrames.value[displayedHoverIndex.value])
const hoverX = computed(() => xAt(displayedHoverIndex.value))
const tooltipX = computed(() => Math.min(
  plotLeft + plotWidth - 228,
  Math.max(plotLeft + 8, hoverX.value + 14),
))

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

function refreshSvgBounds(target: SVGSVGElement) {
  const bounds = target.getBoundingClientRect()
  svgBoundsLeft = bounds.left
  svgBoundsWidth = Math.max(bounds.width, 1)
}

function handlePointerEnter(event: PointerEvent) {
  refreshSvgBounds(event.currentTarget as SVGSVGElement)
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

onBeforeUnmount(() => {
  cancelPendingPointerMove()
  if (wheelAnimationFrame != null) cancelAnimationFrame(wheelAnimationFrame)
  if (sliderAnimationFrame != null) cancelAnimationFrame(sliderAnimationFrame)
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

function yAtr(value: number): number {
  const domain = atrDomain.value
  return atrTop + ((domain.max - value) / (domain.max - domain.min)) * atrHeight
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
  property: 'bollingerUpper' | 'bollingerMiddle' | 'bollingerLower',
): string {
  return numericPath((frame) => frame.indicators[property], yPrice)
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

function priceActionPoints(bullish: boolean, x: number, y: number): string {
  return bullish
    ? `${x},${y - 12} ${x - 7},${y + 2} ${x + 7},${y + 2}`
    : `${x},${y + 12} ${x - 7},${y - 2} ${x + 7},${y - 2}`
}

function swingPoints(swing: SwingPoint, x: number, y: number): string {
  return swing.type === 'High'
    ? `${x - 6},${y - 11} ${x + 6},${y - 11} ${x},${y - 2}`
    : `${x - 6},${y + 11} ${x + 6},${y + 11} ${x},${y + 2}`
}
</script>

<template>
  <div class="chart-shell">
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
        <small>Drag horizontally to pan · use the mouse wheel to zoom</small>
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
      :class="['analysis-chart', { 'is-dragging': dragging }]"
      :viewBox="`0 0 ${width} ${height}`"
      role="img"
      aria-label="Interactive candlestick chart with Bollinger regimes, RSI relationships, ATR context, price action and market structure annotations"
      @pointerdown="handlePointerDown"
      @pointerenter="handlePointerEnter"
      @pointermove="handlePointerMove"
      @pointerup="handlePointerUp"
      @pointercancel="handlePointerUp"
      @pointerleave="handlePointerLeave"
      @wheel="handleWheel"
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
        <clipPath id="price-clip">
          <rect :x="plotLeft" :y="priceTop" :width="plotWidth" :height="priceHeight" />
        </clipPath>
        <clipPath id="rsi-clip">
          <rect :x="plotLeft" :y="rsiTop" :width="plotWidth" :height="rsiHeight" />
        </clipPath>
        <clipPath id="atr-clip">
          <rect :x="plotLeft" :y="atrTop" :width="plotWidth" :height="atrHeight" />
        </clipPath>
      </defs>

      <rect class="chart-panel" :x="plotLeft" :y="priceTop" :width="plotWidth" :height="priceHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="volumeTop" :width="plotWidth" :height="volumeHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="rsiTop" :width="plotWidth" :height="rsiHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="atrTop" :width="plotWidth" :height="atrHeight" rx="4" />

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
          :y2="atrTop + atrHeight"
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
          v-for="tick in timeTicks"
          :key="`time-${tick.index}`"
          :x="tick.x"
          :y="height - 12"
          text-anchor="middle"
        >{{ tick.label }}</text>
        <text :x="plotLeft + 10" :y="rsiTop + 15" class="panel-label">RSI · 14</text>
        <text :x="plotLeft + 10" :y="volumeTop + 15" class="panel-label">TICK VOLUME</text>
        <text :x="plotLeft + 10" :y="atrTop + 15" class="panel-label">ATR · NORMALIZED %</text>
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

        <line
          v-if="analysisFrame"
          class="last-price-line"
          :x1="plotLeft"
          :x2="plotLeft + plotWidth"
          :y1="yPrice(analysisFrame.candle.close)"
          :y2="yPrice(analysisFrame.candle.close)"
        />
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

      <g v-if="layers.atr" clip-path="url(#atr-clip)" class="atr-panel">
        <path
          v-for="item in atrRegimePaths"
          :key="`atr-regime-${item.className}`"
          :class="`atr-regime atr-regime-${item.className}`"
          :d="item.path"
        />
        <path :d="atrPath" class="atr-line" />
      </g>

      <g v-if="hoveredFrame" class="chart-hover">
        <line :x1="hoverX" :x2="hoverX" :y1="priceTop" :y2="atrTop + atrHeight" />
        <circle :cx="hoverX" :cy="yPrice(hoveredFrame.candle.close)" r="4" />
        <g :transform="`translate(${tooltipX}, ${priceTop + 10})`" class="hover-card">
          <rect width="216" height="130" rx="7" />
          <text x="12" y="19" class="hover-time">{{ timestamp(hoveredFrame.availableAt) }} UTC</text>
          <text x="12" y="40">O <tspan>{{ price(hoveredFrame.candle.open) }}</tspan></text>
          <text x="112" y="40">H <tspan>{{ price(hoveredFrame.candle.high) }}</tspan></text>
          <text x="12" y="60">L <tspan>{{ price(hoveredFrame.candle.low) }}</tspan></text>
          <text x="112" y="60">C <tspan>{{ price(hoveredFrame.candle.close) }}</tspan></text>
          <text x="12" y="80">VOL <tspan>{{ compact(hoveredFrame.candle.volume) }}</tspan></text>
          <text x="112" y="80">RSI <tspan>{{ hoveredFrame.indicators.rsi?.toFixed(1) ?? 'warm-up' }}</tspan></text>
          <text x="12" y="100">ATR% <tspan>{{ hoveredFrame.indicators.atrAnalysis?.normalizedPercent?.toFixed(3) ?? 'warm-up' }}</tspan></text>
          <text x="112" y="100">BB <tspan>{{ hoveredFrame.indicators.bollingerAnalysis?.widthRegime ?? 'warm-up' }}</tspan></text>
          <text x="12" y="120">ADX <tspan>{{ hoveredFrame.indicators.adxAnalysis?.adx?.toFixed(1) ?? 'warm-up' }}</tspan></text>
          <text x="112" y="120">PA <tspan>{{ hoveredFrame.priceAction?.bias ?? 'Neutral' }}</tspan></text>
        </g>
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
