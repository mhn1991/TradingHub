<script setup lang="ts">
import { computed, ref } from 'vue'
import { compact, price, shortTime, timestamp } from '../format'
import type {
  ChartLayers,
  PriceChannel,
  ReplayFrame,
  SwingPoint,
  Trendline,
} from '../types'

const props = defineProps<{
  frames: ReplayFrame[]
  selectedIndex: number
  windowSize: number
  layers: ChartLayers
}>()

const width = 1240
const height = 690
const plotLeft = 24
const plotRight = 92
const plotWidth = width - plotLeft - plotRight
const priceTop = 34
const priceHeight = 382
const volumeTop = 430
const volumeHeight = 64
const rsiTop = 532
const rsiHeight = 118
const hoveredIndex = ref<number | null>(null)

const startIndex = computed(() =>
  Math.max(0, props.selectedIndex - props.windowSize + 1),
)
const visibleFrames = computed(() =>
  props.frames.slice(startIndex.value, props.selectedIndex + 1),
)
const currentFrame = computed(() => props.frames[props.selectedIndex])
const step = computed(() => plotWidth / Math.max(visibleFrames.value.length, 1))
const candleWidth = computed(() => Math.max(2, Math.min(11, step.value * 0.62)))

const priceDomain = computed(() => {
  const values: number[] = []
  for (const frame of visibleFrames.value) {
    values.push(frame.candle.high, frame.candle.low)
    if (props.layers.bollinger) {
      if (frame.indicators.bollingerUpper != null) values.push(frame.indicators.bollingerUpper)
      if (frame.indicators.bollingerLower != null) values.push(frame.indicators.bollingerLower)
    }
  }

  const current = currentFrame.value
  if (current && props.layers.zones) {
    for (const zone of current.priceZones) values.push(zone.lowerPrice, zone.upperPrice)
  }

  const firstTime = visibleFrames.value[0]?.candle.openTime
  const lastTime = visibleFrames.value.at(-1)?.candle.closeTime
  if (current && firstTime && lastTime) {
    if (props.layers.trendlines) {
      for (const line of current.trendlines) {
        values.push(linePriceAt(line, firstTime), linePriceAt(line, lastTime))
      }
    }
    if (props.layers.channels) {
      for (const channel of current.channels.slice(0, 2)) {
        values.push(
          linePriceAt(channel.lowerLine, firstTime),
          linePriceAt(channel.lowerLine, lastTime),
          linePriceAt(channel.upperLine, firstTime),
          linePriceAt(channel.upperLine, lastTime),
        )
      }
    }
  }

  const finite = values.filter(Number.isFinite)
  const min = finite.length ? Math.min(...finite) : 0
  const max = finite.length ? Math.max(...finite) : 1
  const padding = Math.max((max - min) * 0.08, max * 0.0004)
  return { min: min - padding, max: max + padding }
})

const priceTicks = computed(() =>
  Array.from({ length: 6 }, (_, index) => {
    const ratio = index / 5
    const value = priceDomain.value.max -
      (priceDomain.value.max - priceDomain.value.min) * ratio
    return { value, y: priceTop + priceHeight * ratio }
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

const candleVisuals = computed(() => visibleFrames.value.map((frame, index) => ({
  frame,
  x: xAt(index),
  openY: yPrice(frame.candle.open),
  closeY: yPrice(frame.candle.close),
  highY: yPrice(frame.candle.high),
  lowY: yPrice(frame.candle.low),
  up: frame.candle.close >= frame.candle.open,
})))

const maximumVolume = computed(() =>
  Math.max(1, ...visibleFrames.value.map((frame) => frame.candle.volume)),
)

const bollingerBand = computed(() => {
  const ready = visibleFrames.value
    .map((frame, index) => ({ frame, index }))
    .filter(({ frame }) =>
      frame.indicators.bollingerUpper != null &&
      frame.indicators.bollingerLower != null,
    )
  if (ready.length < 2) return ''
  const upper = ready.map(({ frame, index }) =>
    `${xAt(index)},${yPrice(frame.indicators.bollingerUpper!)}`,
  )
  const lower = ready.slice().reverse().map(({ frame, index }) =>
    `${xAt(index)},${yPrice(frame.indicators.bollingerLower!)}`,
  )
  return [...upper, ...lower].join(' ')
})

const bollingerUpper = computed(() => indicatorPath('bollingerUpper'))
const bollingerMiddle = computed(() => indicatorPath('bollingerMiddle'))
const bollingerLower = computed(() => indicatorPath('bollingerLower'))
const rsiPath = computed(() => {
  const points = visibleFrames.value
    .map((frame, index) => ({ value: frame.indicators.rsi, index }))
    .filter((point): point is { value: number; index: number } => point.value != null)
  return points.map((point, index) =>
    `${index === 0 ? 'M' : 'L'} ${xAt(point.index)} ${yRsi(point.value)}`,
  ).join(' ')
})

const zoneVisuals = computed(() => (currentFrame.value?.priceZones ?? []).map((zone) => ({
  ...zone,
  y: yPrice(zone.upperPrice),
  height: Math.max(2, yPrice(zone.lowerPrice) - yPrice(zone.upperPrice)),
})))

const trendVisuals = computed(() => {
  const frames = visibleFrames.value
  if (!frames.length) return []
  const start = frames[0].candle.openTime
  const end = frames.at(-1)!.candle.closeTime
  return (currentFrame.value?.trendlines ?? []).map((line) => ({
    line,
    x1: xForTime(start),
    y1: yPrice(linePriceAt(line, start)),
    x2: xForTime(end),
    y2: yPrice(linePriceAt(line, end)),
  }))
})

const channelVisuals = computed(() => {
  const frames = visibleFrames.value
  if (!frames.length) return []
  const start = frames[0].candle.openTime
  const end = frames.at(-1)!.candle.closeTime
  return (currentFrame.value?.channels ?? []).slice(0, 2).map((channel) =>
    channelPolygon(channel, start, end),
  )
})

const swingVisuals = computed(() => (currentFrame.value?.swings ?? [])
  .filter((swing) => {
    const pivot = new Date(swing.pivotTime).getTime()
    const first = new Date(visibleFrames.value[0]?.candle.openTime ?? 0).getTime()
    const last = new Date(visibleFrames.value.at(-1)?.candle.closeTime ?? 0).getTime()
    return pivot >= first && pivot <= last
  })
  .map((swing) => ({
    swing,
    x: xForTime(swing.pivotTime),
    y: yPrice(swing.price),
    fresh: swing.confirmedAt === currentFrame.value?.availableAt,
  })))

const displayedHoverIndex = computed(() =>
  hoveredIndex.value ?? Math.max(0, visibleFrames.value.length - 1),
)
const hoveredFrame = computed(() => visibleFrames.value[displayedHoverIndex.value])
const hoverX = computed(() => xAt(displayedHoverIndex.value))
const tooltipX = computed(() => Math.min(plotLeft + plotWidth - 228, Math.max(plotLeft + 8, hoverX.value + 14)))

function xAt(index: number): number {
  return plotLeft + step.value * (index + 0.5)
}

function xForTime(value: string): number {
  const frames = visibleFrames.value
  if (!frames.length) return plotLeft
  const timestampValue = new Date(value).getTime()
  let nearest = 0
  let distance = Number.POSITIVE_INFINITY
  frames.forEach((frame, index) => {
    const candidate = Math.abs(new Date(frame.candle.openTime).getTime() - timestampValue)
    if (candidate < distance) {
      distance = candidate
      nearest = index
    }
  })
  return xAt(nearest)
}

function yPrice(value: number): number {
  const domain = priceDomain.value
  return priceTop + ((domain.max - value) / (domain.max - domain.min)) * priceHeight
}

function yRsi(value: number): number {
  return rsiTop + ((100 - value) / 100) * rsiHeight
}

function indicatorPath(
  property: 'bollingerUpper' | 'bollingerMiddle' | 'bollingerLower',
): string {
  return visibleFrames.value
    .map((frame, index) => ({ value: frame.indicators[property], index }))
    .filter((point): point is { value: number; index: number } => point.value != null)
    .map((point, index) =>
      `${index === 0 ? 'M' : 'L'} ${xAt(point.index)} ${yPrice(point.value)}`,
    ).join(' ')
}

function linePriceAt(line: Trendline, value: string): number {
  const elapsedSeconds = (new Date(value).getTime() - new Date(line.originTime).getTime()) / 1_000
  return line.originPrice + line.slopePerSecond * elapsedSeconds
}

function channelPolygon(channel: PriceChannel, start: string, end: string) {
  const x1 = xForTime(start)
  const x2 = xForTime(end)
  const lowerStart = yPrice(linePriceAt(channel.lowerLine, start))
  const lowerEnd = yPrice(linePriceAt(channel.lowerLine, end))
  const upperStart = yPrice(linePriceAt(channel.upperLine, start))
  const upperEnd = yPrice(linePriceAt(channel.upperLine, end))
  return {
    channel,
    points: `${x1},${upperStart} ${x2},${upperEnd} ${x2},${lowerEnd} ${x1},${lowerStart}`,
  }
}

function bodyY(openY: number, closeY: number): number {
  return Math.min(openY, closeY)
}

function bodyHeight(openY: number, closeY: number): number {
  return Math.max(1.5, Math.abs(closeY - openY))
}

function volumeY(frame: ReplayFrame): number {
  return volumeTop + volumeHeight * (1 - frame.candle.volume / maximumVolume.value)
}

function volumeBarHeight(frame: ReplayFrame): number {
  return Math.max(1, volumeTop + volumeHeight - volumeY(frame))
}

function swingPoints(swing: SwingPoint, x: number, y: number): string {
  return swing.type === 'High'
    ? `${x - 6},${y - 11} ${x + 6},${y - 11} ${x},${y - 2}`
    : `${x - 6},${y + 11} ${x + 6},${y + 11} ${x},${y + 2}`
}

function handlePointerMove(event: MouseEvent) {
  const bounds = (event.currentTarget as SVGSVGElement).getBoundingClientRect()
  const relativeX = ((event.clientX - bounds.left) / bounds.width) * width
  hoveredIndex.value = Math.max(
    0,
    Math.min(
      visibleFrames.value.length - 1,
      Math.floor((relativeX - plotLeft) / step.value),
    ),
  )
}
</script>

<template>
  <div class="chart-shell">
    <svg
      class="analysis-chart"
      :viewBox="`0 0 ${width} ${height}`"
      role="img"
      aria-label="Candlestick chart with Bollinger Bands, market structure annotations and RSI"
      @mousemove="handlePointerMove"
      @mouseleave="hoveredIndex = null"
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
      </defs>

      <rect class="chart-panel" :x="plotLeft" :y="priceTop" :width="plotWidth" :height="priceHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="volumeTop" :width="plotWidth" :height="volumeHeight" rx="4" />
      <rect class="chart-panel" :x="plotLeft" :y="rsiTop" :width="plotWidth" :height="rsiHeight" rx="4" />

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
          :y2="rsiTop + rsiHeight"
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
          v-for="tick in timeTicks"
          :key="`time-${tick.index}`"
          :x="tick.x"
          :y="height - 12"
          text-anchor="middle"
        >{{ tick.label }}</text>
        <text :x="plotLeft + 10" :y="rsiTop + 15" class="panel-label">RSI · 14</text>
        <text :x="plotLeft + 10" :y="volumeTop + 15" class="panel-label">TICK VOLUME</text>
      </g>

      <g clip-path="url(#price-clip)">
        <g v-if="layers.zones" class="price-zones">
          <rect
            v-for="(zone, index) in zoneVisuals"
            :key="`${zone.centrePrice}-${index}`"
            :class="`zone-${zone.type.toLowerCase()}`"
            :x="plotLeft"
            :y="zone.y"
            :width="plotWidth"
            :height="zone.height"
          />
        </g>

        <polygon
          v-for="(channel, index) in channelVisuals"
          v-show="layers.channels"
          :key="`channel-${index}`"
          :points="channel.points"
          class="channel-area"
        />

        <polygon
          v-if="layers.bollinger && bollingerBand"
          :points="bollingerBand"
          fill="url(#band-fill)"
        />
        <path v-if="layers.bollinger" :d="bollingerUpper" class="indicator-line bollinger-edge" />
        <path v-if="layers.bollinger" :d="bollingerMiddle" class="indicator-line bollinger-middle" />
        <path v-if="layers.bollinger" :d="bollingerLower" class="indicator-line bollinger-edge" />

        <g v-if="layers.trendlines" class="trend-lines">
          <line
            v-for="(item, index) in trendVisuals"
            :key="`${item.line.type}-${index}`"
            :class="item.line.type === 'Support' ? 'trend-support' : 'trend-resistance'"
            :x1="item.x1"
            :y1="item.y1"
            :x2="item.x2"
            :y2="item.y2"
          />
        </g>

        <g class="candles">
          <g v-for="(item, index) in candleVisuals" :key="item.frame.index">
            <line
              :class="item.up ? 'candle-up' : 'candle-down'"
              :x1="item.x"
              :x2="item.x"
              :y1="item.highY"
              :y2="item.lowY"
            />
            <rect
              :class="item.up ? 'candle-up' : 'candle-down'"
              :x="item.x - candleWidth / 2"
              :y="bodyY(item.openY, item.closeY)"
              :width="candleWidth"
              :height="bodyHeight(item.openY, item.closeY)"
              rx="1"
            />
            <title>{{ timestamp(item.frame.availableAt) }} · O {{ price(item.frame.candle.open) }} · H {{ price(item.frame.candle.high) }} · L {{ price(item.frame.candle.low) }} · C {{ price(item.frame.candle.close) }}</title>
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

        <line
          v-if="currentFrame"
          class="last-price-line"
          :x1="plotLeft"
          :x2="plotLeft + plotWidth"
          :y1="yPrice(currentFrame.candle.close)"
          :y2="yPrice(currentFrame.candle.close)"
        />
      </g>

      <g v-if="layers.volume" class="volume-bars">
        <rect
          v-for="(item, index) in candleVisuals"
          :key="`volume-${item.frame.index}`"
          :class="item.up ? 'volume-up' : 'volume-down'"
          :x="item.x - candleWidth / 2"
          :y="volumeY(item.frame)"
          :width="candleWidth"
          :height="volumeBarHeight(item.frame)"
          rx="1"
        />
      </g>

      <g class="rsi-guides">
        <rect :x="plotLeft" :y="yRsi(70)" :width="plotWidth" :height="yRsi(30) - yRsi(70)" />
        <line v-for="level in [30, 50, 70]" :key="level" :x1="plotLeft" :x2="plotLeft + plotWidth" :y1="yRsi(level)" :y2="yRsi(level)" />
        <text v-for="level in [30, 50, 70]" :key="`rsi-${level}`" :x="plotLeft + plotWidth + 10" :y="yRsi(level) + 4">{{ level }}</text>
      </g>
      <path :d="rsiPath" class="rsi-line" />

      <g v-if="hoveredFrame" class="chart-hover">
        <line :x1="hoverX" :x2="hoverX" :y1="priceTop" :y2="rsiTop + rsiHeight" />
        <circle :cx="hoverX" :cy="yPrice(hoveredFrame.candle.close)" r="4" />
        <g :transform="`translate(${tooltipX}, ${priceTop + 10})`" class="hover-card">
          <rect width="216" height="90" rx="7" />
          <text x="12" y="19" class="hover-time">{{ timestamp(hoveredFrame.availableAt) }} UTC</text>
          <text x="12" y="40">O <tspan>{{ price(hoveredFrame.candle.open) }}</tspan></text>
          <text x="112" y="40">H <tspan>{{ price(hoveredFrame.candle.high) }}</tspan></text>
          <text x="12" y="60">L <tspan>{{ price(hoveredFrame.candle.low) }}</tspan></text>
          <text x="112" y="60">C <tspan>{{ price(hoveredFrame.candle.close) }}</tspan></text>
          <text x="12" y="80">VOL <tspan>{{ compact(hoveredFrame.candle.volume) }}</tspan></text>
          <text x="112" y="80">RSI <tspan>{{ hoveredFrame.indicators.rsi?.toFixed(1) ?? 'warm-up' }}</tspan></text>
        </g>
      </g>
    </svg>
  </div>
</template>
