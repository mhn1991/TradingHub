<script setup lang="ts">
import { computed, markRaw, onBeforeUnmount, onMounted, reactive, ref, shallowReactive, shallowRef, watch } from 'vue'
import AnalysisChart from './components/AnalysisChart.vue'
import SimulatorPanel from './components/SimulatorPanel.vue'
import { duration, price, timestamp } from './format'
import { metricsAt } from './seriesMetrics'
import type {
  BacktestManifest,
  BacktestMarketDataset,
  BacktestRunDataset,
  ChartLayers,
  LiveFeedStatus,
  LiveReplayPayload,
  LiveReplayUpdate,
  OandaOrderEventBatch,
  OandaWorkspaceAccount,
  OrderSubmission,
  ReplayDataset,
  ReplayFrame,
  ReplayTrade,
  ReplaySeries,
  WorkspaceAsset,
  WorkspaceBroker,
  WorkspaceCatalog,
  WorkspaceDefinition,
  WorkspaceSnapshot,
} from './types'

const workspaceStorageKey = 'tradinghub.workspaces.v1'
const fallbackTimeframes = ['1m', '3m', '5m', '15m', '30m', '1h', '4h', '1d']
const fallbackCatalog: WorkspaceCatalog = {
  generatedAt: new Date(0).toISOString(),
  streamingSelection: { brokerId: 'binance', symbol: 'BTCUSDT', interval: '1m' },
  brokers: [
    {
      id: 'simulator', displayName: 'Simulator', environment: 'Demo', dataKind: 'Replay',
      isConfigured: true, isReadOnly: true, canTrade: false,
      description: 'Deterministic local replay; no orders or broker account.',
      assets: [{ symbol: 'EURUSD', displayName: 'EUR / USD', instrument: 'FX:EUR/USD', timeframes: ['5m', '15m', '1h'] }],
    },
    {
      id: 'binance', displayName: 'Binance Spot', environment: 'Live', dataKind: 'Market',
      isConfigured: true, isReadOnly: true, canTrade: false,
      description: 'Live public market data only; no API keys and no order access.',
      assets: [
        ['BTCUSDT', 'BTC / USDT', 'CRYPTO:BTC/USDT'],
        ['ETHUSDT', 'ETH / USDT', 'CRYPTO:ETH/USDT'],
        ['BNBUSDT', 'BNB / USDT', 'CRYPTO:BNB/USDT'],
        ['SOLUSDT', 'SOL / USDT', 'CRYPTO:SOL/USDT'],
        ['XRPUSDT', 'XRP / USDT', 'CRYPTO:XRP/USDT'],
      ].map(([symbol, displayName, instrument]) => ({ symbol, displayName, instrument, timeframes: fallbackTimeframes })),
    },
    {
      id: 'oanda', displayName: 'OANDA', environment: 'Demo', dataKind: 'Market',
      isConfigured: false, isReadOnly: true, canTrade: false,
      description: 'OANDA is disabled on the backend. Set Oanda__Enabled, Oanda__AccountId, and Oanda__AccessToken before starting DashboardLive.', assets: [],
    },
    {
      id: 'ig', displayName: 'IG', environment: 'Demo', dataKind: 'Market',
      isConfigured: false, isReadOnly: true, canTrade: false,
      description: 'Add IG demo credentials and EPIC mappings to enable this broker.', assets: [],
    },
  ],
}

const dataset = shallowRef<ReplayDataset | null>(null)
const replayDataset = shallowRef<ReplayDataset | null>(null)
const backtestManifest = ref<BacktestManifest | null>(null)
const backtestMarket = shallowRef<BacktestMarketDataset | null>(null)
const selectedBacktestId = ref('')
const workspaceCatalog = ref<WorkspaceCatalog>(fallbackCatalog)
const workspaces = ref<WorkspaceDefinition[]>([])
const activeWorkspaceId = ref('')
const mode = ref<'replay' | 'live'>('replay')
const uiView = ref<'workspaces' | 'simulator'>('workspaces')
const liveStatus = ref<LiveFeedStatus | null>(null)
const activeSeriesIndex = ref(0)
const selectedIndex = ref(0)
const windowSize = ref(100)
const playbackSpeed = ref(4)
const isPlaying = ref(false)
const loading = ref(true)
const loadError = ref<string | null>(null)
const catalogWarning = ref<string | null>(null)
const oandaAccount = ref<OandaWorkspaceAccount | null>(null)
const oandaAccountLoading = ref(false)
const ordersArmed = ref(false)
const orderSubmitting = ref(false)
const orderNotice = ref<string | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)
const orderForm = reactive({
  side: 'Buy',
  type: 'Market',
  units: 100,
  price: null as number | null,
  stopLoss: null as number | null,
  takeProfit: null as number | null,
})
const layers = reactive<ChartLayers>({
  priceAction: true,
  bollinger: true,
  bollingerRegimes: true,
  rsiRelationships: true,
  atr: true,
  volume: true,
  swings: true,
  zones: true,
  trendlines: true,
  channels: true,
  donchian: true,
  efficiencyRatio: true,
  marketRegime: true,
})
let playbackTimer: number | undefined
let marketPollTimer: number | undefined
let workspacePersistTimer: number | undefined
let liveEvents: EventSource | undefined
let orderEvents: EventSource | undefined
let sourceAbortController: AbortController | undefined
let sourceGeneration = 0

function humanizeEnum(value: string | null | undefined): string {
  if (!value || value === 'Unknown' || value === 'None') return 'Waiting for context'
  return value.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
}

const activeWorkspace = computed<WorkspaceDefinition | null>(() =>
  workspaces.value.find((workspace) => workspace.id === activeWorkspaceId.value) ?? null,
)
const activeBroker = computed<WorkspaceBroker | null>(() =>
  workspaceCatalog.value.brokers.find((broker) => broker.id === activeWorkspace.value?.brokerId) ?? null,
)
const availableAssets = computed<WorkspaceAsset[]>(() => activeBroker.value?.assets ?? [])
const activeAsset = computed<WorkspaceAsset | null>(() =>
  availableAssets.value.find((asset) => asset.symbol === activeWorkspace.value?.symbol) ?? null,
)
const availableTimeframes = computed(() => {
  if (activeBroker.value?.dataKind === 'Replay' && replayDataset.value) {
    return replayDataset.value.series.map((series) => series.interval)
  }
  return activeAsset.value?.timeframes ?? []
})
const activeSeries = computed<ReplaySeries | null>(() =>
  dataset.value?.series[activeSeriesIndex.value] ?? null,
)
const selectedBacktestRun = computed(() =>
  backtestManifest.value?.runs.find((run) => run.id === selectedBacktestId.value) ?? null,
)
const replayPerformance = computed(() => dataset.value?.performance ?? null)
const replayTrades = computed(() => [...(dataset.value?.trades ?? [])]
  .sort((left, right) => new Date(right.signalCreatedAt).getTime() - new Date(left.signalCreatedAt).getTime()),
)
const currentFrame = computed<ReplayFrame | null>(() =>
  activeSeries.value?.frames[selectedIndex.value] ?? null,
)
const currentStructure = computed(() => currentFrame.value?.marketStructure ?? ({
  direction: 'Unknown' as const,
  previousDirection: 'Unknown' as const,
  break: 'None' as const,
  directionChanged: false,
  segmentStartedAt: null,
  changedAt: null,
  lastSwingHigh: null,
  lastSwingLow: null,
  consecutiveHigherHighs: 0,
  consecutiveHigherLows: 0,
  consecutiveLowerHighs: 0,
  consecutiveLowerLows: 0,
  strength: 0,
}))
const environmentLabel = computed(() => {
  const broker = activeBroker.value
  if (!broker) return 'Unknown'
  if (!broker.isConfigured) return `${broker.environment} · Setup required`
  if (broker.canTrade) return `${broker.environment} · Demo orders`
  return `${broker.environment}${broker.isReadOnly ? ' · Read only' : ''}`
})
const isReady = computed(() =>
  currentFrame.value?.indicators.rsi != null &&
  currentFrame.value?.indicators.bollingerMiddle != null &&
  currentFrame.value?.indicators.atr != null,
)
const rsiState = computed(() => {
  const value = currentFrame.value?.indicators.rsi
  if (value == null) return { label: 'Warming up', tone: 'muted' }
  if (value >= 70) return { label: 'Overbought', tone: 'danger' }
  if (value <= 30) return { label: 'Oversold', tone: 'positive' }
  if (value >= 55) return { label: 'Bullish pressure', tone: 'positive' }
  if (value <= 45) return { label: 'Bearish pressure', tone: 'danger' }
  return { label: 'Balanced', tone: 'neutral' }
})
const rsiRelationship = computed(() =>
  currentFrame.value?.indicators.rsiAnalysis?.latestRelationship ?? null,
)
const newRsiRelationship = computed(() =>
  currentFrame.value?.indicators.rsiAnalysis?.isNewRelationship
    ? currentFrame.value.indicators.rsiAnalysis.latestRelationship
    : null,
)
const rsiContextLabel = computed(() => {
  const relationship = rsiRelationship.value
  if (relationship && relationship.ageCandles <= 10) {
    return `${humanizeEnum(relationship.type)} · ${relationship.strength.toFixed(0)} strength`
  }
  const analysis = currentFrame.value?.indicators.rsiAnalysis
  if (!analysis) return rsiState.value.label
  return `${humanizeEnum(analysis.momentumDirection)} momentum · ${humanizeEnum(analysis.zone)}`
})
const bollingerContextLabel = computed(() => {
  const analysis = currentFrame.value?.indicators.bollingerAnalysis
  if (!analysis || analysis.widthRegime === 'Unknown') return bandWidthLabel.value
  const release = analysis.squeezeReleased ? 'Squeeze released' : humanizeEnum(analysis.widthRegime)
  return `${release} · ${humanizeEnum(analysis.widthDirection)}`
})
const atrContextLabel = computed(() => {
  const analysis = currentFrame.value?.indicators.atrAnalysis
  if (!analysis || analysis.regime === 'Unknown') return atrLabel.value
  return `${humanizeEnum(analysis.regime)} · ${humanizeEnum(analysis.direction)}`
})
const bandPosition = computed(() => {
  const frame = currentFrame.value
  const upper = frame?.indicators.bollingerUpper
  const lower = frame?.indicators.bollingerLower
  if (!frame || upper == null || lower == null || upper === lower) return null
  return ((frame.candle.close - lower) / (upper - lower)) * 100
})
const bandWidthPips = computed(() => {
  const upper = currentFrame.value?.indicators.bollingerUpper
  const lower = currentFrame.value?.indicators.bollingerLower
  return upper == null || lower == null ? null : (upper - lower) * 10_000
})
const candleMovePips = computed(() => {
  const candle = currentFrame.value?.candle
  return candle ? (candle.close - candle.open) * 10_000 : 0
})
const isForex = computed(() => dataset.value?.instrument.startsWith('FX:') ?? false)
const candleMovementLabel = computed(() => {
  const candle = currentFrame.value?.candle
  if (!candle) return '—'
  if (isForex.value) {
    return `${candleMovePips.value >= 0 ? '+' : ''}${candleMovePips.value.toFixed(1)} pips this candle`
  }
  const percentage = ((candle.close - candle.open) / candle.open) * 100
  return `${percentage >= 0 ? '+' : ''}${percentage.toFixed(3)}% this candle`
})
const bandWidthLabel = computed(() => {
  const frame = currentFrame.value
  const upper = frame?.indicators.bollingerUpper
  const lower = frame?.indicators.bollingerLower
  const middle = frame?.indicators.bollingerMiddle
  if (upper == null || lower == null || middle == null) return 'Waiting for period'
  return isForex.value
    ? `${bandWidthPips.value!.toFixed(1)} pip width`
    : `${(((upper - lower) / middle) * 100).toFixed(3)}% width`
})
const atrLabel = computed(() => {
  const frame = currentFrame.value
  const atr = frame?.indicators.atr
  if (!frame || atr == null) return 'Warming up'
  return isForex.value
    ? `${(atr * 10_000).toFixed(1)} pips`
    : `${((atr / frame.candle.close) * 100).toFixed(3)}% of price`
})
const freshSwings = computed(() =>
  currentFrame.value?.swings.filter((swing) =>
    swing.confirmedAt === currentFrame.value?.availableAt,
  ) ?? [],
)
const strongestZones = computed(() =>
  [...(currentFrame.value?.priceZones ?? [])]
    .sort((left, right) => right.strength - left.strength)
    .slice(0, 4),
)
const selectedSeriesMetrics = computed(() => {
  const series = activeSeries.value
  return series
    ? metricsAt(series, selectedIndex.value, isForex.value)
    : {
        timing: { average: 0, p95: 0, maximum: 0 },
        integrity: { gaps: 0, futureSwings: 0, warmupFrames: 0 },
      }
})
const timing = computed(() => {
  return selectedSeriesMetrics.value.timing
})
const integrity = computed(() => {
  const metrics = selectedSeriesMetrics.value.integrity
  return {
    gaps: Math.max(metrics.gaps, mode.value === 'live' ? (liveStatus.value?.gapsDetected ?? 0) : 0),
    futureSwings: metrics.futureSwings,
    warmupFrames: metrics.warmupFrames,
  }
})
const confidenceTone = computed(() => {
  const value = currentFrame.value?.confidence.total ?? 0
  if (value >= 70) return 'strong'
  if (value >= 40) return 'developing'
  return 'quiet'
})
const sourceLabel = computed(() => {
  if (!activeBroker.value) return 'No workspace selected'
  if (!activeBroker.value.isConfigured) return `${activeBroker.value.displayName} · Setup required`
  return mode.value === 'live'
    ? `${liveStatus.value?.symbol ?? activeAsset.value?.displayName ?? 'Public feed'} · ${liveStatus.value?.state ?? 'Connecting'}`
    : 'Deterministic replay'
})
const sourceStateClass = computed(() => {
  if (!activeBroker.value?.isConfigured) return 'stopped'
  if (mode.value === 'replay') return 'replay'
  return (liveStatus.value?.state ?? 'Starting').toLowerCase()
})
const emptyTitle = computed(() => {
  if (activeBroker.value && !activeBroker.value.isConfigured) return `${activeBroker.value.displayName} is not configured`
  return mode.value === 'live' ? 'Connecting workspace data' : (loading.value ? 'Preparing analysis replay' : 'Replay unavailable')
})
const emptyMessage = computed(() => {
  if (activeBroker.value && !activeBroker.value.isConfigured) return activeBroker.value.description
  if (mode.value === 'live') return liveStatus.value?.message ?? loadError.value ?? 'Loading closed candles and calculating indicators…'
  return loading.value ? 'Loading deterministic indicator and annotation snapshots…' : loadError.value
})

onMounted(async () => {
  window.addEventListener('keydown', handleKeyboard)
  const [replayResult, catalogResult] = await Promise.allSettled([
    loadSampleReplay(),
    loadWorkspaceCatalog(),
  ])
  if (replayResult.status === 'rejected') {
    loadError.value = messageFrom(replayResult.reason, 'The replay could not be loaded.')
  }
  if (catalogResult.status === 'rejected') {
    catalogWarning.value = 'Broker discovery is offline; using the built-in pair list.'
  }

  workspaces.value = restoreWorkspaces()
  workspaces.value.forEach(normalizeWorkspace)
  const queryMode = new URLSearchParams(window.location.search).get('mode')
  activeWorkspaceId.value = queryMode === 'live'
    ? (workspaces.value.find((workspace) => workspace.brokerId === 'binance')?.id ?? workspaces.value[0].id)
    : workspaces.value[0].id
  await loadActiveWorkspace()
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', handleKeyboard)
  stopPlaybackTimer()
  disconnectDataSource()
  if (workspacePersistTimer !== undefined) window.clearTimeout(workspacePersistTimer)
  persistWorkspaces()
})

watch([isPlaying, playbackSpeed, activeSeries], () => {
  stopPlaybackTimer()
  if (!isPlaying.value || !activeSeries.value) return
  playbackTimer = window.setInterval(stepForward, Math.max(50, 1_000 / playbackSpeed.value))
})

watch(workspaces, () => {
  if (workspacePersistTimer !== undefined) window.clearTimeout(workspacePersistTimer)
  workspacePersistTimer = window.setTimeout(persistWorkspaces, 250)
}, { deep: true, flush: 'post' })

function persistWorkspaces() {
  workspacePersistTimer = undefined
  if (workspaces.value.length) {
    localStorage.setItem(workspaceStorageKey, JSON.stringify(workspaces.value))
  }
}

async function loadSampleReplay() {
  const manifestResponse = await fetch(`${import.meta.env.BASE_URL}data/backtests/manifest.json`, { cache: 'no-store' })
    .catch(() => null)
  if (manifestResponse?.ok) {
    const manifest = await manifestResponse.json() as BacktestManifest
    if (manifest.schemaVersion === 1 && Array.isArray(manifest.runs) && manifest.runs.length > 0) {
      backtestManifest.value = manifest
      const queryStrategy = new URLSearchParams(window.location.search).get('strategy')
      selectedBacktestId.value = manifest.runs.some((run) => run.id === queryStrategy)
        ? queryStrategy!
        : (manifest.runs.find((run) => run.id.includes('improved'))?.id ?? manifest.runs[0].id)
      await loadBacktestRun(selectedBacktestId.value)
      return
    }
  }

  const response = await fetch(`${import.meta.env.BASE_URL}data/sample-replay.json`)
  if (!response.ok) throw new Error(`Replay request failed with HTTP ${response.status}.`)
  replayDataset.value = validateDataset(await response.json())
  syncReplayAsset(replayDataset.value)
}

async function loadBacktestRun(id: string) {
  const manifest = backtestManifest.value
  const run = manifest?.runs.find((item) => item.id === id)
  if (!manifest || !run) throw new Error(`Backtest strategy '${id}' was not found in the manifest.`)

  if (!backtestMarket.value) {
    const marketResponse = await fetch(
      `${import.meta.env.BASE_URL}data/backtests/${manifest.marketFile}`,
      { cache: 'no-store' },
    )
    if (!marketResponse.ok) throw new Error(`Backtest market replay failed with HTTP ${marketResponse.status}.`)
    const market = await marketResponse.json() as BacktestMarketDataset
    if (market.schemaVersion !== 1 || !Array.isArray(market.series) || market.series.length === 0) {
      throw new Error('Backtest market replay is invalid.')
    }
    backtestMarket.value = market
  }

  const response = await fetch(`${import.meta.env.BASE_URL}data/backtests/${run.file}`, { cache: 'no-store' })
  if (!response.ok) throw new Error(`Backtest strategy result failed with HTTP ${response.status}.`)
  const strategy = await response.json() as BacktestRunDataset
  const market = backtestMarket.value
  if (!market || strategy.schemaVersion !== 1 || !Array.isArray(strategy.trades) || !strategy.performance) {
    throw new Error('Backtest strategy result is invalid.')
  }

  selectedBacktestId.value = run.id
  replayDataset.value = validateDataset({
    schemaVersion: 1,
    title: strategy.title,
    instrument: market.instrument,
    generatedAt: market.generatedAt,
    source: market.source,
    parameters: market.parameters,
    series: market.series,
    trades: strategy.trades,
    performance: strategy.performance,
  })
  syncReplayAsset(replayDataset.value)
}

async function changeBacktest(event: Event) {
  await selectBacktest((event.target as HTMLSelectElement).value)
}

async function selectBacktest(id: string) {
  if (!id || id === selectedBacktestId.value) return
  loading.value = true
  loadError.value = null
  try {
    await loadBacktestRun(id)
    if (activeBroker.value?.id === 'simulator' && replayDataset.value) {
      setDataset(replayDataset.value)
      const workspace = activeWorkspace.value
      const index = replayDataset.value.series.findIndex((series) => series.interval === workspace?.interval)
      selectSeries(index < 0 ? 0 : index)
    }
  } catch (error) {
    loadError.value = messageFrom(error, 'The selected backtest could not be loaded.')
  } finally {
    loading.value = false
  }
}

async function loadWorkspaceCatalog() {
  const response = await fetch(`${import.meta.env.BASE_URL}api/workspaces/catalog`)
  if (!response.ok) throw new Error(`Workspace catalog failed with HTTP ${response.status}.`)
  const value = await response.json() as WorkspaceCatalog
  if (!Array.isArray(value.brokers) || value.brokers.length === 0 || !value.streamingSelection) {
    throw new Error('The workspace catalog response was invalid.')
  }
  workspaceCatalog.value = value
  catalogWarning.value = value.warning ?? null
}

function restoreWorkspaces(): WorkspaceDefinition[] {
  try {
    const stored = JSON.parse(localStorage.getItem(workspaceStorageKey) ?? 'null') as unknown
    if (Array.isArray(stored)) {
      const valid = stored.filter((item): item is WorkspaceDefinition => {
        const value = item as Partial<WorkspaceDefinition>
        return typeof value.id === 'string' && typeof value.name === 'string' &&
          typeof value.brokerId === 'string' && typeof value.symbol === 'string' &&
          typeof value.interval === 'string'
      }).slice(0, 8)
      if (valid.length) return valid
    }
  } catch {
    localStorage.removeItem(workspaceStorageKey)
  }
  return [
    { id: createId(), name: 'FX Research', brokerId: 'simulator', symbol: 'EURUSD', interval: '5m' },
    { id: createId(), name: 'Crypto Live', brokerId: 'binance', symbol: 'BTCUSDT', interval: '1m' },
  ]
}

function normalizeWorkspace(workspace: WorkspaceDefinition) {
  const broker = workspaceCatalog.value.brokers.find((item) => item.id === workspace.brokerId)
    ?? workspaceCatalog.value.brokers[0]
  workspace.brokerId = broker.id
  if (!broker.assets.length) {
    workspace.symbol = ''
    workspace.interval = ''
    return
  }
  const stream = workspaceCatalog.value.streamingSelection
  const preferredAsset = broker.id === stream.brokerId
    ? broker.assets.find((item) => item.symbol === stream.symbol)
    : broker.id === 'oanda'
      ? broker.assets.find((item) => item.symbol === 'EUR_USD')
    : undefined
  const asset = broker.assets.find((item) => item.symbol === workspace.symbol) ?? preferredAsset ?? broker.assets[0]
  workspace.symbol = asset.symbol
  if (!asset.timeframes.includes(workspace.interval)) workspace.interval = asset.timeframes[0]
}

function setDataset(value: ReplayDataset, preservePosition = false) {
  dataset.value = markRaw({
    ...value,
    series: value.series.map((series) => markRaw({
      ...series,
      frames: shallowReactive(series.frames),
    })),
  })
  activeSeriesIndex.value = 0
  if (!preservePosition || mode.value === 'live') selectedIndex.value = Math.max(0, value.series[0].frames.length - 1)
  isPlaying.value = false
  loadError.value = null
}

function validateDataset(value: unknown): ReplayDataset {
  const candidate = value as Partial<ReplayDataset>
  if (!candidate || candidate.schemaVersion !== 1 || typeof candidate.instrument !== 'string' ||
    !Array.isArray(candidate.series) || candidate.series.length === 0 ||
    candidate.series.some((series) => !Array.isArray(series.frames) || series.frames.length === 0)) {
    throw new Error('This is not a supported TradingHub replay (schema version 1).')
  }
  return candidate as ReplayDataset
}

async function activateWorkspace(id: string) {
  if (!workspaces.value.some((workspace) => workspace.id === id)) return
  activeWorkspaceId.value = id
  await loadActiveWorkspace()
}

async function loadActiveWorkspace() {
  disconnectDataSource()
  sourceAbortController = new AbortController()
  const workspace = activeWorkspace.value
  const broker = activeBroker.value
  dataset.value = null
  liveStatus.value = null
  loadError.value = null
  loading.value = true
  isPlaying.value = false
  if (!workspace || !broker) {
    loading.value = false
    return
  }
  if (!broker.isConfigured || !activeAsset.value) {
    mode.value = broker.dataKind === 'Replay' ? 'replay' : 'live'
    loading.value = false
    return
  }
  if (broker.dataKind === 'Replay') {
    mode.value = 'replay'
    if (replayDataset.value) {
      setDataset(replayDataset.value)
      const seriesIndex = replayDataset.value.series.findIndex((series) => series.interval === workspace.interval)
      selectSeries(seriesIndex < 0 ? 0 : seriesIndex)
    }
    loading.value = false
    return
  }

  mode.value = 'live'
  const stream = workspaceCatalog.value.streamingSelection
  if (broker.id === stream.brokerId && workspace.symbol === stream.symbol && workspace.interval === stream.interval) {
    connectLive()
    return
  }
  if (broker.id === 'binance') {
    const generation = sourceGeneration
    await refreshMarketSnapshot(generation)
    scheduleMarketSnapshot(generation, workspace.interval)
    return
  }
  if (broker.id === 'oanda') {
    const query = new URLSearchParams({ symbol: workspace.symbol, interval: workspace.interval })
    connectLive(`api/workspaces/oanda/events?${query}`, false)
    void loadOandaAccount(sourceGeneration)
    connectOandaOrderEvents(sourceGeneration)
    return
  }
  loading.value = false
  loadError.value = `${broker.displayName} workspace data is not implemented yet.`
}

async function changeBroker(event: Event) {
  const workspace = activeWorkspace.value
  if (!workspace) return
  workspace.brokerId = (event.target as HTMLSelectElement).value
  normalizeWorkspace(workspace)
  await loadActiveWorkspace()
}

async function changeAsset(event: Event) {
  const workspace = activeWorkspace.value
  const broker = activeBroker.value
  if (!workspace || !broker) return
  workspace.symbol = (event.target as HTMLSelectElement).value
  const asset = broker.assets.find((item) => item.symbol === workspace.symbol)
  if (asset && !asset.timeframes.includes(workspace.interval)) workspace.interval = asset.timeframes[0]
  await loadActiveWorkspace()
}

async function changeTimeframe(interval: string) {
  const workspace = activeWorkspace.value
  if (!workspace || workspace.interval === interval) return
  workspace.interval = interval
  if (mode.value === 'replay' && dataset.value) {
    const index = dataset.value.series.findIndex((series) => series.interval === interval)
    if (index >= 0) selectSeries(index)
    return
  }
  await loadActiveWorkspace()
}

async function addWorkspace() {
  if (workspaces.value.length >= 8) {
    loadError.value = 'A maximum of eight workspaces can be open at once.'
    return
  }
  const source = activeWorkspace.value ?? workspaces.value[0]
  const workspace: WorkspaceDefinition = {
    id: createId(),
    name: `Workspace ${workspaces.value.length + 1}`,
    brokerId: source?.brokerId ?? 'simulator',
    symbol: source?.symbol ?? 'EURUSD',
    interval: source?.interval ?? '5m',
  }
  normalizeWorkspace(workspace)
  workspaces.value.push(workspace)
  await activateWorkspace(workspace.id)
}

async function removeWorkspace(id: string) {
  if (workspaces.value.length === 1) return
  const index = workspaces.value.findIndex((workspace) => workspace.id === id)
  if (index < 0) return
  const wasActive = activeWorkspaceId.value === id
  workspaces.value.splice(index, 1)
  if (wasActive) await activateWorkspace(workspaces.value[Math.max(0, index - 1)].id)
}

function selectSeries(index: number) {
  activeSeriesIndex.value = index
  selectedIndex.value = Math.max(0, (dataset.value?.series[index].frames.length ?? 1) - 1)
  isPlaying.value = false
}

function togglePlayback() {
  if (!activeSeries.value) return
  if (selectedIndex.value >= activeSeries.value.frames.length - 1) selectedIndex.value = 0
  isPlaying.value = !isPlaying.value
}

function stepForward() {
  const series = activeSeries.value
  if (!series) return
  if (selectedIndex.value >= series.frames.length - 1) {
    isPlaying.value = false
    return
  }
  selectedIndex.value++
}

function stepBackward() {
  isPlaying.value = false
  selectedIndex.value = Math.max(0, selectedIndex.value - 1)
}

function jumpToEdge(edge: 'start' | 'end') {
  isPlaying.value = false
  selectedIndex.value = edge === 'start' ? 0 : Math.max(0, (activeSeries.value?.frames.length ?? 1) - 1)
}

function stopPlaybackTimer() {
  if (playbackTimer !== undefined) window.clearInterval(playbackTimer)
  playbackTimer = undefined
}

function handleKeyboard(event: KeyboardEvent) {
  if (event.target instanceof HTMLInputElement || event.target instanceof HTMLSelectElement ||
    event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLButtonElement) return
  if (event.code === 'Space') {
    event.preventDefault()
    togglePlayback()
  } else if (event.code === 'ArrowRight') stepForward()
  else if (event.code === 'ArrowLeft') stepBackward()
}

async function importReplay(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  try {
    replayDataset.value = validateDataset(JSON.parse(await file.text()))
    syncReplayAsset(replayDataset.value)
    let workspace = workspaces.value.find((item) => item.brokerId === 'simulator')
    if (!workspace) {
      workspace = { id: createId(), name: 'Imported Replay', brokerId: 'simulator', symbol: '', interval: replayDataset.value.series[0].interval }
      workspaces.value.push(workspace)
    }
    normalizeWorkspace(workspace)
    if (!replayDataset.value.series.some((series) => series.interval === workspace.interval)) {
      workspace.interval = replayDataset.value.series[0].interval
    }
    await activateWorkspace(workspace.id)
  } catch (error) {
    loadError.value = messageFrom(error, 'The selected file is invalid.')
  } finally {
    input.value = ''
  }
}

function toggleLayer(layer: keyof ChartLayers) {
  layers[layer] = !layers[layer]
}

async function connectLive(eventsPath = 'api/live/events', allowOneShot = true) {
  const generation = sourceGeneration
  dataset.value = null
  loading.value = true
  loadError.value = null
  if (allowOneShot && new URLSearchParams(window.location.search).get('once') === '1') {
    try {
      const response = await fetch(`${import.meta.env.BASE_URL}api/live/snapshot`, {
        signal: sourceAbortController?.signal,
      })
      if (!response.ok) throw new Error(`Live snapshot failed with HTTP ${response.status}.`)
      const payload = await response.json() as LiveReplayPayload
      if (generation === sourceGeneration) applyLiveSnapshot(payload)
    } catch (error) {
      if (generation !== sourceGeneration) return
      if (isAbortError(error)) return
      loadError.value = messageFrom(error, 'The live snapshot failed.')
      loading.value = false
    }
    return
  }

  liveEvents = new EventSource(`${import.meta.env.BASE_URL}${eventsPath}`)
  liveEvents.addEventListener('snapshot', (event) => {
    if (generation !== sourceGeneration) return
    try {
      applyLiveSnapshot(JSON.parse((event as MessageEvent<string>).data) as LiveReplayPayload)
    } catch (error) {
      loadError.value = messageFrom(error, 'The live update was invalid.')
      loading.value = false
    }
  })
  liveEvents.addEventListener('update', (event) => {
    if (generation !== sourceGeneration) return
    try {
      const update = JSON.parse((event as MessageEvent<string>).data) as LiveReplayUpdate
      liveStatus.value = update.status
      const currentDataset = dataset.value
      const currentSeries = currentDataset?.series[0]
      if (currentDataset && currentSeries && update.frames.length > 0) {
        const newestKnownIndex = currentSeries.frames.at(-1)?.index ?? -1
        const additions = update.frames.filter((frame) => frame.index > newestKnownIndex)
        if (additions.length > 0) {
          currentSeries.frames.push(...additions)
          const frameCapacity = Math.max(1, currentDataset.parameters.candleCapacity)
          const overflow = currentSeries.frames.length - frameCapacity
          if (overflow > 0) currentSeries.frames.splice(0, overflow)
          currentDataset.generatedAt = currentSeries.frames.at(-1)?.availableAt ?? currentDataset.generatedAt
          selectedIndex.value = Math.max(0, currentSeries.frames.length - 1)
          loading.value = currentSeries.frames.length === 0
        }
      }
      loadError.value = null
    } catch (error) {
      loadError.value = messageFrom(error, 'The live update was invalid.')
    }
  })
  liveEvents.onerror = () => {
    if (generation === sourceGeneration && mode.value === 'live') {
      loadError.value = 'The live dashboard connection was interrupted; the browser is retrying.'
    }
  }
}

async function loadOandaAccount(generation = sourceGeneration) {
  if (activeBroker.value?.id !== 'oanda' || generation !== sourceGeneration) return
  oandaAccountLoading.value = true
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}api/workspaces/oanda/account`, {
      signal: sourceAbortController?.signal,
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
      throw new Error(problem?.error ?? problem?.detail ?? `OANDA account request failed with HTTP ${response.status}.`)
    }
    const account = await response.json() as OandaWorkspaceAccount
    if (generation === sourceGeneration) oandaAccount.value = account
  } catch (error) {
    if (isAbortError(error)) return
    if (generation === sourceGeneration) loadError.value = messageFrom(error, 'OANDA account data could not be loaded.')
  } finally {
    if (generation === sourceGeneration) oandaAccountLoading.value = false
  }
}

function connectOandaOrderEvents(generation: number) {
  orderEvents = new EventSource(`${import.meta.env.BASE_URL}api/workspaces/oanda/order-events`)
  orderEvents.addEventListener('orders', (event) => {
    if (generation !== sourceGeneration) return
    try {
      const batch = JSON.parse((event as MessageEvent<string>).data) as OandaOrderEventBatch
      if (batch.events.length > 0) {
        const latest = batch.events.at(-1)!
        orderNotice.value = `OANDA ${latest.type.toLowerCase()}: ${latest.instrument}`
        void loadOandaAccount(generation)
      }
    } catch (error) {
      loadError.value = messageFrom(error, 'An OANDA order event was invalid.')
    }
  })
}

async function submitOandaOrder() {
  const workspace = activeWorkspace.value
  if (!workspace || activeBroker.value?.id !== 'oanda' || !activeBroker.value.canTrade) return
  if (!ordersArmed.value) {
    loadError.value = 'Arm demo orders before submitting an OANDA practice order.'
    return
  }
  if (!window.confirm(`Submit this ${orderForm.side} ${orderForm.type} order to the OANDA practice account?`)) return

  orderSubmitting.value = true
  orderNotice.value = null
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}api/workspaces/oanda/orders`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-TradingHub-Demo-Confirm': 'OANDA-PRACTICE',
      },
      body: JSON.stringify({
        symbol: workspace.symbol,
        side: orderForm.side,
        type: orderForm.type,
        units: orderForm.units,
        price: optionalNumber(orderForm.price),
        stopLoss: optionalNumber(orderForm.stopLoss),
        takeProfit: optionalNumber(orderForm.takeProfit),
        clientOrderId: `dashboard-${Date.now()}`,
      }),
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
      throw new Error(problem?.error ?? problem?.detail ?? `Order request failed with HTTP ${response.status}.`)
    }
    const submission = await response.json() as OrderSubmission
    orderNotice.value = `Practice order ${submission.status.toLowerCase()} · ${submission.brokerOrderId ?? submission.clientOrderId}`
    ordersArmed.value = false
    await loadOandaAccount()
  } catch (error) {
    loadError.value = messageFrom(error, 'The OANDA practice order was not confirmed.')
  } finally {
    orderSubmitting.value = false
  }
}

async function cancelOandaOrder(orderId: string) {
  if (!ordersArmed.value) {
    loadError.value = 'Arm demo orders before cancelling an OANDA practice order.'
    return
  }
  if (!window.confirm(`Cancel OANDA practice order ${orderId}?`)) return
  orderSubmitting.value = true
  try {
    const response = await fetch(
      `${import.meta.env.BASE_URL}api/workspaces/oanda/orders/${encodeURIComponent(orderId)}`,
      {
        method: 'DELETE',
        headers: { 'X-TradingHub-Demo-Confirm': 'OANDA-PRACTICE' },
      },
    )
    if (!response.ok) {
      const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
      throw new Error(problem?.error ?? problem?.detail ?? `Cancellation failed with HTTP ${response.status}.`)
    }
    orderNotice.value = `Practice order ${orderId} cancelled.`
    ordersArmed.value = false
    await loadOandaAccount()
  } catch (error) {
    loadError.value = messageFrom(error, 'The OANDA cancellation was not confirmed.')
  } finally {
    orderSubmitting.value = false
  }
}

async function refreshMarketSnapshot(generation: number) {
  const workspace = activeWorkspace.value
  if (!workspace || generation !== sourceGeneration) return
  try {
    const query = new URLSearchParams({ symbol: workspace.symbol, interval: workspace.interval })
    const response = await fetch(`${import.meta.env.BASE_URL}api/workspaces/binance/snapshot?${query}`, {
      signal: sourceAbortController?.signal,
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
      throw new Error(problem?.error ?? problem?.detail ?? `Market snapshot failed with HTTP ${response.status}.`)
    }
    if (generation !== sourceGeneration) return
    const payload = await response.json() as WorkspaceSnapshot
    if (generation !== sourceGeneration) return
    liveStatus.value = payload.status
    setDataset(validateDataset(payload.dataset))
    loading.value = false
  } catch (error) {
    if (generation !== sourceGeneration) return
    if (isAbortError(error)) return
    loadError.value = messageFrom(error, 'The workspace market snapshot failed.')
    loading.value = false
  }
}

function scheduleMarketSnapshot(generation: number, interval: string) {
  if (generation !== sourceGeneration || activeBroker.value?.id !== 'binance') return
  marketPollTimer = window.setTimeout(async () => {
    marketPollTimer = undefined
    await refreshMarketSnapshot(generation)
    scheduleMarketSnapshot(generation, interval)
  }, pollMilliseconds(interval))
}

function disconnectDataSource() {
  sourceGeneration++
  sourceAbortController?.abort()
  sourceAbortController = undefined
  liveEvents?.close()
  liveEvents = undefined
  orderEvents?.close()
  orderEvents = undefined
  oandaAccount.value = null
  oandaAccountLoading.value = false
  ordersArmed.value = false
  orderNotice.value = null
  if (marketPollTimer !== undefined) window.clearTimeout(marketPollTimer)
  marketPollTimer = undefined
}

function optionalNumber(value: number | null) {
  return value == null || value === 0 || !Number.isFinite(value) ? null : value
}

function accountAmount(value: number | null | undefined, currency?: string | null) {
  if (value == null) return '—'
  return `${value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}${currency ? ` ${currency}` : ''}`
}

function signedAmount(value: number, currency?: string | null) {
  const prefix = value > 0 ? '+' : ''
  return `${prefix}${value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}${currency ? ` ${currency}` : ''}`
}

function jumpToTrade(trade: ReplayTrade) {
  const value = dataset.value
  if (!value) return
  const detailedIndex = value.series.findIndex((series) => series.interval === '5m')
  const seriesIndex = detailedIndex >= 0 ? detailedIndex : activeSeriesIndex.value
  const series = value.series[seriesIndex]
  if (!series?.frames.length) return
  if (seriesIndex !== activeSeriesIndex.value) selectSeries(seriesIndex)

  const target = new Date(trade.openedAt ?? trade.signalCreatedAt).getTime()
  let bestIndex = 0
  let bestDistance = Number.POSITIVE_INFINITY
  series.frames.forEach((frame, index) => {
    const distance = Math.abs(new Date(frame.availableAt).getTime() - target)
    if (distance < bestDistance) {
      bestDistance = distance
      bestIndex = index
    }
  })
  selectedIndex.value = bestIndex
  isPlaying.value = false
}

function applyLiveSnapshot(payload: LiveReplayPayload) {
  liveStatus.value = payload.status
  setDataset(payload.dataset)
  loading.value = payload.dataset.series[0]?.frames.length === 0
}

function pollMilliseconds(interval: string) {
  const seconds: Record<string, number> = { '1m': 60, '3m': 180, '5m': 300, '15m': 900, '30m': 1_800, '1h': 3_600, '4h': 14_400, '1d': 86_400 }
  return Math.min(15 * 60_000, Math.max(60_000, (seconds[interval] ?? 60) * 1_000))
}

function syncReplayAsset(value: ReplayDataset) {
  const broker = workspaceCatalog.value.brokers.find((item) => item.id === 'simulator')
  if (!broker) return
  const pair = value.instrument.replace(/^[^:]+:/, '')
  const symbol = pair.replace(/[^a-zA-Z0-9]/g, '').toUpperCase() || 'REPLAY'
  broker.assets = [{
    symbol,
    displayName: pair.replace('/', ' / '),
    instrument: value.instrument,
    timeframes: value.series.map((series) => series.interval),
  }]
  workspaces.value
    .filter((workspace) => workspace.brokerId === 'simulator')
    .forEach(normalizeWorkspace)
}

function createId() {
  return globalThis.crypto?.randomUUID?.() ?? `workspace-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function messageFrom(error: unknown, fallback: string) {
  return error instanceof Error ? error.message : fallback
}

function isAbortError(error: unknown) {
  return error instanceof DOMException && error.name === 'AbortError'
}
</script>

<template>
  <div class="app-frame">
    <header class="topbar">
      <div class="brand-lockup">
        <div class="brand-mark" aria-hidden="true">
          <span></span><span></span><span></span>
        </div>
        <div>
          <strong>TradingHub</strong>
          <small>Analysis laboratory</small>
        </div>
      </div>
      <div class="topbar-actions">
        <div class="view-toggle" role="tablist" aria-label="Dashboard views">
          <button
            type="button"
            class="button button-secondary"
            :class="{ active: uiView === 'workspaces' }"
            @click="uiView = 'workspaces'"
          >Workspaces</button>
          <button
            type="button"
            class="button button-secondary"
            :class="{ active: uiView === 'simulator' }"
            @click="uiView = 'simulator'"
          >Simulator</button>
        </div>
        <div class="engine-status">
          <span :class="['status-dot', sourceStateClass]"></span>
          {{ sourceLabel }}
        </div>
        <button class="button button-secondary" type="button" @click="fileInput?.click()">
          Import replay
        </button>
        <input ref="fileInput" type="file" accept="application/json,.json" hidden @change="importReplay" />
      </div>
    </header>

    <SimulatorPanel v-if="uiView === 'simulator'" />

    <section v-if="uiView === 'workspaces' && activeWorkspace" class="workspace-dock" aria-label="Trading workspaces">
      <div class="workspace-tabs" role="tablist" aria-label="Open workspaces">
        <div
          v-for="workspaceItem in workspaces"
          :key="workspaceItem.id"
          :class="['workspace-tab', { active: workspaceItem.id === activeWorkspaceId }]"
        >
          <button
            class="workspace-tab-main"
            type="button"
            role="tab"
            :aria-selected="workspaceItem.id === activeWorkspaceId"
            @click="activateWorkspace(workspaceItem.id)"
          >
            <span>{{ workspaceItem.name }}</span>
            <small>{{ workspaceCatalog.brokers.find((broker) => broker.id === workspaceItem.brokerId)?.displayName }}</small>
          </button>
          <button
            v-if="workspaces.length > 1"
            class="workspace-tab-close"
            type="button"
            aria-label="Close workspace"
            @click="removeWorkspace(workspaceItem.id)"
          >×</button>
        </div>
        <button class="add-workspace" type="button" title="Add workspace" aria-label="Add workspace" @click="addWorkspace">+</button>
      </div>

      <div class="workspace-config">
        <label class="workspace-name-control">
          <span>Workspace</span>
          <input v-model.trim="activeWorkspace.name" type="text" maxlength="32" aria-label="Workspace name" />
        </label>
        <label class="workspace-select-control">
          <span>Broker</span>
          <select :value="activeWorkspace.brokerId" @change="changeBroker">
            <option v-for="broker in workspaceCatalog.brokers" :key="broker.id" :value="broker.id">
              {{ broker.displayName }}{{ broker.isConfigured ? '' : ' · setup required' }}
            </option>
          </select>
        </label>
        <button
          type="button"
          :class="['environment-indicator', activeBroker?.environment.toLowerCase(), { unavailable: !activeBroker?.isConfigured }]"
          aria-disabled="true"
          :title="activeBroker?.description"
        >
          <span></span>{{ environmentLabel }}
        </button>
        <label class="workspace-select-control asset-control">
          <span>Asset pair</span>
          <select
            :value="activeWorkspace.symbol"
            :disabled="!activeBroker?.isConfigured || availableAssets.length === 0"
            @change="changeAsset"
          >
            <option v-if="availableAssets.length === 0" value="">No mapped pairs</option>
            <option v-for="asset in availableAssets" :key="asset.symbol" :value="asset.symbol">
              {{ asset.displayName }} · {{ asset.symbol }}
            </option>
          </select>
        </label>
        <label v-if="activeBroker?.id === 'simulator' && backtestManifest?.runs.length" class="workspace-select-control strategy-control">
          <span>Backtest strategy</span>
          <select :value="selectedBacktestId" :disabled="loading" @change="changeBacktest">
            <option v-for="run in backtestManifest.runs" :key="run.id" :value="run.id">
              {{ run.strategyName }}
            </option>
          </select>
        </label>
        <p class="broker-description">{{ activeBroker?.description }}</p>
        <span v-if="catalogWarning" class="catalog-warning">{{ catalogWarning }}</span>
      </div>
    </section>

    <main v-if="uiView === 'workspaces' && dataset && activeSeries && currentFrame" class="workspace">
      <section class="workspace-heading">
        <div>
          <div class="eyebrow">{{ dataset.source }}</div>
          <div class="title-row">
            <h1>{{ dataset.instrument.replace(/^[^:]+:/, '') }}</h1>
            <span class="market-pill">{{ activeBroker?.displayName }} · {{ activeBroker?.environment }}</span>
            <span :class="['readiness-pill', isReady ? 'ready' : 'warming']">
              {{ isReady ? 'Indicators ready' : 'Warm-up period' }}
            </span>
          </div>
          <p>{{ dataset.title }}</p>
        </div>
        <div class="timeframe-selector" aria-label="Analysis timeframe">
          <button
            v-for="interval in availableTimeframes"
            :key="interval"
            type="button"
            :class="{ active: activeWorkspace?.interval === interval }"
            :disabled="loading"
            @click="changeTimeframe(interval)"
          >{{ interval }}</button>
        </div>
      </section>

      <section v-if="mode === 'replay' && backtestManifest?.runs.length" class="backtest-comparison-strip">
        <button
          v-for="run in backtestManifest.runs"
          :key="run.id"
          type="button"
          :class="['backtest-run-card', { active: run.id === selectedBacktestId }]"
          @click="selectBacktest(run.id)"
        >
          <span>{{ run.strategyName }}</span>
          <strong :class="run.performance.netProfit >= 0 ? 'positive-text' : 'negative-text'">
            {{ signedAmount(run.performance.netProfit, run.performance.currency) }}
          </strong>
          <small>{{ run.performance.tradeCount }} trades · {{ run.performance.winRatePercent.toFixed(1) }}% wins · {{ run.performance.averageR?.toFixed(2) ?? '—' }}R avg</small>
        </button>
      </section>

      <section v-if="activeBroker?.id === 'oanda'" class="oanda-account-strip">
        <div class="oanda-account-identity">
          <span>OANDA {{ activeBroker.environment }} account</span>
          <strong>{{ oandaAccount?.accountId ?? (oandaAccountLoading ? 'Loading…' : 'Unavailable') }}</strong>
        </div>
        <div><span>Balance</span><strong>{{ accountAmount(oandaAccount?.balance, oandaAccount?.currency) }}</strong></div>
        <div><span>Available</span><strong>{{ accountAmount(oandaAccount?.available, oandaAccount?.currency) }}</strong></div>
        <div><span>Unrealized P/L</span><strong :class="(oandaAccount?.unrealizedProfitLoss ?? 0) >= 0 ? 'positive-text' : 'negative-text'">{{ accountAmount(oandaAccount?.unrealizedProfitLoss, oandaAccount?.currency) }}</strong></div>
        <div><span>Positions</span><strong>{{ oandaAccount?.positions.length ?? '—' }}</strong></div>
        <div><span>Pending orders</span><strong>{{ oandaAccount?.pendingOrders.length ?? '—' }}</strong></div>
        <button type="button" :disabled="oandaAccountLoading" @click="loadOandaAccount()">Refresh</button>
      </section>

      <section class="metric-strip">
        <article class="metric-card price-card">
          <div class="metric-label">Close price</div>
          <strong>{{ price(currentFrame.candle.close) }}</strong>
          <span :class="candleMovePips >= 0 ? 'positive-text' : 'negative-text'">
            {{ candleMovementLabel }}
          </span>
        </article>
        <article class="metric-card">
          <div class="metric-label">RSI · {{ dataset.parameters.rsiPeriod }}</div>
          <strong>{{ currentFrame.indicators.rsi?.toFixed(1) ?? '—' }}</strong>
          <span :class="`${rsiState.tone}-text`">{{ rsiContextLabel }}</span>
        </article>
        <article class="metric-card">
          <div class="metric-label">Bollinger position</div>
          <strong>{{ bandPosition == null ? '—' : `${bandPosition.toFixed(0)}%` }}</strong>
          <span>{{ bollingerContextLabel }}</span>
        </article>
        <article class="metric-card">
          <div class="metric-label">ATR · {{ dataset.parameters.atrPeriod }}</div>
          <strong>{{ price(currentFrame.indicators.atr) }}</strong>
          <span>{{ atrContextLabel }}</span>
        </article>
        <article class="metric-card">
          <div class="metric-label">Market structure</div>
          <strong>{{ currentStructure.direction }}</strong>
          <span v-if="currentStructure.break !== 'None'" class="negative-text">{{ currentStructure.break }} break</span>
          <span v-else>{{ currentStructure.strength.toFixed(0) }} strength</span>
        </article>
        <article class="metric-card confidence-card">
          <div class="metric-label">Confidence</div>
          <strong>{{ currentFrame.confidence.total.toFixed(1) }}</strong>
          <span :class="`confidence-${confidenceTone}`">{{ confidenceTone }}</span>
        </article>
      </section>

      <section v-if="mode === 'replay' && replayPerformance" class="backtest-performance-strip">
        <article><span>Strategy</span><strong>{{ selectedBacktestRun?.strategyName ?? dataset.title }}</strong></article>
        <article><span>Net profit</span><strong :class="replayPerformance.netProfit >= 0 ? 'positive-text' : 'negative-text'">{{ signedAmount(replayPerformance.netProfit, replayPerformance.currency) }}</strong></article>
        <article><span>Win rate</span><strong>{{ replayPerformance.winRatePercent.toFixed(1) }}%</strong><small>{{ replayPerformance.winningTrades }}W / {{ replayPerformance.losingTrades }}L</small></article>
        <article><span>Profit factor</span><strong>{{ replayPerformance.profitFactor?.toFixed(2) ?? '—' }}</strong></article>
        <article><span>Average R</span><strong>{{ replayPerformance.averageR?.toFixed(2) ?? '—' }}</strong></article>
        <article><span>Closed-trade drawdown</span><strong>{{ replayPerformance.maximumDrawdown.toLocaleString(undefined, { maximumFractionDigits: 2 }) }} {{ replayPerformance.currency }}</strong></article>
        <article><span>Final equity</span><strong>{{ replayPerformance.finalEquity.toLocaleString(undefined, { maximumFractionDigits: 2 }) }} {{ replayPerformance.currency }}</strong></article>
        <article><span>Peak equity</span><strong>{{ replayPerformance.peakEquity.toLocaleString(undefined, { maximumFractionDigits: 2 }) }} {{ replayPerformance.currency }}</strong></article>
        <article><span>Protection activations</span><strong>{{ replayPerformance.equityProtectionActivationCount }}</strong></article>
      </section>

      <div class="dashboard-grid">
        <section class="main-stage">
          <article class="panel chart-panel-card">
            <header class="panel-header chart-header">
              <div>
                <div class="panel-title">Market structure</div>
                <div class="panel-subtitle">
                  {{ mode === 'live' ? 'Closed-candle state' : 'As known' }} at {{ timestamp(currentFrame.availableAt) }} UTC · frame {{ selectedIndex + 1 }}/{{ activeSeries.frames.length }}
                </div>
              </div>
              <div class="layer-controls" aria-label="Chart layers">
                <button
                  v-for="layer in (Object.keys(layers) as (keyof ChartLayers)[])"
                  :key="layer"
                  type="button"
                  :class="{ active: layers[layer] }"
                  @click="toggleLayer(layer)"
                >
                  <span :class="`layer-dot layer-${layer}`"></span>{{ layer }}
                </button>
              </div>
            </header>

            <AnalysisChart
              :frames="activeSeries.frames"
              :selected-index="selectedIndex"
              :window-size="windowSize"
              :layers="layers"
              :trades="dataset?.trades ?? []"
            />

            <div v-if="mode === 'replay'" class="replay-controls">
              <div class="transport-buttons">
                <button type="button" title="First candle" @click="jumpToEdge('start')">|‹</button>
                <button type="button" title="Previous candle" @click="stepBackward">‹</button>
                <button class="play-button" type="button" @click="togglePlayback">
                  {{ isPlaying ? 'Pause' : 'Play' }}
                </button>
                <button type="button" title="Next candle" @click="stepForward">›</button>
                <button type="button" title="Latest candle" @click="jumpToEdge('end')">›|</button>
              </div>
              <input
                v-model.number="selectedIndex"
                class="timeline"
                type="range"
                min="0"
                :max="activeSeries.frames.length - 1"
                step="1"
                aria-label="Replay position"
              />
              <label class="select-control">
                <span>Speed</span>
                <select v-model.number="playbackSpeed">
                  <option :value="1">1×</option>
                  <option :value="2">2×</option>
                  <option :value="4">4×</option>
                  <option :value="8">8×</option>
                </select>
              </label>
              <label class="select-control">
                <span>Window</span>
                <select v-model.number="windowSize">
                  <option :value="50">50</option>
                  <option :value="100">100</option>
                  <option :value="160">160</option>
                  <option :value="500">500</option>
                  <option :value="0">All</option>
                </select>
              </label>
            </div>
            <div v-else class="live-control-bar">
              <div class="live-now">
                <span :class="['live-pulse', sourceStateClass]"></span>
                <div>
                  <strong>{{ liveStatus?.state ?? 'Starting' }}</strong>
                  <small>{{ liveStatus?.message ?? 'Opening the public market-data stream.' }}</small>
                </div>
              </div>
              <div class="live-facts">
                <span class="live-price">{{ price(currentFrame.candle.close) }}</span>
                <span :class="candleMovePips >= 0 ? 'positive-text' : 'negative-text'">{{ candleMovementLabel }}</span>
                <span>{{ activeWorkspace?.interval ?? activeSeries.interval }}</span>
                <span>Closed candles only</span>
                <span>{{ activeSeries.frames.length }} buffered</span>
                <span v-if="liveStatus?.reconnectAttempt">Retry {{ liveStatus.reconnectAttempt }}</span>
              </div>
              <label class="select-control">
                <span>Window</span>
                <select v-model.number="windowSize">
                  <option :value="50">50</option>
                  <option :value="100">100</option>
                  <option :value="160">160</option>
                  <option :value="500">500</option>
                  <option :value="0">All</option>
                </select>
              </label>
            </div>
          </article>

          <div class="detail-grid">
            <article class="panel detail-panel">
              <header class="panel-header">
                <div>
                  <div class="panel-title">Bollinger anatomy</div>
                  <div class="panel-subtitle">SMA {{ dataset.parameters.bollingerPeriod }} · {{ dataset.parameters.bollingerStandardDeviations }} standard deviations</div>
                </div>
              </header>
              <div class="band-stack">
                <div><span class="band-swatch upper"></span><label>Upper</label><strong>{{ price(currentFrame.indicators.bollingerUpper) }}</strong></div>
                <div><span class="band-swatch middle"></span><label>Middle</label><strong>{{ price(currentFrame.indicators.bollingerMiddle) }}</strong></div>
                <div><span class="band-swatch lower"></span><label>Lower</label><strong>{{ price(currentFrame.indicators.bollingerLower) }}</strong></div>
              </div>
              <div class="band-meter">
                <span>Lower</span>
                <div><i :style="{ left: `${Math.max(0, Math.min(100, bandPosition ?? 0))}%` }"></i></div>
                <span>Upper</span>
              </div>
              <div v-if="currentFrame.indicators.bollingerAnalysis" class="band-stack indicator-context">
                <div><label>Width regime</label><strong>{{ humanizeEnum(currentFrame.indicators.bollingerAnalysis.widthRegime) }}</strong></div>
                <div><label>Width direction</label><strong>{{ humanizeEnum(currentFrame.indicators.bollingerAnalysis.widthDirection) }}</strong></div>
                <div><label>Bandwidth</label><strong>{{ currentFrame.indicators.bollingerAnalysis.bandwidthPercent?.toFixed(3) ?? '—' }}%</strong></div>
                <div><label>Historical percentile</label><strong>{{ currentFrame.indicators.bollingerAnalysis.widthPercentile?.toFixed(1) ?? '—' }}%</strong></div>
              </div>
            </article>

            <article class="panel detail-panel">
              <header class="panel-header">
                <div>
                  <div class="panel-title">New information</div>
                  <div class="panel-subtitle">Confirmed on this candle—never shown early</div>
                </div>
              </header>
              <div v-if="freshSwings.length || newRsiRelationship" class="fresh-events">
                <div v-for="swing in freshSwings" :key="`${swing.pivotTime}-${swing.type}`">
                  <span :class="swing.type === 'High' ? 'event-high' : 'event-low'">{{ swing.type[0] }}</span>
                  <div>
                    <strong>{{ swing.type }} swing · {{ price(swing.price) }}</strong>
                    <small>Pivot {{ timestamp(swing.pivotTime) }} · strength {{ swing.strength }}</small>
                  </div>
                </div>
                <div v-if="newRsiRelationship" :key="newRsiRelationship.confirmedAt">
                  <span :class="newRsiRelationship.type.includes('Bullish') ? 'event-low' : 'event-high'">R</span>
                  <div>
                    <strong>{{ humanizeEnum(newRsiRelationship.type) }}</strong>
                    <small>
                      Price {{ price(newRsiRelationship.firstPrice) }} → {{ price(newRsiRelationship.secondPrice) }} ·
                      RSI {{ newRsiRelationship.firstRsi.toFixed(1) }} → {{ newRsiRelationship.secondRsi.toFixed(1) }} ·
                      strength {{ newRsiRelationship.strength.toFixed(0) }}
                    </small>
                  </div>
                </div>
              </div>
              <div v-else class="empty-state">
                <span>○</span>
                No new swing or RSI relationship was confirmed at this frame.
              </div>
            </article>
          </div>
        </section>

        <aside class="inspector">
          <article class="panel confidence-panel">
            <header class="panel-header">
              <div>
                <div class="panel-title">Annotation confidence</div>
                <div class="panel-subtitle">Explainable score, not a trade signal</div>
              </div>
            </header>
            <div class="confidence-gauge">
              <div
                class="gauge-ring"
                :style="{ '--confidence': `${Math.min(100, currentFrame.confidence.total)}%` }"
              >
                <div><strong>{{ currentFrame.confidence.total.toFixed(0) }}</strong><span>/100</span></div>
              </div>
              <div>
                <strong>{{ currentFrame.confidence.contributions.length }} active rules</strong>
                <span>Structure updates every {{ dataset.parameters.heavyAnalysisEveryCandles }} candles or on a new swing.</span>
              </div>
            </div>
            <div v-if="currentFrame.confidence.contributions.length" class="contribution-list">
              <div v-for="item in currentFrame.confidence.contributions" :key="`${item.rule}-${item.explanation}`">
                <div><strong>{{ item.rule }}</strong><b>+{{ item.score.toFixed(1) }}</b></div>
                <p>{{ item.explanation }}</p>
              </div>
            </div>
            <div v-else class="empty-state compact-empty">No confidence rules are active yet.</div>
          </article>

          <article class="panel annotation-panel">
            <header class="panel-header">
              <div>
                <div class="panel-title">Structure inventory</div>
                <div class="panel-subtitle">Annotations visible at this replay instant</div>
              </div>
            </header>
            <div class="inventory-grid">
              <div><span>Swings</span><strong>{{ currentFrame.swings.length }}</strong></div>
              <div><span>Zones</span><strong>{{ currentFrame.priceZones.length }}</strong></div>
              <div><span>Trendlines</span><strong>{{ currentFrame.trendlines.length }}</strong></div>
              <div><span>Channels</span><strong>{{ currentFrame.channels.length }}</strong></div>
            </div>
            <div class="zone-list">
              <div
                v-for="zone in strongestZones"
                :key="`${zone.type}-${zone.centrePrice}`"
              >
                <span :class="`zone-chip ${zone.type.toLowerCase()}`">{{ zone.type }}</span>
                <strong>{{ price(zone.centrePrice) }}</strong>
                <small>{{ zone.touchCount }} touches · {{ zone.strength.toFixed(0) }} strength</small>
              </div>
              <div v-if="!currentFrame.priceZones.length" class="empty-state compact-empty">No price zones detected.</div>
            </div>
          </article>

          <article class="panel integrity-panel">
            <header class="panel-header">
              <div>
                <div class="panel-title">Data integrity</div>
                <div class="panel-subtitle">Checks that protect agent development</div>
              </div>
            </header>
            <div class="check-list">
              <div :class="integrity.futureSwings === 0 ? 'pass' : 'fail'">
                <span>{{ integrity.futureSwings === 0 ? '✓' : '!' }}</span>
                <div><strong>No future annotations</strong><small>{{ integrity.futureSwings }} invalid confirmations</small></div>
              </div>
              <div :class="integrity.gaps === 0 ? 'pass' : 'fail'">
                <span>{{ integrity.gaps === 0 ? '✓' : '!' }}</span>
                <div><strong>Continuous candles</strong><small>{{ integrity.gaps }} timing gaps</small></div>
              </div>
              <div class="info">
                <span>i</span>
                <div><strong>Warm-up isolated</strong><small>{{ integrity.warmupFrames }} frames marked unavailable</small></div>
              </div>
            </div>
            <div class="timing-table">
              <div><span>Mean analysis</span><strong>{{ duration(timing.average) }}</strong></div>
              <div><span>95th percentile</span><strong>{{ duration(timing.p95) }}</strong></div>
              <div><span>Slowest frame</span><strong>{{ duration(timing.maximum) }}</strong></div>
            </div>
          </article>
        </aside>
      </div>

      <section v-if="mode === 'replay' && replayTrades.length" class="panel backtest-trades-panel">
        <header class="panel-header">
          <div>
            <div class="panel-title">Strategy trade journal</div>
            <div class="panel-subtitle">Click a row to jump to its setup and entry on the chart</div>
          </div>
          <span>{{ replayTrades.length }} recorded trades</span>
        </header>
        <div class="backtest-trades-scroll">
          <table class="backtest-trades-table">
            <thead>
              <tr>
                <th>Setup</th><th>Confirmation</th><th>Signal</th><th>Opened</th><th>Closed</th>
                <th>Side</th><th>Qty</th><th>Entry</th><th>Stop</th><th>Target</th><th>Exit</th><th>Exit reason</th><th>Expected R</th><th>Fees</th><th>Net P/L</th><th>Realised R</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="trade in replayTrades" :key="trade.setupId" tabindex="0" @click="jumpToTrade(trade)" @keydown.enter="jumpToTrade(trade)">
                <td>{{ timestamp(trade.setupStartedAt) }}</td>
                <td>{{ trade.confirmationAt ? timestamp(trade.confirmationAt) : '—' }}</td>
                <td>{{ timestamp(trade.signalCreatedAt) }}</td>
                <td>{{ trade.openedAt ? timestamp(trade.openedAt) : 'Not filled' }}</td>
                <td>{{ trade.closedAt ? timestamp(trade.closedAt) : 'Open' }}</td>
                <td><span :class="['trade-side-chip', trade.side.toLowerCase()]">{{ trade.side }}</span></td>
                <td>{{ trade.quantity.toLocaleString() }}</td>
                <td>{{ price(trade.entryPrice) }}</td>
                <td :title="trade.stopSource ?? undefined">{{ price(trade.stopLossPrice) }}</td>
                <td :title="trade.targetSource ?? undefined">{{ price(trade.takeProfitPrice) }}</td>
                <td>{{ price(trade.exitPrice) }}</td>
                <td :title="trade.exitReasonText ?? trade.exitReason">{{ humanizeEnum(trade.exitReason) }}</td>
                <td>{{ trade.expectedRewardRisk?.toFixed(2) ?? '—' }}</td>
                <td>{{ trade.commission.toLocaleString(undefined, { maximumFractionDigits: 2 }) }}</td>
                <td :class="trade.netProfitLoss >= 0 ? 'positive-text' : 'negative-text'">{{ signedAmount(trade.netProfitLoss, replayPerformance?.currency) }}</td>
                <td>{{ trade.rMultiple?.toFixed(2) ?? '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section v-if="activeBroker?.id === 'oanda'" class="oanda-trading-grid">
        <article class="panel oanda-portfolio-panel">
          <header class="panel-header">
            <div>
              <div class="panel-title">Practice portfolio</div>
              <div class="panel-subtitle">Open positions and pending orders from the connected OANDA account</div>
            </div>
          </header>
          <div class="oanda-position-list">
            <div v-for="position in oandaAccount?.positions ?? []" :key="position.positionId">
              <span :class="position.side.toLowerCase() === 'buy' ? 'position-buy' : 'position-sell'">{{ position.side }}</span>
              <strong>{{ position.nativeInstrument }}</strong>
              <small>{{ position.quantity.toLocaleString() }} units · P/L {{ accountAmount(position.unrealizedProfitLoss, oandaAccount?.currency) }}</small>
            </div>
            <div v-if="!oandaAccount?.positions.length" class="empty-state compact-empty">No open OANDA positions.</div>
          </div>
          <div class="oanda-order-list">
            <div v-for="order in oandaAccount?.pendingOrders ?? []" :key="order.brokerOrderId">
              <div>
                <strong>{{ order.side }} {{ order.nativeInstrument }}</strong>
                <small>{{ order.type }} · {{ order.quantity.toLocaleString() }} units<span v-if="order.price"> · {{ order.price }}</span></small>
              </div>
              <button
                v-if="activeBroker.canTrade"
                type="button"
                :disabled="!ordersArmed || orderSubmitting"
                @click="cancelOandaOrder(order.brokerOrderId)"
              >Cancel</button>
            </div>
            <div v-if="!oandaAccount?.pendingOrders.length" class="empty-state compact-empty">No pending OANDA orders.</div>
          </div>
        </article>

        <article class="panel oanda-order-ticket">
          <header class="panel-header">
            <div>
              <div class="panel-title">OANDA practice order</div>
              <div class="panel-subtitle">Demo only · backend policy and local arming are both required</div>
            </div>
            <span :class="['execution-policy', activeBroker.canTrade ? 'enabled' : 'disabled']">
              {{ activeBroker.canTrade ? 'Backend enabled' : 'Backend disabled' }}
            </span>
          </header>
          <form class="order-ticket-form" @submit.prevent="submitOandaOrder">
            <label><span>Side</span><select v-model="orderForm.side"><option>Buy</option><option>Sell</option></select></label>
            <label><span>Order type</span><select v-model="orderForm.type"><option>Market</option><option>Limit</option><option>Stop</option></select></label>
            <label><span>Units</span><input v-model.number="orderForm.units" type="number" min="1" max="100000000" step="1" required /></label>
            <label v-if="orderForm.type !== 'Market'"><span>Order price</span><input v-model.number="orderForm.price" type="number" min="0" step="any" required /></label>
            <label><span>Stop loss</span><input v-model.number="orderForm.stopLoss" type="number" min="0" step="any" placeholder="Optional" /></label>
            <label><span>Take profit</span><input v-model.number="orderForm.takeProfit" type="number" min="0" step="any" placeholder="Optional" /></label>
            <label class="order-arm-control">
              <input v-model="ordersArmed" type="checkbox" :disabled="!activeBroker.canTrade" />
              <span>I understand this submits to the OANDA practice account.</span>
            </label>
            <button class="button button-primary" type="submit" :disabled="!activeBroker.canTrade || !ordersArmed || orderSubmitting">
              {{ orderSubmitting ? 'Submitting…' : `Submit ${orderForm.side} demo order` }}
            </button>
            <p v-if="orderNotice" class="order-notice">{{ orderNotice }}</p>
            <p v-else-if="!activeBroker.canTrade" class="order-policy-note">Set <code>Oanda__AllowDemoOrders=true</code> on the backend to permit practice orders.</p>
          </form>
        </article>
      </section>
    </main>

    <main v-else-if="uiView === 'workspaces'" class="loading-screen">
      <div class="loading-mark"><span></span><span></span><span></span></div>
      <h1>{{ emptyTitle }}</h1>
      <p>{{ emptyMessage }}</p>
      <div v-if="activeBroker?.id === 'oanda' && !activeBroker.isConfigured" class="oanda-setup-help">
        <strong>Backend configuration required</strong>
        <code>Oanda__Enabled=true</code>
        <code>Oanda__Environment=Demo</code>
        <code>Oanda__AccountId=&lt;practice account ID&gt;</code>
        <code>Oanda__AccessToken=&lt;practice token&gt;</code>
        <small>Set these in the shell that starts DashboardLive, then restart the backend. The token is never sent to the browser.</small>
      </div>
      <button v-if="mode === 'replay' && !loading" class="button button-primary" type="button" @click="fileInput?.click()">Choose replay JSON</button>
    </main>

    <div v-if="loadError && dataset" class="toast" role="alert">
      <span>!</span>{{ loadError }}<button type="button" @click="loadError = null">×</button>
    </div>
  </div>
</template>
