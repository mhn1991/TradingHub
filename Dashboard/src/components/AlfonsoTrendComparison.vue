<script setup lang="ts">
import { computed, onMounted, ref, shallowRef, watch } from 'vue'

type State = [number, number, number, boolean]
type Row = { t: number; at: number; o: number; h: number; l: number; c: number; s: State[] }
type Stats = { bars: number; directionalPercent: number; disagreementPercent: number; stateChanges: number
  forwardMatchPercent: number | null; scoredBars: number; commonMatchPercent: number | null; commonBars: number
  breakoutEvents: number; breakoutsRecognized: number; medianRecognitionBars: number | null }
type Timeframe = { minutes: number; file: string; stats: Stats[]; firstAt: number; lastAt: number }
type Instrument = { id: string; label: string; instrument: string; from: string; to: string
  simulationId: string; inputHash: string; timeframes: Timeframe[] }
type Index = { schemaVersion: number; revision: string; generatedAt: string; arms: string[]; states: string[]; instruments: Instrument[] }
type Detail = { schemaVersion: number; rows: Row[]; reasons: string[]
  events: { index: number; at: number; direction: number; delays: (number | null)[] }[] }
const index = shallowRef<Index>()
const detail = shallowRef<Detail>()
const instrumentId = ref('gold')
const minutes = ref(240)
const loading = ref(false)
const error = ref('')
const date = ref('2026-01-29T08:00')
const start = ref(0)
const cursor = ref(0)
const size = ref(240)
const jumpNotice = ref('')
const instrument = computed(() => index.value?.instruments.find(i => i.id === instrumentId.value))
const timeframe = computed(() => instrument.value?.timeframes.find(t => t.minutes === minutes.value))
const rows = computed(() => detail.value?.rows ?? [])
const visible = computed(() => rows.value.slice(start.value, start.value + size.value))
const selected = computed(() => rows.value[cursor.value])
const armShort = ['Baseline', 'Price break', 'Confirmed', 'Both']
const colors = ['#64748b', '#22c58b', '#f16c79', '#eab94f']
const names = ['Unknown', 'Up', 'Down', 'Out of alignment']
const percent = (n: number | null) => n == null ? '—' : `${n.toFixed(1)}%`
const utc = (seconds: number) => new Date(seconds * 1000).toISOString().slice(0, 16).replace('T', ' ')
const formatPrice = (value: number) => value.toLocaleString('en-GB', { maximumFractionDigits: 5 })
const baseUrl = `${import.meta.env.BASE_URL}data/backtests/`
let requestId = 0

async function loadIndex() {
  loading.value = true
  error.value = ''
  try {
    const response = await fetch(`${baseUrl}alfonso-trend-index.json`, { cache: 'no-store' })
    if (!response.ok || !response.headers.get('content-type')?.includes('application/json'))
      throw new Error('Trend comparison results are not exported yet. Reload after the runs finish.')
    const payload = await response.json() as Index
    if (payload.schemaVersion !== 1 || !payload.instruments?.length) throw new Error('Invalid trend comparison index.')
    index.value = payload
    if (!instrument.value) instrumentId.value = payload.instruments[0]!.id
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally { loading.value = false }
}
async function loadDetail() {
  const file = timeframe.value?.file
  if (!file) return
  const id = ++requestId
  loading.value = true
  error.value = ''
  detail.value = undefined
  try {
    const response = await fetch(`${baseUrl}${file}`, { cache: 'no-store' })
    if (!response.ok) throw new Error('Could not load the selected instrument/timeframe.')
    const payload = await response.json() as Detail
    if (payload.schemaVersion !== 1 || !payload.rows?.length) throw new Error('The trend timeline is empty or invalid.')
    if (id !== requestId) return
    detail.value = payload
    jump()
  } catch (e) {
    if (id === requestId) error.value = e instanceof Error ? e.message : String(e)
  } finally { if (id === requestId) loading.value = false }
}
function focus(i: number) {
  cursor.value = Math.max(0, Math.min(rows.value.length - 1, i))
  start.value = Math.max(0, Math.min(rows.value.length - size.value, cursor.value - 40))
}
function jump() {
  const at = Date.parse(`${date.value}Z`) / 1000
  if (!Number.isFinite(at)) return
  jumpNotice.value = rows.value[0] && at < rows.value[0].at
    ? 'The requested time precedes the first recorded state. Showing the earliest available candle; no trend was recorded for the requested instant.'
    : rows.value.length && at > rows.value[rows.value.length - 1]!.at
      ? 'The requested time is after the recorded history. Showing the final available state.' : ''
  // Last state actually available at the requested instant, never a future candle's label.
  let lo = 0, hi = rows.value.length
  while (lo < hi) {
    const mid = (lo + hi) >>> 1
    if (rows.value[mid]!.at <= at) lo = mid + 1
    else hi = mid
  }
  focus(Math.max(0, lo - 1))
}
function move(amount: number) { jumpNotice.value = ''; focus(cursor.value + amount) }
function nextDifference() {
  const i = rows.value.findIndex((r, i) => i > cursor.value && r.s.some(s => s[0] !== r.s[0]![0])
    && r.s.some((s, a) => s[0] !== rows.value[i-1]?.s[a]?.[0]))
  if (i >= 0) { jumpNotice.value = ''; focus(i) }
}
function nextBreakout() {
  const event = detail.value?.events.find(e => e.index > cursor.value)
  if (event) { jumpNotice.value = ''; focus(event.index) }
}
const bounds = computed(() => {
  const low = Math.min(...visible.value.map(r => r.l)), high = Math.max(...visible.value.map(r => r.h))
  const pad = (high - low || 1) * 0.08
  return { low: low - pad, high: high + pad }
})
const x = (i: number) => 112 + (i + 0.5) * 1010 / Math.max(1, visible.value.length)
const y = (price: number) => 278 - (price - bounds.value.low) / (bounds.value.high - bounds.value.low) * 258
const candleWidth = computed(() => Math.max(1, 1010 / Math.max(1, visible.value.length) * 0.7))
function selectCandle(event: MouseEvent) {
  const rect = (event.currentTarget as SVGSVGElement).getBoundingClientRect()
  const chartX = (event.clientX - rect.left) / rect.width * 1200
  const i = Math.floor((chartX - 112) / 1010 * visible.value.length)
  if (i >= 0 && i < visible.value.length) { jumpNotice.value = ''; cursor.value = start.value + i }
}
watch(timeframe, () => void loadDetail())
watch(size, () => focus(cursor.value))
onMounted(loadIndex)
</script>

<template>
  <section class="trend-comparison">
    <header>
      <div><h3>Alfonso · trend detection comparison</h3>
        <p>Baseline and three read-only variants on identical live closed-candle feeds. Entry logic is unchanged; this is not a trade-profitability comparison.</p></div>
      <button type="button" :disabled="loading" @click="loadIndex">Reload results</button>
    </header>
    <p v-if="error" role="alert">{{ error }}</p>
    <div v-if="index" class="controls">
      <label>Instrument <select v-model="instrumentId"><option v-for="i in index.instruments" :key="i.id" :value="i.id">{{ i.label }}</option></select></label>
      <label>Timeframe <select v-model="minutes"><option :value="240">4h · top direction</option><option :value="60">1h · middle</option><option :value="15">15m · lower</option></select></label>
      <span v-if="instrument" class="muted">{{ instrument.from.slice(0, 10) }} → {{ instrument.to.slice(0, 10) }} · All timestamps UTC</span>
    </div>
    <p v-if="loading" role="status">Loading trend timeline…</p>
    <template v-if="timeframe && index && detail && !loading">
      <div class="table-scroll"><table>
        <thead><tr><th>Variant</th><th>Directional coverage</th><th>Different from baseline</th><th>State changes</th><th>Next 4 bars agree</th><th>Same-bar subset agrees</th><th>Breakouts recognized</th><th>Median delay (bars)</th></tr></thead>
        <tbody><tr v-for="(s, a) in timeframe.stats" :key="a">
          <th>{{ index.arms[a] }}</th><td>{{ percent(s.directionalPercent) }}</td><td>{{ percent(s.disagreementPercent) }}</td><td>{{ s.stateChanges }}</td>
          <td>{{ percent(s.forwardMatchPercent) }}<small>n={{ s.scoredBars }}</small></td>
          <td>{{ percent(s.commonMatchPercent) }}<small>n={{ s.commonBars }}</small></td>
          <td>{{ s.breakoutsRecognized }} / {{ s.breakoutEvents }}</td><td>{{ s.medianRecognitionBars ?? '—' }}</td>
        </tr></tbody>
      </table></div>
      <details class="method"><summary>How to read this comparison</summary>
        <p>Coverage is the proportion of {{ timeframe.stats[0]?.bars.toLocaleString() }} candles labeled Up or Down. Neutral is not counted as a correct prediction. More neutral labels alone do not demonstrate better detection.</p>
        <p>“Next 4 bars agree” checks the sign of the future close change only when the variant is directional; flat moves and the last 4 candles are excluded. Different variants can score different candles. The same-bar subset scores only candles where all four variants are directional. These overlapping observations are descriptive, not independent accuracy trials or a profitability estimate.</p>
        <p>The reversal reference is a close breaking the preceding 12 candles’ high/low range in the opposite direction to the last breakout. Recognition must occur at that candle or within 12 subsequent candles, before another opposite breakout. The median includes recognized events only; always read it alongside the recognized/total count. Events too close to the dataset end are excluded. This price reference is not a ground-truth trend label.</p>
        <p>Future prices are used only in the offline evaluation, never fed early to a detector. Chart spacing counts observed candles, including across session gaps. Labels appear when the agent actually receives a closed candle, not retrospectively at its opening time.</p>
      </details>
      <form class="controls" @submit.prevent="jump">
        <label>Inspect at (UTC) <input v-model="date" type="datetime-local" required></label><button type="submit">Jump</button>
        <button type="button" @click="move(-100)">← 100 bars</button><button type="button" @click="move(100)">100 bars →</button>
        <button type="button" @click="nextDifference">Next changed disagreement</button><button type="button" @click="nextBreakout">Next reversal reference</button>
        <label>Visible candles <select v-model="size"><option :value="120">120</option><option :value="240">240</option><option :value="400">400</option></select></label>
      </form>
      <p v-if="jumpNotice" role="status">{{ jumpNotice }}</p>
      <p class="legend"><span v-for="(name, i) in names" :key="name"><i :style="{ background: colors[i] }" />{{ name }}</span> · Click a candle to inspect all four reasons.</p>
      <svg v-if="visible.length" class="timeline" viewBox="0 0 1200 430" role="img" aria-label="Candlestick chart with four aligned trend-state timelines" @click="selectCandle">
        <g v-for="fraction in [0, .25, .5, .75, 1]" :key="fraction">
          <line x1="112" x2="1122" :y1="20 + fraction * 258" :y2="20 + fraction * 258" stroke="#334155" stroke-width=".5" />
          <text x="1130" :y="24 + fraction * 258">{{ formatPrice(bounds.high - fraction * (bounds.high - bounds.low)) }}</text>
        </g>
        <g v-for="(r, i) in visible" :key="r.t">
          <line :x1="x(i)" :x2="x(i)" :y1="y(r.h)" :y2="y(r.l)" :stroke="r.c >= r.o ? colors[1] : colors[2]" />
          <rect :x="x(i) - candleWidth / 2" :y="Math.min(y(r.o), y(r.c))" :width="candleWidth" :height="Math.max(1, Math.abs(y(r.o) - y(r.c)))" :fill="r.c >= r.o ? colors[1] : colors[2]" />
          <rect v-for="(s, a) in r.s" :key="a" :x="x(i) - 505 / visible.length" :y="304 + a * 24" :width="1010 / visible.length + .2" height="18" :fill="colors[s[0]]" />
        </g>
        <text v-for="(name, a) in armShort" :key="name" x="4" :y="317 + a * 24">{{ name }}</text>
        <line v-if="cursor >= start && cursor < start + visible.length" :x1="x(cursor-start)" :x2="x(cursor-start)" y1="15" y2="402" stroke="white" stroke-dasharray="4 3" />
        <text x="112" y="424">{{ utc(visible[0]!.at) }}</text>
        <text x="1122" y="424" text-anchor="end">{{ utc(visible[visible.length - 1]!.at) }} UTC · available time</text>
      </svg>
      <template v-if="selected">
        <h4>State available {{ utc(selected.at) }} UTC · candle opened {{ utc(selected.t) }} UTC</h4>
        <p class="muted">O {{ formatPrice(selected.o) }} · H {{ formatPrice(selected.h) }} · L {{ formatPrice(selected.l) }} · C {{ formatPrice(selected.c) }}</p>
        <div class="reasons"><article v-for="(s, a) in selected.s" :key="a">
          <h4>{{ index.arms[a] }} <span :style="{ color: colors[s[0]] }">{{ names[s[0]] }}</span></h4>
          <p>{{ detail.reasons[s[1]] }}</p><small>Opposing eliminations: {{ s[2] }} · Overextended: {{ s[3] ? 'yes' : 'no' }}</small>
        </article></div>
      </template>
      <details class="method"><summary>Run provenance</summary>
        <p>Code {{ index.revision }} · Exported {{ index.generatedAt }} · Simulation {{ instrument?.simulationId }}</p>
        <p>Input hash {{ instrument?.inputHash }}. Original cache window and 21-day simulator warm-up retained; only actual agent observations are measured.</p>
      </details>
    </template>
  </section>
</template>

<style scoped>
.trend-comparison { padding: 1rem; color: var(--text-primary, #dbe5f3); min-width: 0; }
header, .controls { display: flex; align-items: center; flex-wrap: wrap; gap: .8rem; margin-bottom: 1rem; }
header { justify-content: space-between; } h3, h4 { margin: .5rem 0; } p { line-height: 1.5; }
header p, .muted, small { color: var(--text-muted, #9aaabd); } small { display: block; margin-top: .3rem; }
label { display: flex; gap: .5rem; align-items: center; }
button, select, input { background: #172334; color: #e2e8f0; border: 1px solid #42516a; border-radius: 6px; padding: .5rem .65rem; }
button { cursor: pointer; } button:disabled { opacity: .5; } .table-scroll { overflow-x: auto; }
table { border-collapse: collapse; width: 100%; font-size: .85rem; } th, td { text-align: left; padding: .75rem; border-bottom: 1px solid #334155; }
.method { margin: 1rem 0; color: #aebed1; font-size: .85rem; overflow-wrap: anywhere; } summary { cursor: pointer; }
.legend { display: flex; flex-wrap: wrap; gap: .8rem; font-size: .85rem; } .legend i { display: inline-block; width: .7rem; height: .7rem; margin-right: .3rem; }
.timeline { width: 100%; background: #101a29; border: 1px solid #334155; border-radius: 8px; cursor: crosshair; }
.timeline text { fill: #aebed1; font: 11px system-ui; } .reasons { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .7rem; }
.reasons article { padding: .8rem; background: #172334; border: 1px solid #334155; border-radius: 8px; }
.reasons h4 span { display: block; font-size: .85rem; margin-top: .3rem; } .reasons p { font-size: .9rem; }
@media (max-width: 700px) { .reasons { grid-template-columns: 1fr; } label { flex-wrap: wrap; } }
</style>
