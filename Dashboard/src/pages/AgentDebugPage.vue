<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, useTemplateRef } from 'vue'
import { buildCandleLayout, type DebugCandle } from '../utils/agentDebugChart'

interface InstrumentInfo {
  instrument: string
  cachedFrom: string
  cachedTo: string
}

interface DebugEvent {
  type: 'Candle' | 'Diagnostic' | 'Decision' | 'Status' | 'Error' | 'Complete'
  time: string
  interval?: string
  isBaseInterval?: boolean
  open?: number
  high?: number
  low?: number
  close?: number
  rsi?: number
  bollingerUpper?: number
  bollingerMiddle?: number
  bollingerLower?: number
  stochRsiFast?: number
  stochRsiSlow?: number
  atr?: number
  role?: 'trigger' | 'confirmation'
  certainty?: 'None' | 'Partial' | 'Full'
  direction?: 'Bullish' | 'Bearish'
  relationshipType?: string
  relationshipStrength?: number
  isNewRelationship?: boolean
  action?: string
  referencePrice?: number
  stopLossPrice?: number
  reason?: string
  message?: string
  totalTrades?: number
  funnel?: Funnel
}

/** How far every candle got through the agent's entry condition — emitted once, on the final
 * Status event, from the real agent's own counters. */
interface FunnelStage {
  interval: string
  role: 'trigger' | 'confirmation'
  candlesProcessed: number
  partialExtremes: number
  fullExtremes: number
  noRelationship: number
  staleRelationship: number
  alreadyConsumed: number
  directionMismatch: number
  signalsBuilt: number
}

interface Funnel {
  stages: FunnelStage[]
  evaluations: number
  entries: number
  closes: number
  flips: number
  suppressedAlreadyPositioned: number
  breakoutHeldAgainstPosition: number
}

// A real run can emit far more events per second than Vue/the DOM can render one-at-a-time
// (every closed candle on every timeframe, plus a diagnostic line per timeframe) - incoming SSE
// messages are buffered here and flushed to reactive state in batches on an animation frame,
// and both the log and the chart keep only a bounded rolling window, so a long run stays
// responsive instead of hanging the tab.
const MAX_LOG_LINES = 1500
const MAX_CHART_CANDLES = 300

const instruments = ref<InstrumentInfo[]>([])
const loadingInstruments = ref(false)
const running = ref(false)
const runError = ref<string | null>(null)
const showAdvanced = ref(false)

const form = reactive({
  instrument: '',
  monitoredTimeframes: '30m,15m',
  confirmationTimeframes: '5m,1m',
  warmupStart: '',
  runStart: '',
  runEnd: '',
  allowFetch: false,
  quantity: 1000,
  rsiOverbought: 75,
  rsiOversold: 25,
  stochRsiFastOverbought: 100,
  stochRsiFastOversold: 0,
  partialStochRsiFastOverbought: 85,
  partialStochRsiFastOversold: 15,
  protectiveStopAtrMultiple: 3,
  signalFreshnessCandles: 3,
})

const funnel = ref<Funnel | null>(null)

const logLines = ref<DebugEvent[]>([])
const candlesByInterval = reactive(new Map<string, DebugCandle[]>())
const markersByInterval = reactive(new Map<string, { time: string; event: DebugEvent }[]>())
const seenIntervals = ref<string[]>([])
const chartWidth = 900
const chartHeight = 150

let eventSource: EventSource | null = null
const logEl = useTemplateRef<HTMLDivElement>('logEl')
let pendingEvents: DebugEvent[] = []
let flushHandle = 0

/** Parses "30m"/"1h"/"1d" style interval labels into seconds, for sorting the stacked charts
 * coarsest-first — the same top-to-bottom order the trigger→confirmation cascade reads in. */
function intervalSeconds(label: string): number {
  const match = /^(\d+)([smhd])$/.exec(label)
  if (!match) return 0
  const value = Number(match[1])
  const unit = match[2]
  const scale = unit === 's' ? 1 : unit === 'm' ? 60 : unit === 'h' ? 3600 : 86400
  return value * scale
}

const sortedIntervals = computed(() =>
  [...seenIntervals.value].sort((a, b) => intervalSeconds(b) - intervalSeconds(a)),
)

interface ChartPanel {
  interval: string
  layout: NonNullable<ReturnType<typeof buildCandleLayout>>
  markers: { index: number; event: DebugEvent }[]
}

const chartPanels = computed<ChartPanel[]>(() =>
  sortedIntervals.value
    .map((interval) => {
      const candles = candlesByInterval.get(interval) ?? []
      const layout = buildCandleLayout(candles, chartWidth, chartHeight)
      if (!layout) return null
      const markers = (markersByInterval.get(interval) ?? [])
        .map((marker) => ({ index: candles.findIndex((c) => c.time === marker.time), event: marker.event }))
        .filter((marker) => marker.index >= 0)
      return { interval, layout, markers }
    })
    .filter((panel): panel is ChartPanel => panel !== null),
)

function markerY(event: DebugEvent, layout: ChartPanel['layout']): number {
  if (event.type === 'Decision' && event.referencePrice != null) return layout.y(event.referencePrice)
  return event.direction === 'Bearish' ? 12 : chartHeight - 12
}

function markerColor(event: DebugEvent): string {
  if (event.type === 'Decision') {
    if (event.action === 'Buy') return '#4ade80'
    if (event.action === 'Sell') return '#f87171'
    if (event.action === 'Close') return '#facc15'
    return '#94a3b8'
  }
  if (event.certainty === 'Full') return event.direction === 'Bullish' ? '#22d3ee' : '#fb923c'
  return event.direction === 'Bullish' ? '#0ea5e9aa' : '#f9731688'
}

async function loadInstruments() {
  loadingInstruments.value = true
  try {
    const response = await fetch('/api/agent-debug/instruments')
    instruments.value = await response.json()
    if (instruments.value.length > 0 && !form.instrument) {
      form.instrument = instruments.value[0].instrument
      prefillDates(instruments.value[0])
    }
  } finally {
    loadingInstruments.value = false
  }
}

function prefillDates(info: InstrumentInfo) {
  const to = new Date(info.cachedTo)
  const runStart = new Date(to)
  runStart.setUTCDate(runStart.getUTCDate() - 7)
  const warmup = new Date(runStart)
  warmup.setUTCDate(warmup.getUTCDate() - 30)
  form.warmupStart = toLocalInput(warmup)
  form.runStart = toLocalInput(runStart)
  form.runEnd = toLocalInput(to)
}

function toLocalInput(date: Date): string {
  return date.toISOString().slice(0, 16)
}

function onInstrumentChange() {
  const info = instruments.value.find((item) => item.instrument === form.instrument)
  if (info) prefillDates(info)
}

function recordCandle(event: DebugEvent) {
  if (!event.interval || event.open == null) return
  if (!candlesByInterval.has(event.interval)) {
    candlesByInterval.set(event.interval, [])
    markersByInterval.set(event.interval, [])
    seenIntervals.value = [...seenIntervals.value, event.interval]
  }
  const list = candlesByInterval.get(event.interval)!
  list.push({ time: event.time, open: event.open, high: event.high!, low: event.low!, close: event.close! })
  if (list.length > MAX_CHART_CANDLES) list.splice(0, list.length - MAX_CHART_CANDLES)
}

function recordMarker(event: DebugEvent) {
  const interval = event.interval
  if (!interval) return
  const candles = candlesByInterval.get(interval)
  if (!candles || candles.length === 0) return
  const list = markersByInterval.get(interval) ?? []
  list.push({ time: candles[candles.length - 1].time, event })
  // Markers ride along with the same rolling window as their interval's candles.
  if (list.length > MAX_CHART_CANDLES) list.splice(0, list.length - MAX_CHART_CANDLES)
  markersByInterval.set(interval, list)
}

/** Applies one buffered SSE event to reactive state. Called only from the batched flush loop -
 * never directly from eventSource.onmessage - so a fast stream never triggers more than one
 * Vue re-render per animation frame. */
function applyEvent(event: DebugEvent) {
  if (event.funnel) funnel.value = event.funnel
  if (event.type === 'Candle') recordCandle(event)
  if (event.type === 'Diagnostic' && event.certainty && event.certainty !== 'None') recordMarker(event)
  if (event.type === 'Decision') recordMarker(event)
  if (event.type === 'Complete' || event.type === 'Error') {
    running.value = false
    eventSource?.close()
    eventSource = null
  }
}

function scheduleFlush() {
  if (flushHandle) return
  flushHandle = requestAnimationFrame(() => {
    flushHandle = 0
    if (pendingEvents.length === 0) return
    const batch = pendingEvents
    pendingEvents = []

    for (const event of batch) applyEvent(event)

    const appended = logLines.value.concat(batch)
    logLines.value = appended.length > MAX_LOG_LINES
      ? appended.slice(appended.length - MAX_LOG_LINES)
      : appended
    if (logEl.value) logEl.value.scrollTop = logEl.value.scrollHeight
  })
}

function handleEvent(event: DebugEvent) {
  pendingEvents.push(event)
  scheduleFlush()
}

async function startRun() {
  runError.value = null
  logLines.value = []
  candlesByInterval.clear()
  markersByInterval.clear()
  seenIntervals.value = []
  funnel.value = null
  pendingEvents = []
  if (flushHandle) {
    cancelAnimationFrame(flushHandle)
    flushHandle = 0
  }

  const body = {
    instrument: form.instrument,
    monitoredTimeframes: form.monitoredTimeframes.split(',').map((item) => item.trim()).filter(Boolean),
    confirmationTimeframes: form.confirmationTimeframes.split(',').map((item) => item.trim()).filter(Boolean),
    warmupStart: new Date(form.warmupStart).toISOString(),
    runStart: new Date(form.runStart).toISOString(),
    runEnd: new Date(form.runEnd).toISOString(),
    allowFetch: form.allowFetch,
    quantity: form.quantity,
    rsiOverbought: form.rsiOverbought,
    rsiOversold: form.rsiOversold,
    stochRsiFastOverbought: form.stochRsiFastOverbought,
    stochRsiFastOversold: form.stochRsiFastOversold,
    partialStochRsiFastOverbought: form.partialStochRsiFastOverbought,
    partialStochRsiFastOversold: form.partialStochRsiFastOversold,
    protectiveStopAtrMultiple: form.protectiveStopAtrMultiple,
    signalFreshnessCandles: form.signalFreshnessCandles,
  }

  const response = await fetch('/api/agent-debug/runs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!response.ok) {
    const payload = await response.json().catch(() => ({ error: response.statusText }))
    runError.value = payload.error ?? 'Failed to start run.'
    return
  }
  const { id } = await response.json()
  running.value = true
  eventSource?.close()
  eventSource = new EventSource(`/api/agent-debug/runs/${id}/stream`)
  eventSource.onmessage = (message) => {
    const parsed = JSON.parse(message.data) as DebugEvent
    handleEvent(parsed)
    if (parsed.type === 'Error') runError.value = parsed.message ?? 'Run failed.'
  }
  eventSource.onerror = () => {
    running.value = false
    eventSource?.close()
    eventSource = null
  }
}

function stopRun() {
  eventSource?.close()
  eventSource = null
  running.value = false
  pendingEvents = []
  if (flushHandle) {
    cancelAnimationFrame(flushHandle)
    flushHandle = 0
  }
}

function formatNumber(value: number | undefined, digits = 5): string {
  if (value == null) return '—'
  return value.toFixed(digits)
}

function formatTime(value: string): string {
  return value.replace('T', ' ').replace(/\.\d+Z?$/, '').replace('Z', '')
}

function logClass(event: DebugEvent): string {
  if (event.type === 'Error') return 'log-error'
  if (event.type === 'Status' || event.type === 'Complete') return 'log-status'
  if (event.type === 'Decision') return 'log-decision'
  if (event.type === 'Diagnostic') {
    if (event.certainty === 'Full') return 'log-full'
    if (event.certainty === 'Partial') return 'log-partial'
    return 'log-dim'
  }
  return 'log-candle'
}

function logText(event: DebugEvent): string {
  const t = formatTime(event.time)
  switch (event.type) {
    case 'Candle':
      return `${t} [${event.interval}] O:${formatNumber(event.open)} H:${formatNumber(event.high)} L:${formatNumber(event.low)} C:${formatNumber(event.close)}  RSI:${formatNumber(event.rsi, 1)} BB:${formatNumber(event.bollingerLower)}/${formatNumber(event.bollingerMiddle)}/${formatNumber(event.bollingerUpper)}  StochRSI-fast:${formatNumber(event.stochRsiFast, 1)} slow:${formatNumber(event.stochRsiSlow, 1)}  ATR:${formatNumber(event.atr)}`
    case 'Diagnostic':
      return `${t} [${event.interval}] (${event.role}) certainty=${event.certainty} dir=${event.direction ?? '-'}${event.relationshipType ? `  rel=${event.relationshipType} strength=${formatNumber(event.relationshipStrength, 0)} fresh=${event.isNewRelationship}` : ''}`
    case 'Decision':
      return `${t} [${event.interval}] >>> ${event.action}  ref=${formatNumber(event.referencePrice)} stop=${formatNumber(event.stopLossPrice)}  ${event.reason ?? ''}`
    case 'Status':
      return `${t} --- ${event.message}`
    case 'Error':
      return `${t} !!! ${event.message}`
    case 'Complete':
      return `${t} === run complete ===`
    default:
      return t
  }
}

loadInstruments()

onBeforeUnmount(() => {
  eventSource?.close()
  if (flushHandle) cancelAnimationFrame(flushHandle)
})
</script>

<template>
  <div class="page">
    <header class="header">
      <h1>Agent Debugger</h1>
      <p class="subtitle">
        Watches DivergenceReversalAgent evaluate real cached candles, timeframe by timeframe — true
        lowest-timeframe-up aggregation, only ever the latest closed candle. Standalone from the
        Simulator/backtest pipeline.
      </p>
    </header>

    <section class="form-panel">
      <div class="form-row">
        <label>
          Instrument
          <select v-model="form.instrument" :disabled="running" @change="onInstrumentChange">
            <option v-for="item in instruments" :key="item.instrument" :value="item.instrument">
              {{ item.instrument }}
            </option>
          </select>
        </label>
        <label>
          Monitored timeframes
          <input v-model="form.monitoredTimeframes" :disabled="running" placeholder="30m,15m" />
        </label>
        <label>
          Confirmation timeframes
          <input v-model="form.confirmationTimeframes" :disabled="running" placeholder="5m,1m" />
        </label>
      </div>
      <div class="form-row">
        <label>
          Warm-up start
          <input v-model="form.warmupStart" :disabled="running" type="datetime-local" />
        </label>
        <label>
          Run start
          <input v-model="form.runStart" :disabled="running" type="datetime-local" />
        </label>
        <label>
          Run end
          <input v-model="form.runEnd" :disabled="running" type="datetime-local" />
        </label>
      </div>
      <div v-if="instruments.find((item) => item.instrument === form.instrument)" class="cache-hint">
        Cached range: {{ formatTime(instruments.find((item) => item.instrument === form.instrument)!.cachedFrom) }}
        .. {{ formatTime(instruments.find((item) => item.instrument === form.instrument)!.cachedTo) }}
        <label class="inline-check">
          <input v-model="form.allowFetch" :disabled="running" type="checkbox" />
          Fetch missing data from broker if the range above isn't fully cached
        </label>
      </div>

      <button class="advanced-toggle" type="button" @click="showAdvanced = !showAdvanced">
        {{ showAdvanced ? 'Hide' : 'Show' }} advanced options
      </button>
      <div v-if="showAdvanced" class="form-row advanced">
        <label>Quantity <input v-model.number="form.quantity" :disabled="running" type="number" /></label>
        <label>RSI overbought <input v-model.number="form.rsiOverbought" :disabled="running" type="number" /></label>
        <label>RSI oversold <input v-model.number="form.rsiOversold" :disabled="running" type="number" /></label>
        <label>StochRSI-fast overbought <input v-model.number="form.stochRsiFastOverbought" :disabled="running" type="number" /></label>
        <label>StochRSI-fast oversold <input v-model.number="form.stochRsiFastOversold" :disabled="running" type="number" /></label>
        <label>Partial overbought <input v-model.number="form.partialStochRsiFastOverbought" :disabled="running" type="number" /></label>
        <label>Partial oversold <input v-model.number="form.partialStochRsiFastOversold" :disabled="running" type="number" /></label>
        <label>Protective stop (×ATR) <input v-model.number="form.protectiveStopAtrMultiple" :disabled="running" type="number" step="0.1" /></label>
        <label title="Candles after a StochRSI-fast relationship confirms that it can still supply a signal. 0 = the original same-candle-only rule, which almost never fires because a swing pivot confirms a couple of candles after the price extreme.">
          Signal freshness (candles)
          <input v-model.number="form.signalFreshnessCandles" :disabled="running" type="number" min="0" step="1" />
        </label>
      </div>

      <div class="actions">
        <button :disabled="running || loadingInstruments" type="button" @click="startRun">
          {{ running ? 'Running…' : 'Run' }}
        </button>
        <button :disabled="!running" type="button" @click="stopRun">Stop</button>
        <span v-if="runError" class="error-text">{{ runError }}</span>
      </div>
    </section>

    <section class="chart-panel">
      <div class="chart-header">
        <span>Charts</span>
        <span class="legend">
          <span class="dot" style="background:#22d3ee"></span> Full bearish extreme
          <span class="dot" style="background:#fb923c"></span> Full bullish extreme
          <span class="dot" style="background:#4ade80"></span> Buy
          <span class="dot" style="background:#f87171"></span> Sell
          <span class="dot" style="background:#facc15"></span> Close
        </span>
      </div>
      <div v-if="chartPanels.length === 0" class="empty-hint">
        No candles yet — run the agent to populate the charts.
      </div>
      <div v-for="panel in chartPanels" :key="panel.interval" class="chart-stack-item">
        <div class="chart-stack-label">{{ panel.interval }}</div>
        <svg viewBox="0 0 900 150" class="chart-svg">
          <path :d="panel.layout.upBodies" fill="#3fb97f" />
          <path :d="panel.layout.downBodies" fill="#e5534b" />
          <path :d="panel.layout.upWicks" stroke="#3fb97f" stroke-width="1" />
          <path :d="panel.layout.downWicks" stroke="#e5534b" stroke-width="1" />
          <circle
            v-for="(marker, i) in panel.markers"
            :key="i"
            :cx="panel.layout.centerX(marker.index)"
            :cy="markerY(marker.event, panel.layout)"
            r="3.5"
            :fill="markerColor(marker.event)"
          />
        </svg>
      </div>
    </section>

    <section v-if="funnel" class="funnel-panel">
      <div class="chart-header">
        <span>Signal funnel</span>
        <span class="legend">
          {{ funnel.evaluations }} evaluations → {{ funnel.entries }} entries, {{ funnel.closes }} closes
          ({{ funnel.flips }} flips) · {{ funnel.suppressedAlreadyPositioned }} already positioned ·
          {{ funnel.breakoutHeldAgainstPosition }} breakouts held
        </span>
      </div>
      <table class="funnel-table">
        <thead>
          <tr>
            <th>Timeframe</th><th>Role</th><th>Candles</th><th>Partial</th><th>Full</th>
            <th>No rel.</th><th>Stale</th><th>Consumed</th><th>Dir. mismatch</th><th>Signals</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="stage in funnel.stages" :key="stage.interval">
            <td>{{ stage.interval }}</td>
            <td>{{ stage.role }}</td>
            <td>{{ stage.candlesProcessed }}</td>
            <td>{{ stage.partialExtremes }}</td>
            <td>{{ stage.fullExtremes }}</td>
            <td>{{ stage.noRelationship }}</td>
            <td>{{ stage.staleRelationship }}</td>
            <td>{{ stage.alreadyConsumed }}</td>
            <td>{{ stage.directionMismatch }}</td>
            <td :class="{ 'funnel-zero': stage.signalsBuilt === 0 }">{{ stage.signalsBuilt }}</td>
          </tr>
        </tbody>
      </table>
      <p class="funnel-hint">
        Each column is a stage a candle had to clear: a Full extreme reading, then a StochRSI-fast
        relationship that exists, is inside the freshness window, hasn't already been acted on, and
        points the same way as the extreme. Whichever column absorbs the Full readings is the reason
        the agent isn't trading.
      </p>
    </section>

    <section class="log-panel">
      <div class="chart-header"><span>Log</span></div>
      <div ref="logEl" class="terminal">
        <div v-for="(event, i) in logLines" :key="i" :class="['log-line', logClass(event)]">{{ logText(event) }}</div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 20px;
  max-width: 1200px;
  margin: 0 auto;
  color: var(--text, #e6e8ee);
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
}
.header h1 {
  margin: 0 0 4px;
  font-size: 20px;
}
.subtitle {
  margin: 0;
  color: var(--muted-2, #9aa3b2);
  font-size: 13px;
  max-width: 760px;
}
.form-panel, .chart-panel, .log-panel, .funnel-panel {
  border: 1px solid var(--line-soft, #2a2f3a);
  border-radius: 8px;
  padding: 14px;
  background: var(--ink-1, #12151c);
}
.form-row {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  margin-bottom: 10px;
}
.form-row.advanced {
  gap: 10px 18px;
}
.form-row label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 12px;
  color: var(--muted-2, #9aa3b2);
}
.form-row input, .form-row select {
  padding: 6px 8px;
  border-radius: 4px;
  border: 1px solid var(--line-soft, #2a2f3a);
  background: var(--ink-0, #0b0d12);
  color: inherit;
  font-size: 13px;
  min-width: 140px;
}
.cache-hint {
  font-size: 11.5px;
  color: var(--muted-2, #9aa3b2);
  margin-bottom: 8px;
  display: flex;
  align-items: center;
  gap: 10px;
}
.inline-check {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 11.5px;
  color: var(--muted-2, #9aa3b2);
  cursor: pointer;
}
.advanced-toggle {
  background: none;
  border: none;
  color: var(--mint, #3fb97f);
  cursor: pointer;
  font-size: 12px;
  padding: 0;
  margin-bottom: 8px;
}
.actions {
  display: flex;
  align-items: center;
  gap: 10px;
}
.actions button {
  padding: 7px 16px;
  border-radius: 5px;
  border: 1px solid var(--line-soft, #2a2f3a);
  background: var(--mint, #3fb97f);
  color: #06231a;
  font-weight: 600;
  cursor: pointer;
}
.actions button:disabled {
  opacity: 0.5;
  cursor: default;
}
.actions button:nth-child(2) {
  background: var(--coral, #e5534b);
  color: #2a0b09;
}
.error-text {
  color: var(--coral, #e5534b);
  font-size: 12.5px;
}
.chart-header {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 8px;
  font-size: 12.5px;
  color: var(--muted-2, #9aa3b2);
}
.chart-header select {
  padding: 3px 6px;
  background: var(--ink-0, #0b0d12);
  color: inherit;
  border: 1px solid var(--line-soft, #2a2f3a);
  border-radius: 4px;
}
.legend {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
}
.dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  margin-left: 8px;
}
.chart-stack-item {
  margin-bottom: 10px;
}
.chart-stack-item:last-child {
  margin-bottom: 0;
}
.chart-stack-label {
  font-size: 11px;
  font-weight: 700;
  color: var(--muted-2, #9aa3b2);
  margin-bottom: 3px;
}
.chart-svg {
  width: 100%;
  height: auto;
  display: block;
  background: var(--ink-0, #0b0d12);
  border-radius: 4px;
}
.empty-hint {
  color: var(--muted-2, #9aa3b2);
  font-size: 12.5px;
}
.funnel-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}
.funnel-table th, .funnel-table td {
  padding: 4px 8px;
  text-align: right;
  border-bottom: 1px solid var(--line-soft, #2a2f3a);
}
.funnel-table th:first-child, .funnel-table td:first-child,
.funnel-table th:nth-child(2), .funnel-table td:nth-child(2) {
  text-align: left;
}
.funnel-table th {
  color: var(--muted-2, #9aa3b2);
  font-weight: 600;
}
.funnel-zero {
  color: #f87171;
}
.funnel-hint {
  margin: 8px 0 0;
  color: var(--muted-2, #9aa3b2);
  font-size: 11.5px;
  line-height: 1.5;
}
.terminal {
  height: 420px;
  overflow-y: auto;
  background: #05060a;
  border-radius: 4px;
  padding: 8px 10px;
  font-family: 'SF Mono', Consolas, Menlo, monospace;
  font-size: 11.5px;
  line-height: 1.5;
}
.log-line {
  white-space: pre;
}
.log-candle { color: #5c6472; }
.log-dim { color: #45505f; }
.log-partial { color: #e0b13c; }
.log-full { color: #ff9d4d; font-weight: 600; }
.log-decision { color: #4ade80; font-weight: 700; }
.log-status { color: #60a5fa; }
.log-error { color: #f87171; font-weight: 700; }
</style>
