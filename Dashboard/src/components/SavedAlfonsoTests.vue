<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import AnalysisChart from './AnalysisChart.vue'
import { savedChartIndicators } from './savedChartIndicators'
import { price, timestamp } from '../format'
import type { ChartLayers, ReplayFrame, ReplayTrade } from '../types'

type Candle = { availableAt: string; openTime: string; open: number; high: number; low: number; close: number; volume: number }
type Timeframe = '4h' | '1h' | '15m' | '5m' | '1m'
type Record = { trade: ReplayTrade; decision: { [key: string]: string }; candles: Candle[]; timeframes?: Partial<{ [key in Timeframe]: Candle[] }> }
type Run = {
  id: string; arm: string; label: string; instrument: string; generatedAt: string
  from: string; to: string; simulationId: string; records: Record[]
  performance: { tradeCount: number; winningTrades: number; netProfit: number; winRatePercent: number; maximumDrawdown: number; profitFactor: number }
}
const runs = ref<Run[]>([])
const loading = ref(true)
const error = ref('')
const experiment = ref('latest')
const title = ref('Alfonso · latest completed tests')
const arm = ref('lower-aligned')
const instrument = ref('METAL:XAU/USD')
const selectedSetup = ref('')
const showOutcome = ref(false)
const showTradeZones = ref(true)
const timeframe = ref<Timeframe>('4h')
const timeframes: { interval: Timeframe; label: string; trend?: string }[] = [
  { interval: '4h', label: '4h · Top', trend: 'topTrend' },
  { interval: '1h', label: '1h · Middle', trend: 'middleTrend' },
  { interval: '15m', label: '15m · Lower', trend: 'lowerTrend' },
  { interval: '5m', label: '5m · Confirmation', trend: 'confirmationTrend' },
  { interval: '1m', label: '1m · Execution' },
]
const run = computed(() => runs.value.find(item => item.arm === arm.value && item.instrument === instrument.value))
const instruments = computed(() => [...new Set(runs.value.map(item => item.instrument))])
const records = computed(() => [...(run.value?.records ?? [])].sort((a, b) => a.trade.signalCreatedAt.localeCompare(b.trade.signalCreatedAt)))
const selected = computed(() => records.value.find(item => item.trade.setupId === selectedSetup.value))
const availableTimeframes = computed(() => timeframes.filter(item => item.interval !== '5m' || selected.value?.timeframes?.['5m']?.length))
const summaries = computed(() => [...new Set(runs.value.map(item => item.arm))].map(key => {
  const items = runs.value.filter(item => item.arm === key)
  return { arm: key, label: items[0]?.label, trades: items.reduce((n, r) => n + r.performance.tradeCount, 0),
    wins: items.reduce((n, r) => n + r.performance.winningTrades, 0),
    net: items.reduce((n, r) => n + r.performance.netProfit, 0) }
}))
const money = (value: number) => new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'USD' }).format(value)
const number = (value?: string) => value ? Number(value).toFixed(2) : '—'
watch(run, () => { selectedSetup.value = records.value[0]?.trade.setupId ?? '' })
watch(selectedSetup, () => { showOutcome.value = false })
const chartCutoff = computed(() => timeframe.value === '1m'
  ? selected.value?.trade.openedAt ?? selected.value?.trade.signalCreatedAt
  : selected.value?.trade.signalCreatedAt)
const chartCandles = computed(() => timeframe.value === '1m'
  ? selected.value?.candles ?? [] : selected.value?.timeframes?.[timeframe.value] ?? [])
const postExitCount = computed(() => {
  const closedAt = selected.value?.trade.closedAt
  return closedAt ? chartCandles.value.filter(row => Date.parse(row.openTime) >= Date.parse(closedAt)).length : 0
})
const recordedTrend = computed(() => {
  const key = timeframes.find(item => item.interval === timeframe.value)?.trend
  return key ? selected.value?.decision[key] : null
})
const frames = computed<ReplayFrame[]>(() => {
  const candles = chartCandles.value.filter(row => showOutcome.value || Date.parse(row.availableAt) <= Date.parse(chartCutoff.value!))
  const indicators = savedChartIndicators(candles)
  return candles.map((row, index) => ({ index, availableAt: row.availableAt,
    candle: { ...row, closeTime: row.availableAt },
    indicators: { atr: null, efficiencyRatio: null, ...indicators[index]! },
    swings: [], priceZones: [], trendlines: [], channels: [], confidence: { total: 0, contributions: [] }, analysisMicroseconds: 0,
  }))
})
const layers = reactive<ChartLayers>({
  bollinger: true, bollingerRegimes: false, movingAverages: false, cci: true, rsi: true, rsiRelationships: false,
  atr: false, volume: true, swings: false, zones: false, trendlines: false, channels: false,
  donchian: false, efficiencyRatio: false, marketRegime: false, neoWave: false,
})
async function load() {
  const requested = experiment.value
  loading.value = true
  error.value = ''
  try {
    const file = requested === 'latest' ? 'alfonso-latest-tests.json'
      : requested === 'lower-alignment' ? 'alfonso-lower-alignment.json'
      : requested === 'opposing-targets' ? 'alfonso-opposing-targets.json'
      : requested === 'trend-trades' ? 'alfonso-trend-trades.json'
      : requested === 'structural' ? 'alfonso-structural-tests.json' : 'alfonso-tests.json'
    const response = await fetch(`${import.meta.env.BASE_URL}data/backtests/${file}`, { cache: 'no-store' })
    if (!response.ok) throw new Error('Saved Alfonso tests are not available on this dashboard.')
    const payload = await response.json() as { schemaVersion: number; title: string; runs: Run[] }
    if (requested !== experiment.value) return
    if (payload.schemaVersion !== 1 || !Array.isArray(payload.runs)) throw new Error('The saved test export is invalid.')
    runs.value = payload.runs
    title.value = payload.title
    arm.value = requested === 'lower-alignment' ? 'lower-aligned'
      : requested === 'opposing-targets' ? 'both-zone'
      : requested === 'trend-trades' ? 'baseline-1r'
      : requested === 'structural' ? 'structural-1r' : payload.runs[0]?.arm ?? ''
    instrument.value = payload.runs[0]?.instrument ?? ''
    timeframe.value = requested === 'stop-floor' ? '4h' : '15m'
  } catch (problem) {
    if (requested !== experiment.value) return
    error.value = problem instanceof Error ? problem.message : String(problem)
  } finally { if (requested === experiment.value) loading.value = false }
}
watch(experiment, load)
onMounted(load)
</script>

<template>
  <section class="saved-tests" aria-label="Saved Alfonso tests">
    <header>
      <h2>{{ title }}</h2>
      <p>{{ loading ? 'Loading' : runs.length }} completed runs · 1-minute fills. Select a market and trade to inspect the agent’s recorded decision.</p>
    </header>
    <label class="experiment-picker">Comparison <select v-model="experiment"><option value="latest">Latest completed tests · automatic exports</option><option value="lower-alignment">Silver · 15m direction with 5m confirmation</option><option value="opposing-targets">Silver · opposing-zone targets vs fixed R</option><option value="trend-trades">Silver · trend fixes with structural stops</option><option value="structural">Silver · structural stops and targets</option><option value="stop-floor">Previous · minimum-stop tests (six markets)</option></select></label>
    <button type="button" :disabled="loading" @click="load">Refresh tests</button>
    <p v-if="loading" role="status">Loading saved tests…</p>
    <p v-else-if="error" role="alert">{{ error }} <button type="button" @click="load">Retry</button></p>
    <template v-else>
      <div class="summaries">
        <button v-for="summary in summaries" :key="summary.arm" type="button" :class="{ active: arm === summary.arm }" :aria-pressed="arm === summary.arm" @click="arm = summary.arm">
          <strong>{{ summary.label }}</strong>
          <span>{{ summary.trades }} trades · {{ summary.trades ? (summary.wins / summary.trades * 100).toFixed(1) : '0.0' }}% wins</span>
          <span :class="summary.net < 0 ? 'loss' : 'gain'">{{ money(summary.net) }}</span>
        </button>
      </div>
      <p v-if="experiment === 'latest'" class="muted">Completed lower-alignment runner tests are published automatically, newest first. Each card is a separate run, not a combined portfolio. Compare matching markets and date ranges. Blocked submissions and cancelled unfilled orders are not listed as trades. These are in-sample results.</p>
      <p v-else-if="experiment === 'lower-alignment'" class="muted">Silver only · 15m direction must agree with the latest closed 5m trend. Higher trends no longer select direction or impose scenario-specific nesting; existing higher-timeframe zone filters remain. Orders still evaluated on 15m closes, with unchanged structural stops and opposing-zone targets. Both trend-detector fixes remain enabled. New higher-timeframe warning rules are not included yet. Small in-sample trade counts do not establish an improvement.</p>
      <p v-else-if="experiment === 'opposing-targets'" class="muted">Silver only · nearest confirmed opposing 15m zone; no fixed-R floor or cap; no zone means skip. Unchanged structural stops and entry rules. Eight fixed-R controls are reused. These are in-sample results; small trade counts do not establish an improvement.</p>
      <p v-else-if="experiment === 'trend-trades'" class="muted">Silver only · actual trading replays, with each trend fix controlling orders · unchanged core entries and 15m structural stops (48 candles). No-trend-fix controls are reused from the stop experiment. These are in-sample results; fewer trades do not prove better trend detection.</p>
      <p v-else-if="experiment === 'structural'" class="muted">Silver only · unchanged trend/entry rules · 15m swing stops, 48-candle lookback. 3R is a control; its structural run held one trade from January until test-end liquidation in July. These are in-sample results.</p>
      <p v-else class="muted">Totals combine six separate market runs. The minimum stop is a trade filter measured against the top timeframe’s ATR.</p>
      <label class="market-picker">Market <select v-model="instrument"><option v-for="market in instruments" :key="market">{{ market }}</option></select></label>
      <template v-if="run">
        <p>{{ run.label }} · {{ run.performance.tradeCount }} trades · {{ run.performance.winRatePercent.toFixed(1) }}% wins · {{ money(run.performance.netProfit) }} net · {{ money(run.performance.maximumDrawdown) }} max drawdown</p>
        <small class="muted">Saved {{ timestamp(run.generatedAt) }} UTC · Run {{ run.simulationId }}</small>
        <p class="muted">Test period: {{ timestamp(run.from) }}–{{ timestamp(run.to) }} UTC</p>
        <div class="trade-layout">
          <div class="journal">
            <h3>Trades ({{ records.length }})</h3>
            <p v-if="!records.length">No trades met the entry conditions in this run.</p>
            <button v-for="record in records" :key="record.trade.setupId" type="button" :class="{ active: selectedSetup === record.trade.setupId }" :aria-pressed="selectedSetup === record.trade.setupId" @click="selectedSetup = record.trade.setupId">
              <span>{{ timestamp(record.trade.openedAt ?? record.trade.signalCreatedAt) }} UTC · {{ record.trade.side }}</span>
              <span :class="record.trade.netProfitLoss < 0 ? 'loss' : 'gain'">{{ money(record.trade.netProfitLoss) }} · {{ record.trade.exitReason }}</span>
            </button>
          </div>
          <article v-if="selected" class="decision">
            <h3>Why this trade happened</h3>
            <p class="reason">{{ selected.trade.setupReason }}</p>
            <p class="muted">Decision recorded {{ timestamp(selected.trade.signalCreatedAt) }} UTC; filled {{ timestamp(selected.trade.openedAt ?? selected.trade.signalCreatedAt) }} UTC.</p>
            <dl>
              <div><dt>Top trend (4h)</dt><dd>{{ selected.decision.topTrend }}</dd></div>
              <div><dt>Middle trend (1h)</dt><dd>{{ selected.decision.middleTrend }}</dd></div>
              <div><dt>Lower trend (15m)</dt><dd>{{ selected.decision.lowerTrend }}</dd></div>
              <div v-if="selected.decision.confirmationTrend"><dt>Confirmation trend (5m)</dt><dd>{{ selected.decision.confirmationTrend }} · closed {{ timestamp(selected.decision.confirmationClosedAt!) }} UTC</dd></div>
              <div><dt>Entry timeframe</dt><dd>{{ selected.decision.entryTimeframe }}</dd></div>
              <template v-if="selected.decision.detailsAvailable !== 'False'">
              <div><dt>Zone</dt><dd>{{ selected.decision.side }} · {{ selected.decision.state }} · {{ selected.decision.strength }}</dd></div>
              <div><dt>Grade / score</dt><dd>{{ selected.decision.grade }} / {{ selected.decision.scoreTotal }}</dd></div>
              <div><dt>Proximal / distal</dt><dd>{{ price(Number(selected.decision.proximal)) }} / {{ price(Number(selected.decision.distal)) }}</dd></div>
              <div><dt>Nested in larger zone</dt><dd>{{ selected.decision.nested === 'True' ? 'Yes' : 'No' }}</dd></div>
              <div><dt>What the impulse achieved</dt><dd>{{ selected.decision.accomplished }}</dd></div>
              <div><dt>Impulse / base ratio</dt><dd>{{ number(selected.decision.impulseToBaseRatio) }}:1 · {{ selected.decision.baseCandles }} base candles</dd></div>
              </template>
              <div v-else><dt>Detailed decision scores</dt><dd>Not recorded in this batch; saved setup explanation shown above.</dd></div>
              <div><dt>Entry / initial stop / target</dt><dd>{{ price(selected.trade.entryPrice) }} / {{ price(selected.trade.initialStopLossPrice ?? selected.trade.stopLossPrice) }} / {{ price(selected.trade.takeProfitPrice) }}</dd></div>
              <div><dt>Quantity / fees</dt><dd>{{ selected.trade.quantity }} / {{ money(selected.trade.commission) }}</dd></div>
            </dl>
            <h4>Stop and target decision</h4>
            <p>{{ selected.trade.stopSource || 'No stop explanation recorded.' }}<template v-if="selected.trade.targetSource"> · {{ selected.trade.targetSource }}</template></p>
            <h4>Trade chart · {{ timeframe }}</h4>
            <div class="timeframe-picker" role="group" aria-label="Trade chart timeframe">
              <button v-for="option in availableTimeframes" :key="option.interval" type="button" :class="{ active: timeframe === option.interval }" :aria-pressed="timeframe === option.interval" @click="timeframe = option.interval">{{ option.label }}</button>
            </div>
            <p v-if="recordedTrend" class="chart-trend">Recorded {{ timeframe }} trend at decision: <strong>{{ recordedTrend }}</strong></p>
            <div class="chart-options">
              <label><input v-model="showOutcome" type="checkbox"> Show outcome + 200 candles after exit</label>
              <label><input v-model="showTradeZones" type="checkbox"> Show stop-loss / target zones</label>
              <label><input v-model="layers.bollinger" type="checkbox"> Bollinger Bands (20, 2)</label>
              <label><input v-model="layers.rsi" type="checkbox"> RSI (14)</label>
              <label><input v-model="layers.cci" type="checkbox"> CCI (20)</label>
            </div>
            <p class="muted">Indicators use this timeframe’s saved closed candles: Bollinger Bands (20-period SMA ± 2 standard deviations), Wilder RSI (14), CCI (20). Initial warmup values are blank. RSI is seeded from the available chart history, so it can differ from the engine’s longer warmup history. These are recalculated chart overlays, not recorded engine indicator values.</p>
            <p v-if="showOutcome" class="post-exit-count muted">{{ postExitCount }} {{ timeframe }} candles after exit.<template v-if="postExitCount < 200"> Saved history ends before 200 are available; no candles have been invented.</template></p>
            <p v-if="showTradeZones" class="zone-legend"><span class="loss">Red: entry → initial stop</span> · <span class="gain">Green: entry → target</span>. Shading extends across the chart as a price reference, not the trade’s duration.</p>
            <p class="chart-boundary muted">{{ showOutcome ? 'Outcome review — includes future candles.' : `Completed candles only, through ${timeframe === '1m' ? 'fill' : 'decision'}: ${timestamp(chartCutoff!)} UTC.` }}<template v-if="frames.length"> Last candle closed {{ timestamp(frames[frames.length - 1]!.availableAt) }} UTC · {{ frames.length }} bars available.</template></p>
            <p class="muted">{{ timeframe === '1m' ? 'Original 1-minute execution candles.' : 'Up to 200 candles before the decision, aggregated from the original 1-minute history. Incomplete buckets and market gaps are not filled.' }} Trade levels are shown for reference. Trend labels are the agent’s recorded decision, not recalculated from this chart; exact agent trendlines were not saved in this batch.</p>
            <AnalysisChart v-if="frames.length" :key="`${run.id}-${selectedSetup}`" :frames="frames" :selected-index="frames.length - 1" :window-size="Math.min(frames.length, 500)" :layers="layers" :trades="[selected.trade]" :show-trade-zones="showTradeZones" enable-trade-drawing />
            <p v-else>No {{ timeframe }} candles available for this view. Older exports need to be regenerated to include higher timeframes.</p>
            <h4>Outcome</h4>
            <p>{{ selected.trade.exitReasonText || selected.trade.exitReason }} · exit {{ price(selected.trade.exitPrice) }}<template v-if="selected.trade.closedAt"> at {{ timestamp(selected.trade.closedAt) }} UTC</template> · net {{ money(selected.trade.netProfitLoss) }}</p>
          </article>
        </div>
      </template>
    </template>
  </section>
</template>

<style scoped>
.saved-tests { padding: 1rem; color: #d9e3ef; background: #101b2b; border: 1px solid #2b3c52; border-radius: .8rem; }
h2, h3, h4 { margin: 0 0 .65rem; } h4 { margin-top: 1.2rem; }
p { line-height: 1.55; } .muted { color: #9eafc4; }
.summaries { display: grid; grid-template-columns: repeat(3, 1fr); gap: .7rem; }
button, select { color: inherit; background: #17263a; border: 1px solid #40516a; border-radius: .5rem; padding: .75rem; }
button { cursor: pointer; text-align: left; } button:hover { border-color: #7f9ab8; }
button.active { border-color: #5eead4; background: #173737; }
button:focus-visible, select:focus-visible { outline: 2px solid #5eead4; outline-offset: 2px; }
.summaries button, .journal button { display: flex; flex-direction: column; gap: .4rem; }
.loss { color: #fda4af; } .gain { color: #6ee7b7; }
.market-picker { display: flex; gap: .75rem; align-items: center; }
.timeframe-picker { display: flex; flex-wrap: wrap; gap: .5rem; margin-bottom: .8rem; }
.chart-options { display: flex; flex-wrap: wrap; gap: .6rem 1.5rem; }
.chart-trend { padding: .7rem; background: #172a3b; border-left: 3px solid #5eead4; }
.trade-layout { display: grid; grid-template-columns: 290px minmax(0, 1fr); gap: 1.2rem; margin-top: 1.3rem; }
.journal { max-height: 1050px; overflow-y: auto; } .journal button { width: 100%; margin-bottom: .45rem; }
.decision { min-width: 0; } .reason { padding: 1rem; border-left: 3px solid #5eead4; background: #172a3b; }
dl { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .7rem; }
dl div { padding: .6rem; background: #152236; border-radius: .4rem; } dt { color: #9eafc4; font-size: .8rem; } dd { margin: .3rem 0 0; overflow-wrap: anywhere; }
@media (max-width: 850px) { .trade-layout, .summaries { grid-template-columns: 1fr; } .journal { max-height: 260px; } }
@media (max-width: 500px) { dl { grid-template-columns: 1fr; } }
</style>
