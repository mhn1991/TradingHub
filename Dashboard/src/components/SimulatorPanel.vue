<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import type { SimulationJobSnapshot, StrategyProgressSnapshot } from '../types'
import { useSimulationRealtime } from '../composables/useSimulationRealtime'
import { useSimulationPlayback, type PlaybackRow } from '../composables/useSimulationPlayback'

const form = reactive({
  instrument: 'FX:GBP/JPY',
  from: '2025-01-01',
  to: '2025-02-01',
  precisionMode: 'Fast',
  sourceKind: 'OandaCandles',
  executionInterval: '1m',
  analysisBaseInterval: '1m',
  analysisIntervals: '5m,15m,1h',
  trendInterval: '1h',
  confirmationInterval: '15m',
  entryInterval: '5m',
  strategies: 'legacy,improved',
  startingBalance: 100000,
  quantity: 1000,
  leverage: 20,
  commissionRate: 0.00002,
  spreadBasisPoints: 1,
  slippageBasisPoints: 0.5,
  minimumRewardRisk: 1.5,
  warmupDays: 5,
  strategyExecutionMode: 'ParallelWorkers',
  strategyWorkerMode: 'Task',
  ambiguousIntrabarPolicy: 'ConservativeStopFirst',
  refreshCache: false,
  noCache: false,
  deterministicSeed: 12345,
})

watch(
  () => form.precisionMode,
  (mode) => {
    if (mode === 'Fast') {
      form.executionInterval = '1m'
      form.analysisBaseInterval = '1m'
      form.sourceKind = 'OandaCandles'
    } else if (mode === 'BrokerNativePrecision') {
      form.executionInterval = '5s'
      form.analysisBaseInterval = '1m'
      form.sourceKind = 'OandaCandles'
    } else if (mode === 'HighPrecision') {
      form.executionInterval = '1s'
      form.analysisBaseInterval = '1m'
      form.sourceKind = 'ImportedSecondCandles'
    }
  },
)

const selectedId = ref<string | null>(null)
const jobs = ref<SimulationJobSnapshot[]>([])
const trades = ref<unknown[]>([])
const error = ref<string | null>(null)
const busy = ref(false)
const replayRows = ref<PlaybackRow[]>([])
const loadedChunks = ref<Set<string>>(new Set())
const chunkCursor = ref(0)
const maxChartRows = 2_000

const { snapshot: liveSnapshot, connected, usingPolling, error: realtimeError } = useSimulationRealtime(selectedId)
const job = computed(() => liveSnapshot.value)
const {
  index: replayIndex,
  paused: playbackPaused,
  speed: playbackSpeed,
  followLatest,
  current: currentReplayRow,
  play,
  pause,
  step,
  jumpToEnd,
} = useSimulationPlayback(replayRows)

const activeStrategies = computed(() => job.value?.strategies ?? [])
const progressLabel = computed(() => {
  if (!job.value) return 'No simulation running'
  const source = job.value.sourceProgress
  const sourceBit = source ? ` · source ${source.phase} ${source.candlesRead}` : ''
  return `${job.value.status} · ${job.value.progressPercent.toFixed(1)}% · ${job.value.candlesPerSecond.toFixed(0)} c/s${sourceBit}`
})

let tradePoll: number | undefined
let chunkPoll: number | undefined

onBeforeUnmount(() => {
  if (tradePoll !== undefined) window.clearInterval(tradePoll)
  if (chunkPoll !== undefined) window.clearInterval(chunkPoll)
})

watch(job, (value) => {
  if (!value) return
  if (!value.isComplete) {
    void loadProgressiveReplay(value.id)
    void loadTrades(value.id)
  } else {
    void loadProgressiveReplay(value.id)
    void loadTrades(value.id)
  }
})

async function refreshJobList() {
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations?take=20`)
    if (!response.ok) return
    jobs.value = await response.json() as SimulationJobSnapshot[]
  } catch {
    // API may be offline.
  }
}

async function startSimulation() {
  busy.value = true
  error.value = null
  replayRows.value = []
  loadedChunks.value = new Set()
  chunkCursor.value = 0
  try {
    const body = {
      instrument: form.instrument,
      from: new Date(`${form.from}T00:00:00Z`).toISOString(),
      to: new Date(`${form.to}T00:00:00Z`).toISOString(),
      precisionMode: form.precisionMode,
      sourceKind: form.sourceKind,
      executionInterval: form.executionInterval,
      analysisBaseInterval: form.analysisBaseInterval,
      analysisIntervals: form.analysisIntervals.split(',').map((item) => item.trim()).filter(Boolean),
      trendInterval: form.trendInterval,
      confirmationInterval: form.confirmationInterval,
      entryInterval: form.entryInterval,
      strategies: form.strategies.split(',').map((item) => item.trim()).filter(Boolean),
      startingBalance: form.startingBalance,
      quantity: form.quantity,
      leverage: form.leverage,
      commissionRate: form.commissionRate,
      spreadBasisPoints: form.spreadBasisPoints,
      slippageBasisPoints: form.slippageBasisPoints,
      minimumRewardRisk: form.minimumRewardRisk,
      warmupDays: form.warmupDays,
      strategyExecutionMode: form.strategyExecutionMode,
      strategyWorkerMode: form.strategyWorkerMode,
      ambiguousIntrabarPolicy: form.ambiguousIntrabarPolicy,
      refreshCache: form.refreshCache,
      noCache: form.noCache,
      deterministicSeed: form.deterministicSeed,
    }
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null) as { error?: string } | null
      throw new Error(problem?.error ?? `Start failed with HTTP ${response.status}`)
    }
    const created = await response.json() as { simulationId: string; status: string }
    selectedId.value = created.simulationId
    startBackgroundPollers(created.simulationId)
    await refreshJobList()
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

function startBackgroundPollers(id: string) {
  if (tradePoll !== undefined) window.clearInterval(tradePoll)
  if (chunkPoll !== undefined) window.clearInterval(chunkPoll)
  tradePoll = window.setInterval(() => { void loadTrades(id) }, 2000)
  chunkPoll = window.setInterval(() => { void loadProgressiveReplay(id) }, 1500)
}

async function control(action: 'pause' | 'resume' | 'cancel') {
  if (!job.value) return
  busy.value = true
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${job.value.id}/${action}`, {
      method: 'POST',
    })
    if (!response.ok) throw new Error(`${action} failed with HTTP ${response.status}`)
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

async function loadTrades(id: string) {
  const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}/trades`)
  if (!response.ok) return
  trades.value = await response.json() as unknown[]
}

async function loadProgressiveReplay(id: string) {
  if (selectedId.value !== id) return
  // Prefer chunk list + incremental fetch; fall back to bounded range API.
  // Composite identity: simulationId + chunkId prevents cross-job pollution.
  const chunksResponse = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}/replay/chunks`)
  if (chunksResponse.ok) {
    const chunks = await chunksResponse.json() as Array<{ chunkId: string }>
    for (const chunk of chunks) {
      if (selectedId.value !== id) return
      const key = `${id}:${chunk.chunkId}`
      if (loadedChunks.value.has(key)) continue
      const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}/replay/chunks/${chunk.chunkId}`)
      if (!response.ok) continue
      const payload = await response.json() as PlaybackRow[] | { rows?: PlaybackRow[] }
      const rows = Array.isArray(payload) ? payload : (payload.rows ?? [])
      if (selectedId.value !== id) return
      appendRows(rows)
      loadedChunks.value.add(key)
    }
    return
  }

  const response = await fetch(
    `${import.meta.env.BASE_URL}api/simulations/${id}/replay?startSequence=${chunkCursor.value}&limit=2000`,
  )
  if (!response.ok) return
  const payload = await response.json() as { rows?: PlaybackRow[]; nextCursor?: string | null }
  appendRows(payload.rows ?? [])
  if (payload.nextCursor?.startsWith('seq:')) {
    chunkCursor.value = Number(payload.nextCursor.slice(4)) || chunkCursor.value
  }
}

function appendRows(rows: PlaybackRow[]) {
  if (!rows.length) return
  const bySequence = new Map<number, PlaybackRow>()
  for (const row of replayRows.value) bySequence.set(row.sequence, row)
  for (const row of rows) bySequence.set(row.sequence, row)
  const next = [...bySequence.values()].sort((a, b) => a.sequence - b.sequence)
  // Bound memory: keep a rolling window around the latest data.
  replayRows.value = next.length > maxChartRows ? next.slice(next.length - maxChartRows) : next
}

function selectJob(snapshot: SimulationJobSnapshot) {
  // Clear all job-specific state so chunks/trades never mix across simulations.
  replayRows.value = []
  loadedChunks.value = new Set()
  chunkCursor.value = 0
  trades.value = []
  error.value = null
  selectedId.value = snapshot.id
  startBackgroundPollers(snapshot.id)
}

function metric(strategy: StrategyProgressSnapshot, key: keyof StrategyProgressSnapshot) {
  const value = strategy[key]
  if (typeof value === 'number') {
    return value.toLocaleString(undefined, { maximumFractionDigits: 2 })
  }
  return String(value ?? '—')
}

function togglePlayback() {
  if (playbackPaused.value) play()
  else pause()
}

void refreshJobList()
</script>

<template>
  <section class="simulator-panel">
    <header class="simulator-header">
      <div>
        <h2>Dashboard Simulator</h2>
        <p>
          Streamed historical simulation with shared market frames, isolated strategy accounts,
          SignalR progress, and visual playback independent of compute speed.
        </p>
      </div>
      <div class="simulator-status">
        {{ progressLabel }}
        <small v-if="connected"> · SignalR</small>
        <small v-else-if="usingPolling"> · polling fallback</small>
      </div>
    </header>

    <div class="simulator-grid">
      <form class="card config-card" @submit.prevent="startSimulation">
        <h3>Configuration</h3>
        <label>Instrument <input v-model="form.instrument" /></label>
        <div class="row">
          <label>From <input v-model="form.from" type="date" /></label>
          <label>To <input v-model="form.to" type="date" /></label>
        </div>
        <label>
          Precision mode
          <select v-model="form.precisionMode">
            <option value="Fast">Fast — 1m execution</option>
            <option value="BrokerNativePrecision">OANDA precision — 5s execution</option>
            <option value="HighPrecision">High precision — 1s imported/recorded</option>
          </select>
        </label>
        <p class="muted">
          OANDA historical candles support 5s+ (not 1s). High precision requires imported/recorded 1s data —
          we never invent 1s bars from 1m OHLC.
        </p>
        <div class="row">
          <label>Execution interval <input v-model="form.executionInterval" /></label>
          <label>Analysis base <input v-model="form.analysisBaseInterval" /></label>
        </div>
        <label>Analysis intervals <input v-model="form.analysisIntervals" /></label>
        <div class="row">
          <label>Trend <input v-model="form.trendInterval" /></label>
          <label>Confirmation <input v-model="form.confirmationInterval" /></label>
        </div>
        <label>Entry <input v-model="form.entryInterval" /></label>
        <label>Strategies <input v-model="form.strategies" /></label>
        <div class="row">
          <label>Balance <input v-model.number="form.startingBalance" type="number" /></label>
          <label>Quantity <input v-model.number="form.quantity" type="number" /></label>
        </div>
        <div class="row">
          <label>Leverage <input v-model.number="form.leverage" type="number" /></label>
          <label>Min R:R <input v-model.number="form.minimumRewardRisk" type="number" step="0.1" /></label>
        </div>
        <div class="row">
          <label>Spread bps <input v-model.number="form.spreadBasisPoints" type="number" step="0.1" /></label>
          <label>Slippage bps <input v-model.number="form.slippageBasisPoints" type="number" step="0.1" /></label>
        </div>
        <div class="row">
          <label>Commission <input v-model.number="form.commissionRate" type="number" step="0.00001" /></label>
          <label>Warm-up days <input v-model.number="form.warmupDays" type="number" /></label>
        </div>
        <div class="row">
          <label>
            Execution
            <select v-model="form.strategyExecutionMode">
              <option>Sequential</option>
              <option>ParallelWorkers</option>
            </select>
          </label>
          <label>
            Worker
            <select v-model="form.strategyWorkerMode">
              <option>Task</option>
              <option>DedicatedThread</option>
            </select>
          </label>
        </div>
        <label>
          Ambiguity policy
          <select v-model="form.ambiguousIntrabarPolicy">
            <option>ConservativeStopFirst</option>
            <option>OptimisticTargetFirst</option>
            <option>NearestToOpenFirst</option>
          </select>
        </label>
        <div class="row checks">
          <label><input v-model="form.refreshCache" type="checkbox" /> Refresh cache</label>
          <label><input v-model="form.noCache" type="checkbox" /> No cache</label>
        </div>
        <p class="muted">Historical bid/ask fills are not enabled until OANDA bid/ask history is wired.</p>
        <button type="submit" :disabled="busy">Run Simulation</button>
        <p v-if="error || realtimeError" class="error">{{ error || realtimeError }}</p>
      </form>

      <section class="card runtime-card">
        <h3>Runtime</h3>
        <template v-if="job">
          <dl class="metrics">
            <div><dt>Simulation ID</dt><dd class="mono">{{ job.id }}</dd></div>
            <div><dt>Revision</dt><dd>{{ job.revision ?? 0 }}</dd></div>
            <div><dt>Status</dt><dd>{{ job.status }}</dd></div>
            <div><dt>Market time</dt><dd>{{ job.currentMarketTime ?? '—' }}</dd></div>
            <div><dt>Candles</dt><dd>{{ job.processedBaseCandles.toLocaleString() }}</dd></div>
            <div><dt>Progress</dt><dd>{{ job.progressPercent.toFixed(2) }}%</dd></div>
            <div><dt>Candles/sec</dt><dd>{{ job.candlesPerSecond.toFixed(1) }}</dd></div>
            <div><dt>Data source</dt><dd>{{ job.dataSourceStatus ?? '—' }}</dd></div>
            <div><dt>Input request</dt><dd class="mono">{{ job.inputRequestId ?? '—' }}</dd></div>
            <div><dt>Input hash</dt><dd class="mono">{{ job.inputHash ?? '—' }}</dd></div>
          </dl>
          <div class="controls">
            <button type="button" :disabled="busy" @click="control('pause')">Pause compute</button>
            <button type="button" :disabled="busy" @click="control('resume')">Resume compute</button>
            <button type="button" :disabled="busy" @click="control('cancel')">Cancel</button>
          </div>
        </template>
        <p v-else class="muted">Start a simulation to stream progress. Refresh reconnects via SignalR or polling.</p>

        <h3>Strategy comparison</h3>
        <table v-if="activeStrategies.length" class="compare-table">
          <thead>
            <tr>
              <th>Metric</th>
              <th v-for="strategy in activeStrategies" :key="strategy.strategyId">{{ strategy.strategyName }}</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>Balance</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-bal`">{{ metric(strategy, 'balance') }}</td>
            </tr>
            <tr>
              <td>Equity</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-eq`">{{ metric(strategy, 'equity') }}</td>
            </tr>
            <tr>
              <td>Net P/L</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-net`">{{ metric(strategy, 'netProfit') }}</td>
            </tr>
            <tr>
              <td>Trades</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-tr`">{{ metric(strategy, 'completedTrades') }}</td>
            </tr>
            <tr>
              <td>Open positions</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-op`">{{ metric(strategy, 'openPositions') }}</td>
            </tr>
          </tbody>
        </table>
        <p v-else class="muted">Strategy metrics appear as frames process.</p>

        <h3>Live trades ({{ trades.length }} payloads)</h3>
        <p class="muted">Completed trades stream while the simulation is still running.</p>
      </section>

      <section class="card playback-card">
        <h3>Visual playback</h3>
        <p class="muted">Playback is independent from backend compute pause/resume. Rows load progressively from chunks.</p>
        <div class="controls">
          <button type="button" @click="togglePlayback">{{ playbackPaused ? 'Play' : 'Pause' }} playback</button>
          <button type="button" @click="step(-1)">Step back</button>
          <button type="button" @click="step(1)">Step forward</button>
          <button type="button" @click="jumpToEnd(); followLatest = true">Follow latest</button>
          <label>
            Speed
            <select v-model.number="playbackSpeed">
              <option :value="1">1×</option>
              <option :value="5">5×</option>
              <option :value="10">10×</option>
              <option :value="25">25×</option>
              <option :value="50">50×</option>
              <option :value="100">100×</option>
            </select>
          </label>
        </div>
        <dl v-if="currentReplayRow" class="metrics">
          <div><dt>Index</dt><dd>{{ replayIndex + 1 }} / {{ replayRows.length }}</dd></div>
          <div><dt>Sequence</dt><dd>{{ currentReplayRow.sequence }}</dd></div>
          <div><dt>Time</dt><dd>{{ currentReplayRow.availableAt }}</dd></div>
          <div><dt>OHLC</dt><dd>{{ currentReplayRow.open }} / {{ currentReplayRow.high }} / {{ currentReplayRow.low }} / {{ currentReplayRow.close }}</dd></div>
          <div><dt>Warm-up</dt><dd>{{ currentReplayRow.isWarmup ? 'yes' : 'no' }}</dd></div>
          <div><dt>Follow</dt><dd>{{ followLatest ? 'latest' : 'scrubbing' }}</dd></div>
        </dl>
        <p v-else class="muted">Waiting for first replay chunk…</p>

        <h3>Recent jobs</h3>
        <ul class="job-list">
          <li v-for="item in jobs" :key="item.id">
            <button type="button" class="linkish" @click="selectJob(item)">
              {{ item.instrument }} · {{ item.status }} · {{ item.progressPercent.toFixed(0) }}%
            </button>
          </li>
        </ul>
        <button type="button" class="secondary" @click="refreshJobList">Refresh jobs</button>
      </section>
    </div>
  </section>
</template>

<style scoped>
.simulator-panel {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 1rem 1.25rem 2rem;
}
.simulator-header {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  align-items: flex-start;
}
.simulator-header h2 { margin: 0 0 0.25rem; }
.simulator-header p, .muted {
  margin: 0;
  color: var(--muted, #8b93a7);
  max-width: 60ch;
}
.simulator-status {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.85rem;
  padding: 0.5rem 0.75rem;
  border-radius: 0.5rem;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.08);
}
.simulator-grid {
  display: grid;
  grid-template-columns: minmax(260px, 1fr) minmax(320px, 1.3fr) minmax(260px, 1fr);
  gap: 1rem;
}
.card {
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 0.85rem;
  padding: 1rem;
  display: flex;
  flex-direction: column;
  gap: 0.65rem;
}
.card h3 { margin: 0.25rem 0; }
label {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  font-size: 0.85rem;
}
input, select, button { font: inherit; }
input, select {
  border-radius: 0.4rem;
  border: 1px solid rgba(255, 255, 255, 0.12);
  background: rgba(0, 0, 0, 0.25);
  color: inherit;
  padding: 0.4rem 0.5rem;
}
.row { display: grid; grid-template-columns: 1fr 1fr; gap: 0.5rem; }
.checks label { flex-direction: row; align-items: center; gap: 0.4rem; }
button {
  border: 0;
  border-radius: 0.5rem;
  padding: 0.55rem 0.8rem;
  background: #3b82f6;
  color: white;
  cursor: pointer;
}
button:disabled { opacity: 0.5; cursor: not-allowed; }
button.secondary {
  background: transparent;
  border: 1px solid rgba(255, 255, 255, 0.16);
}
.controls { display: flex; flex-wrap: wrap; gap: 0.5rem; }
.metrics { display: grid; gap: 0.35rem; margin: 0; }
.metrics > div {
  display: grid;
  grid-template-columns: 8rem 1fr;
  gap: 0.5rem;
  font-size: 0.9rem;
}
.metrics dt { margin: 0; color: var(--muted, #8b93a7); }
.metrics dd { margin: 0; }
.mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  word-break: break-all;
}
.compare-table { width: 100%; border-collapse: collapse; font-size: 0.88rem; }
.compare-table th, .compare-table td {
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  padding: 0.35rem 0.4rem;
  text-align: left;
}
.job-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.35rem; }
.linkish {
  background: transparent;
  border: 1px solid rgba(255, 255, 255, 0.1);
  width: 100%;
  text-align: left;
}
.error { color: #f87171; margin: 0; }
@media (max-width: 1100px) {
  .simulator-grid { grid-template-columns: 1fr; }
}
</style>
