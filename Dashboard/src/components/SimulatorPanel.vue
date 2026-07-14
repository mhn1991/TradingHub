<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import type {
  ChartLayers,
  ImportedDatasetMetadata,
  ReplayFrame,
  ReplayTrade,
  SimulationBrokerCatalog,
  SimulationBrokerOption,
  SimulationInstrumentOption,
  SimulationJobSnapshot,
  StrategyProgressSnapshot,
} from '../types'
import AnalysisChart from './AnalysisChart.vue'
import { useSimulationRealtime } from '../composables/useSimulationRealtime'
import { useSimulationPlayback, type PlaybackRow } from '../composables/useSimulationPlayback'

const form = reactive({
  brokerId: 'oanda',
  instrument: 'FX:GBP/JPY',
  from: '2025-01-01',
  to: '2025-02-01',
  precisionMode: 'Fast',
  sourceKind: 'OandaCandles',
  executionInterval: '1m',
  analysisBaseInterval: '1m',
  analysisIntervals: '5m,15m,30m,1h,2h',
  trendInterval: '2h',
  secondaryTrendIntervals: '1h',
  setupIntervals: '30m',
  confirmationInterval: '15m',
  additionalConfirmationIntervals: '',
  entryInterval: '5m',
  minimumSecondaryTrendAlignments: 0,
  minimumSetupAlignments: 1,
  minimumConfirmationAlignments: 1,
  strongOppositionVeto: true,
  strategies: 'legacy,improved',
  startingBalance: 100000,
  dailyEquityProfitTarget: 0,
  dailyEquityGivebackActivation: 0,
  maximumDailyEquityGiveback: 0,
  quantity: 1000,
  positionSizingMode: 'FixedFractionalRisk',
  fixedCashRisk: 250,
  riskPercentOfEquity: 0.5,
  minimumQuantity: 1,
  maximumQuantity: 0,
  quantityStep: 1,
  maximumAccountMarginUsagePercent: 30,
  maximumSinglePositionMarginPercent: 10,
  leverage: 20,
  commissionRate: 0.00002,
  spreadBasisPoints: 1,
  slippageBasisPoints: 0.5,
  minimumRewardRisk: 1.5,
  priceActionConfirmation: 'Soft',
  minimumPriceActionConfidence: 55,
  rejectStrongOpposingPriceAction: true,
  warmupDays: 45,
  strategyExecutionMode: 'ParallelWorkers',
  ambiguousIntrabarPolicy: 'ConservativeStopFirst',
  refreshCache: false,
  noCache: false,
  legacyPositionManagement: {
    mode: 'StructureAtr', managementInterval: '15m',
    evaluateMechanicalProtectionOnEveryExecutionFrame: true,
    fastStructureInterval: '5m', mainStructureInterval: '15m', thesisInterval: '1h',
    breakEvenActivationR: 1,
    structureTrailActivationR: 1.5, atrBufferMultiplier: 0.25,
    minimumStopImprovementAtr: 0.05, exitOnAdverseStructureBreak: false,
    preserveBracketTarget: false, enableScaleOut: true, minimumRunnerFraction: 0.4,
    enableProfitFloor: true, enableMaximumGiveback: true,
    enableStagnationReduction: true, enableStructuralDeteriorationReduction: true,
    enableMomentumDecayReduction: true, enableVolatilityExhaustionReduction: true,
    enableRiskWindowReduction: false, riskWindowStartUtc: '21:45', riskWindowEndUtc: '22:15',
    enableExecutionCostStressReduction: false,
  },
  improvedPositionManagement: {
    mode: 'StructureAtr', managementInterval: '15m',
    evaluateMechanicalProtectionOnEveryExecutionFrame: true,
    fastStructureInterval: '5m', mainStructureInterval: '15m', thesisInterval: '1h',
    breakEvenActivationR: 1,
    structureTrailActivationR: 2, atrBufferMultiplier: 0.25,
    minimumStopImprovementAtr: 0.05, exitOnAdverseStructureBreak: false,
    preserveBracketTarget: true, enableScaleOut: true, minimumRunnerFraction: 0.5,
    enableProfitFloor: true, enableMaximumGiveback: true,
    enableStagnationReduction: true, enableStructuralDeteriorationReduction: true,
    enableMomentumDecayReduction: true, enableVolatilityExhaustionReduction: true,
    enableRiskWindowReduction: false, riskWindowStartUtc: '21:45', riskWindowEndUtc: '22:15',
    enableExecutionCostStressReduction: false,
  },
})

const brokerCatalog = ref<SimulationBrokerCatalog | null>(null)
const brokerCatalogLoading = ref(false)
const brokerCatalogError = ref<string | null>(null)
const selectedAssetClass = ref('All')
const assetSearch = ref('')

const fallbackBroker: SimulationBrokerOption = {
  id: 'oanda',
  displayName: 'OANDA',
  environment: 'Demo',
  sourceKind: 'OandaCandles',
  isAvailable: true,
  requiresCredentials: true,
  description: 'Loading the broker instrument catalog…',
  supportedExecutionIntervals: ['5s', '10s', '15s', '30s', '1m', '5m', '15m', '1h'],
  instruments: [],
}

const availableBrokers = computed(() => brokerCatalog.value?.brokers ?? [fallbackBroker])
const selectedBroker = computed(() =>
  availableBrokers.value.find((broker) => broker.id === form.brokerId) ?? availableBrokers.value[0] ?? fallbackBroker,
)
const assetClasses = computed(() => [
  'All',
  ...new Set(selectedBroker.value.instruments.map((asset) => asset.assetClass)),
])
const filteredAssets = computed<SimulationInstrumentOption[]>(() => {
  const term = assetSearch.value.trim().toLowerCase()
  return selectedBroker.value.instruments.filter((asset) => {
    const classMatches = selectedAssetClass.value === 'All' || asset.assetClass === selectedAssetClass.value
    const textMatches = !term || [asset.symbol, asset.displayName, asset.instrument, asset.assetClass]
      .some((value) => value.toLowerCase().includes(term))
    return classMatches && textMatches
  })
})
const visibleAssets = computed(() => filteredAssets.value.slice(0, 250))
const precisionOptions = computed(() => {
  switch (selectedBroker.value.id) {
    case 'oanda':
      return [
        { value: 'Fast', label: 'Fast — 1m execution' },
        { value: 'BrokerNativePrecision', label: 'OANDA precision — 5s execution' },
      ]
    case 'binance':
      return [
        { value: 'Fast', label: 'Fast — 1m execution' },
        { value: 'BrokerNativePrecision', label: 'Binance native precision — 1s execution' },
      ]
    case 'imported':
      return [{ value: 'HighPrecision', label: 'Imported precision — 1s or 5s execution' }]
    default:
      return [{ value: 'Fast', label: 'Unavailable' }]
  }
})

const selectedId = ref<string | null>(null)
const importedFile = ref<File | null>(null)
const importedDatasetId = ref<string | null>(null)
const importedDatasetInterval = ref<string | null>(null)
const importedDatasetSummary = ref<string | null>(null)
const importedDatasets = ref<ImportedDatasetMetadata[]>([])
const jobs = ref<SimulationJobSnapshot[]>([])
const trades = ref<Array<{ strategyId: string; trade: ReplayTrade }>>([])
const tradeCursor = ref<string | null>(null)
const tradeKeys = new Set<string>()
const selectedTrade = ref<{ strategyId: string; trade: ReplayTrade } | null>(null)
const executionDetailRows = ref<PlaybackRow[]>([])
const executionDetailStatus = ref<string | null>(null)
const error = ref<string | null>(null)
const busy = ref(false)
const replayRows = ref<PlaybackRow[]>([])
const loadedChunks = ref<Set<string>>(new Set())
const chunkCursor = ref(0)
const maxChartRows = 2_000

const {
  snapshot: liveSnapshot,
  connected,
  usingPolling,
  error: realtimeError,
  completedTradeRevision,
  completedTrade,
} = useSimulationRealtime(selectedId)
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
const flatTrades = computed(() => trades.value.map((payload) => payload.trade))
const managementSummary = computed(() => {
  const legacy = form.legacyPositionManagement
  const improved = form.improvedPositionManagement
  const sizing = form.positionSizingMode === 'FixedFractionalRisk'
    ? `${form.riskPercentOfEquity}% equity risk`
    : form.positionSizingMode === 'FixedCashRisk'
      ? `${form.fixedCashRisk} cash risk`
      : `${form.quantity} fixed units`
  return `MTF: ${form.trendInterval} primary trend · ${form.secondaryTrendIntervals || 'no secondary trend'} secondary · ` +
    `${form.setupIntervals || 'no setup layer'} setup · ${form.confirmationInterval} confirmation · ${form.entryInterval} entry. ` +
    `Sizing: ${sizing}, account margin cap ${form.maximumAccountMarginUsagePercent}%. ` +
    `Legacy: mechanical ${legacy.evaluateMechanicalProtectionOnEveryExecutionFrame ? 'execution-frame' : 'off'} · ` +
    `${legacy.fastStructureInterval}/${legacy.mainStructureInterval}/${legacy.thesisInterval} fast/main/thesis · ` +
    `${legacy.enableScaleOut ? `scale out to ${Math.round(legacy.minimumRunnerFraction * 100)}% runner` : 'no scale-out'}. ` +
    `Improved: mechanical ${improved.evaluateMechanicalProtectionOnEveryExecutionFrame ? 'execution-frame' : 'off'} · ` +
    `${improved.fastStructureInterval}/${improved.mainStructureInterval}/${improved.thesisInterval} fast/main/thesis · ` +
    `${improved.enableScaleOut ? `scale out to ${Math.round(improved.minimumRunnerFraction * 100)}% runner` : 'no scale-out'} · ` +
    `${improved.preserveBracketTarget ? 'preserve target' : 'replace target'}.`
})
const replayFrames = computed<ReplayFrame[]>(() => replayRows.value.map((row, index) => ({
  index,
  availableAt: row.availableAt,
  candle: {
    openTime: row.openTime ?? row.availableAt,
    closeTime: row.availableAt,
    open: row.open,
    high: row.high,
    low: row.low,
    close: row.close,
    volume: row.volume ?? 0,
  },
  indicators: row.analysis?.indicators ?? {
    atr: null,
    rsi: null,
    bollingerMiddle: null,
    bollingerUpper: null,
    bollingerLower: null,
  },
  swings: row.analysis?.swings ?? [],
  priceZones: row.analysis?.priceZones ?? [],
  trendlines: row.analysis?.trendlines ?? [],
  channels: row.analysis?.channels ?? [],
  marketStructure: row.analysis?.marketStructure,
  priceAction: row.analysis?.priceAction,
  confidence: row.analysis?.confidence ?? { total: 0, contributions: [] },
  analysisMicroseconds: 0,
})))
const executionDetailFrames = computed<ReplayFrame[]>(() => executionDetailRows.value.map((row, index) => ({
  index,
  availableAt: row.availableAt,
  candle: {
    openTime: row.openTime ?? row.availableAt,
    closeTime: row.availableAt,
    open: row.open, high: row.high, low: row.low, close: row.close, volume: row.volume ?? 0,
  },
  indicators: row.analysis?.indicators ?? {
    atr: null, rsi: null, bollingerMiddle: null, bollingerUpper: null, bollingerLower: null,
  },
  swings: row.analysis?.swings ?? [],
  priceZones: row.analysis?.priceZones ?? [],
  trendlines: row.analysis?.trendlines ?? [],
  channels: row.analysis?.channels ?? [],
  marketStructure: row.analysis?.marketStructure,
  priceAction: row.analysis?.priceAction,
  confidence: row.analysis?.confidence ?? { total: 0, contributions: [] },
  analysisMicroseconds: 0,
})))
const replayLayers = reactive<ChartLayers>({
  priceAction: true,
  bollinger: false,
  bollingerRegimes: false,
  rsiRelationships: false,
  atr: false,
  volume: true,
  swings: true,
  zones: true,
  trendlines: true,
  channels: true,
})
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

watch(completedTradeRevision, () => {
  const payload = completedTrade.value
  if (payload && payload.simulationId.replaceAll('-', '').toLowerCase() === selectedId.value?.replaceAll('-', '').toLowerCase()) {
    appendTrade(payload.simulationId, payload.strategyId, payload.trade)
  }
  if (selectedId.value) void loadTrades(selectedId.value)
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

async function readApiError(response: Response, fallback: string): Promise<string> {
  const contentType = response.headers.get('content-type') ?? ''
  if (contentType.includes('application/json') || contentType.includes('application/problem+json')) {
    const payload = await response.json().catch(() => null) as {
      error?: string
      title?: string
      detail?: string
      traceId?: string
      code?: string
    } | null
    if (payload) {
      const message = payload.error ?? payload.detail ?? payload.title
      const trace = payload.traceId ? ` Trace ID: ${payload.traceId}` : ''
      const code = payload.code ? ` [${payload.code}]` : ''
      if (message) return `${message}${code}${trace}`
    }
  }
  const text = await response.text().catch(() => '')
  if (text && !text.trimStart().startsWith('<')) return text.trim()
  return `${fallback} (HTTP ${response.status})`
}

async function startSimulation() {
  busy.value = true
  error.value = null
  replayRows.value = []
  loadedChunks.value = new Set()
  chunkCursor.value = 0
  trades.value = []
  tradeCursor.value = null
  tradeKeys.clear()
  try {
    validateBrokerSelection()
    validatePositionManagement()
    if (form.sourceKind === 'ImportedSecondCandles' &&
        importedDatasetInterval.value !== form.executionInterval) {
      importedDatasetId.value = null
    }
    if (form.sourceKind === 'ImportedSecondCandles' && !importedDatasetId.value) {
      if (!importedFile.value) {
        throw new Error('Choose a validated 1-second or 5-second CSV dataset first.')
      }
      await uploadImportedDataset()
    }

    const body = {
      brokerId: form.brokerId,
      instrument: form.instrument,
      from: new Date(`${form.from}T00:00:00Z`).toISOString(),
      to: new Date(`${form.to}T00:00:00Z`).toISOString(),
      precisionMode: form.precisionMode,
      sourceKind: form.sourceKind,
      importedDatasetId: importedDatasetId.value,
      executionInterval: form.executionInterval,
      analysisBaseInterval: form.analysisBaseInterval,
      analysisIntervals: form.analysisIntervals.split(',').map((item) => item.trim()).filter(Boolean),
      trendInterval: form.trendInterval,
      secondaryTrendIntervals: form.secondaryTrendIntervals.split(',').map((item) => item.trim()).filter(Boolean),
      setupIntervals: form.setupIntervals.split(',').map((item) => item.trim()).filter(Boolean),
      confirmationInterval: form.confirmationInterval,
      additionalConfirmationIntervals: form.additionalConfirmationIntervals.split(',').map((item) => item.trim()).filter(Boolean),
      entryInterval: form.entryInterval,
      minimumSecondaryTrendAlignments: form.minimumSecondaryTrendAlignments,
      minimumSetupAlignments: form.minimumSetupAlignments,
      minimumConfirmationAlignments: form.minimumConfirmationAlignments,
      strongOppositionVeto: form.strongOppositionVeto,
      strategies: form.strategies.split(',').map((item) => item.trim()).filter(Boolean),
      startingBalance: form.startingBalance,
      dailyEquityProfitTarget: form.dailyEquityProfitTarget > 0
        ? form.dailyEquityProfitTarget
        : null,
      dailyEquityGivebackActivation: form.dailyEquityGivebackActivation > 0
        ? form.dailyEquityGivebackActivation
        : null,
      maximumDailyEquityGiveback: form.maximumDailyEquityGiveback > 0
        ? form.maximumDailyEquityGiveback
        : null,
      quantity: form.quantity,
      positionSizingMode: form.positionSizingMode,
      fixedCashRisk: form.fixedCashRisk,
      riskPercentOfEquity: form.riskPercentOfEquity,
      minimumQuantity: form.minimumQuantity,
      maximumQuantity: form.maximumQuantity > 0 ? form.maximumQuantity : null,
      quantityStep: form.quantityStep,
      maximumAccountMarginUsagePercent: form.maximumAccountMarginUsagePercent,
      maximumSinglePositionMarginPercent: form.maximumSinglePositionMarginPercent,
      leverage: form.leverage,
      commissionRate: form.commissionRate,
      spreadBasisPoints: form.spreadBasisPoints,
      slippageBasisPoints: form.slippageBasisPoints,
      minimumRewardRisk: form.minimumRewardRisk,
      priceActionConfirmation: form.priceActionConfirmation,
      minimumPriceActionConfidence: form.minimumPriceActionConfidence,
      rejectStrongOpposingPriceAction: form.rejectStrongOpposingPriceAction,
      warmupDays: form.warmupDays,
      strategyExecutionMode: form.strategyExecutionMode,
      ambiguousIntrabarPolicy: form.ambiguousIntrabarPolicy,
      refreshCache: form.refreshCache,
      noCache: form.noCache,
      legacyPositionManagement: form.legacyPositionManagement,
      improvedPositionManagement: form.improvedPositionManagement,
    }
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (!response.ok) {
      throw new Error(await readApiError(response, 'Simulation could not be started'))
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

function selectImportedFile(event: Event) {
  const input = event.target as HTMLInputElement
  importedFile.value = input.files?.[0] ?? null
  importedDatasetId.value = null
  importedDatasetInterval.value = null
  importedDatasetSummary.value = importedFile.value?.name ?? null
}

async function uploadImportedDataset() {
  if (!importedFile.value) return
  const upload = new FormData()
  upload.append('file', importedFile.value)
  upload.append('interval', form.executionInterval)
  const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/imports`, {
    method: 'POST',
    body: upload,
  })
  const result = await response.json().catch(() => null) as {
    datasetId?: string
    rows?: number
    interval?: string
    error?: string
  } | null
  if (!response.ok || !result?.datasetId) {
    throw new Error(result?.error ?? `Dataset import failed (HTTP ${response.status})`)
  }
  importedDatasetId.value = result.datasetId
  importedDatasetInterval.value = result.interval ?? form.executionInterval
  importedDatasetSummary.value = `${importedFile.value.name} · ${result.rows?.toLocaleString() ?? 'unknown'} ${result.interval ?? ''} rows`
  await refreshImportedDatasets()
}

async function refreshImportedDatasets() {
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/imports`)
    if (response.ok) importedDatasets.value = await response.json() as ImportedDatasetMetadata[]
  } catch {
    // Import selection remains optional when the API is offline.
  }
}

function selectImportedDataset() {
  const dataset = importedDatasets.value.find(item => item.datasetId === importedDatasetId.value)
  importedDatasetInterval.value = dataset?.interval ?? null
  if (dataset?.interval === '1s' || dataset?.interval === '5s') {
    form.executionInterval = dataset.interval
  }
  importedDatasetSummary.value = dataset
    ? `${dataset.fileName} · ${dataset.rows.toLocaleString()} ${dataset.interval} rows · expires ${new Date(dataset.expiresAt).toLocaleString()}`
    : null
  importedFile.value = null
}

async function deleteImportedDataset() {
  if (!importedDatasetId.value) return
  const response = await fetch(
    `${import.meta.env.BASE_URL}api/simulations/imports/${importedDatasetId.value}`,
    { method: 'DELETE' },
  )
  if (!response.ok && response.status !== 404) {
    throw new Error(`Dataset delete failed with HTTP ${response.status}`)
  }
  importedDatasetId.value = null
  importedDatasetInterval.value = null
  importedDatasetSummary.value = null
  await refreshImportedDatasets()
}

async function loadSimulationCatalog(refresh = false) {
  brokerCatalogLoading.value = true
  brokerCatalogError.value = null
  try {
    const suffix = refresh ? '?refresh=true' : ''
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/catalog${suffix}`)
    if (!response.ok) throw new Error(await readApiError(response, 'Broker catalog could not be loaded'))
    brokerCatalog.value = await response.json() as SimulationBrokerCatalog
    if (!brokerCatalog.value.brokers.some((broker) => broker.id === form.brokerId && broker.isAvailable)) {
      form.brokerId = brokerCatalog.value.brokers.find((broker) => broker.isAvailable)?.id ?? 'oanda'
    }
    applyBrokerSelection()
  } catch (err) {
    brokerCatalogError.value = err instanceof Error ? err.message : String(err)
  } finally {
    brokerCatalogLoading.value = false
  }
}

function applyBrokerSelection() {
  const broker = selectedBroker.value
  form.sourceKind = broker.sourceKind === 'Unavailable' ? 'OandaCandles' : broker.sourceKind
  selectedAssetClass.value = 'All'
  assetSearch.value = ''

  const allowedModes = precisionOptions.value.map((item) => item.value)
  if (!allowedModes.includes(form.precisionMode)) form.precisionMode = allowedModes[0] ?? 'Fast'
  applyPrecisionDefaults()

  if (broker.id === 'imported') return
  const preferred = broker.instruments.find((asset) =>
    asset.instrument === (broker.id === 'binance' ? 'CRYPTO:BTC/USDT' : 'FX:GBP/JPY'))
  const selected = broker.instruments.find((asset) => asset.instrument === form.instrument)
  form.instrument = (selected ?? preferred ?? broker.instruments[0])?.instrument ?? ''
}

function applyPrecisionDefaults() {
  const brokerId = selectedBroker.value.id
  form.analysisBaseInterval = '1m'
  if (brokerId === 'imported') {
    form.precisionMode = 'HighPrecision'
    if (form.executionInterval !== '1s' && form.executionInterval !== '5s') form.executionInterval = '1s'
    return
  }
  if (form.precisionMode === 'Fast') {
    form.executionInterval = '1m'
    return
  }
  form.executionInterval = brokerId === 'binance' ? '1s' : '5s'
}

function selectFirstFilteredAsset() {
  if (selectedBroker.value.id === 'imported') return
  if (!filteredAssets.value.some((asset) => asset.instrument === form.instrument)) {
    form.instrument = filteredAssets.value[0]?.instrument ?? ''
  }
}

function validateBrokerSelection() {
  const broker = selectedBroker.value
  if (!broker.isAvailable) throw new Error(broker.description)
  if (!form.instrument.trim()) throw new Error('Choose an instrument before starting the simulation.')
  if (broker.id !== 'imported' && !broker.instruments.some((asset) => asset.instrument === form.instrument)) {
    throw new Error(`Choose an instrument from the ${broker.displayName} catalog.`)
  }
  if (!broker.supportedExecutionIntervals.includes(form.executionInterval)) {
    throw new Error(`${broker.displayName} does not support ${form.executionInterval} historical execution candles.`)
  }
}

function parseIntervalSeconds(value: string, name: string): number {
  const match = value.trim().match(/^([1-9][0-9]*)(s|m|h|d|w|mo)$/i)
  if (!match) throw new Error(`${name} must use a supported interval such as 5m, 1h, or 2h.`)
  const amount = Number(match[1])
  const multiplier: Record<string, number> = {
    s: 1, m: 60, h: 3_600, d: 86_400, w: 604_800, mo: 2_592_000,
  }
  return amount * multiplier[match[2]!.toLowerCase()]!
}

function splitIntervals(value: string): string[] {
  return value.split(',').map((item) => item.trim()).filter(Boolean)
}

function validatePositionManagement() {
  const secondary = splitIntervals(form.secondaryTrendIntervals)
  const setup = splitIntervals(form.setupIntervals)
  const additionalConfirmation = splitIntervals(form.additionalConfirmationIntervals)
  const roleIntervals = [
    form.trendInterval,
    ...secondary,
    ...setup,
    form.confirmationInterval,
    ...additionalConfirmation,
    form.entryInterval,
  ]
  roleIntervals.forEach((interval, index) => parseIntervalSeconds(interval, `Strategy interval ${index + 1}`))
  if (new Set(roleIntervals.map((item) => item.toLowerCase())).size !== roleIntervals.length) {
    throw new Error('Each multi-timeframe role must use a unique interval.')
  }
  const trendSeconds = parseIntervalSeconds(form.trendInterval, 'Primary trend interval')
  const confirmationSeconds = parseIntervalSeconds(form.confirmationInterval, 'Primary confirmation interval')
  const entrySeconds = parseIntervalSeconds(form.entryInterval, 'Entry interval')
  if (!(entrySeconds < confirmationSeconds && confirmationSeconds < trendSeconds)) {
    throw new Error('Intervals must be ordered entry < primary confirmation < primary trend.')
  }
  for (const interval of [...secondary, ...setup]) {
    const seconds = parseIntervalSeconds(interval, 'Secondary/setup interval')
    if (!(confirmationSeconds < seconds && seconds < trendSeconds)) {
      throw new Error('Secondary trend and setup intervals must be between confirmation and trend.')
    }
  }
  for (const interval of additionalConfirmation) {
    const seconds = parseIntervalSeconds(interval, 'Additional confirmation interval')
    if (!(entrySeconds < seconds && seconds < trendSeconds)) {
      throw new Error('Additional confirmations must be between the entry and trend intervals.')
    }
  }
  if (!Number.isInteger(form.minimumSecondaryTrendAlignments) ||
      form.minimumSecondaryTrendAlignments < 0 ||
      form.minimumSecondaryTrendAlignments > secondary.length) {
    throw new Error('Minimum secondary alignments cannot exceed the configured secondary intervals.')
  }
  if (!Number.isInteger(form.minimumSetupAlignments) ||
      form.minimumSetupAlignments < 0 || form.minimumSetupAlignments > setup.length) {
    throw new Error('Minimum setup alignments cannot exceed the configured setup intervals.')
  }
  if (!Number.isInteger(form.minimumConfirmationAlignments) ||
      form.minimumConfirmationAlignments < 1 ||
      form.minimumConfirmationAlignments > 1 + additionalConfirmation.length) {
    throw new Error('Minimum confirmation alignments cannot exceed the configured confirmation intervals.')
  }

  if (form.startingBalance <= 0 || form.leverage <= 0 || form.quantity <= 0) {
    throw new Error('Starting balance, leverage and fallback quantity must be greater than zero.')
  }
  if (form.fixedCashRisk <= 0 || form.riskPercentOfEquity <= 0 || form.riskPercentOfEquity > 100) {
    throw new Error('Cash risk must be positive and percentage risk must be between 0 and 100.')
  }
  if (form.minimumQuantity <= 0 || form.quantityStep <= 0 ||
      form.maximumQuantity < 0 ||
      (form.maximumQuantity > 0 && form.maximumQuantity < form.minimumQuantity)) {
    throw new Error('Quantity limits and step are invalid.')
  }
  if (form.maximumAccountMarginUsagePercent <= 0 || form.maximumAccountMarginUsagePercent > 100 ||
      form.maximumSinglePositionMarginPercent <= 0 || form.maximumSinglePositionMarginPercent > 100 ||
      form.maximumSinglePositionMarginPercent > form.maximumAccountMarginUsagePercent) {
    throw new Error('Margin caps must be within 0-100, and the one-position cap cannot exceed the account cap.')
  }

  if (!Number.isFinite(form.minimumPriceActionConfidence) ||
      form.minimumPriceActionConfidence < 0 ||
      form.minimumPriceActionConfidence > 100) {
    throw new Error('Minimum price-action confidence must be between 0 and 100.')
  }

  for (const [name, options] of [
    ['Legacy', form.legacyPositionManagement],
    ['Improved', form.improvedPositionManagement],
  ] as const) {
    if (options.breakEvenActivationR <= 0) throw new Error(`${name} break-even activation must be greater than zero.`)
    if (options.structureTrailActivationR < options.breakEvenActivationR) {
      throw new Error(`${name} structure activation must be at or above break-even activation.`)
    }
    if (options.atrBufferMultiplier < 0 || options.minimumStopImprovementAtr < 0) {
      throw new Error(`${name} ATR values cannot be negative.`)
    }
    const fast = parseIntervalSeconds(options.fastStructureInterval, `${name} fast structure interval`)
    const main = parseIntervalSeconds(options.mainStructureInterval, `${name} main structure interval`)
    const thesis = parseIntervalSeconds(options.thesisInterval, `${name} thesis interval`)
    if (!(fast <= main && main <= thesis)) {
      throw new Error(`${name} management intervals must be ordered fast ≤ main ≤ thesis.`)
    }
  }

  const givebackActivation = Number(form.dailyEquityGivebackActivation)
  const maximumGiveback = Number(form.maximumDailyEquityGiveback)
  const dailyTarget = Number(form.dailyEquityProfitTarget)
  if (dailyTarget < 0 || givebackActivation < 0 || maximumGiveback < 0) {
    throw new Error('Daily account-profit protection values cannot be negative.')
  }
  const hasGivebackActivation = givebackActivation > 0
  const hasMaximumGiveback = maximumGiveback > 0
  if (hasGivebackActivation !== hasMaximumGiveback) {
    throw new Error('Daily giveback activation and maximum giveback must both be set, or both be 0.')
  }
  if (hasGivebackActivation && maximumGiveback > givebackActivation) {
    throw new Error('Maximum daily giveback cannot exceed the activation profit.')
  }
}

function startBackgroundPollers(id: string) {
  if (tradePoll !== undefined) window.clearInterval(tradePoll)
  if (chunkPoll !== undefined) window.clearInterval(chunkPoll)
  tradePoll = window.setInterval(() => { void loadTrades(id) }, 5000)
  chunkPoll = window.setInterval(() => { void loadProgressiveReplay(id) }, 1500)
}

async function control(action: 'pause' | 'resume' | 'cancel') {
  if (!job.value) return
  busy.value = true
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${job.value.id}/${action}`, {
      method: 'POST',
    })
    if (!response.ok) throw new Error(await readApiError(response, `${action} failed`))
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

async function loadTrades(id: string) {
  const cursor = tradeCursor.value ? `&cursor=${encodeURIComponent(tradeCursor.value)}` : ''
  const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}/trades?limit=250${cursor}`)
  if (!response.ok) return
  const payload = await response.json() as {
    items: Array<{ strategyId: string; trade: ReplayTrade }>
    nextCursor: string
    hasMore: boolean
  }
  for (const item of payload.items) appendTrade(id, item.strategyId, item.trade)
  tradeCursor.value = payload.nextCursor
  if (payload.hasMore) await loadTrades(id)
}

function appendTrade(simulationId: string, strategyId: string, trade: ReplayTrade) {
  const key = `${simulationId.replaceAll('-', '').toLowerCase()}|${strategyId}|${trade.setupId}|${trade.closedAt ?? ''}`
  if (tradeKeys.has(key)) return
  tradeKeys.add(key)
  trades.value = [...trades.value, { strategyId, trade }]
}

async function loadExecutionDetail(item: { strategyId: string; trade: ReplayTrade }) {
  if (!selectedId.value) return
  selectedTrade.value = item
  executionDetailRows.value = []
  executionDetailStatus.value = 'Loading execution-detail index…'
  const query = `strategy=${encodeURIComponent(item.strategyId)}&setupId=${encodeURIComponent(item.trade.setupId)}`
  const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${selectedId.value}/replay/execution-detail?${query}`)
  if (!response.ok) {
    executionDetailStatus.value = 'Execution detail is not available for this trade.'
    return
  }
  const index = await response.json() as { chunks?: Array<{ chunkId: string }> }
  for (const chunk of index.chunks ?? []) {
    const chunkResponse = await fetch(
      `${import.meta.env.BASE_URL}api/simulations/${selectedId.value}/replay/execution-detail/${chunk.chunkId}?${query}`,
    )
    if (!chunkResponse.ok) continue
    const rows = await chunkResponse.json() as PlaybackRow[]
    executionDetailRows.value.push(...rows)
  }
  executionDetailStatus.value = `${executionDetailRows.value.length.toLocaleString()} execution frames loaded for ${item.trade.setupId}.`
}

async function loadProgressiveReplay(id: string) {
  if (selectedId.value !== id) return
  // Prefer chunk list + incremental fetch; fall back to bounded range API.
  // Composite identity: simulationId + chunkId prevents cross-job pollution.
  const chunksResponse = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}/replay/chunks`)
  if (chunksResponse.ok) {
    const chunks = await chunksResponse.json() as Array<{ chunkId: string }>
    // A newly selected long-running job only needs the current bounded tail.
    // Already loaded live chunks remain in the rolling replay window.
    for (const chunk of chunks.slice(-3)) {
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
  tradeCursor.value = null
  tradeKeys.clear()
  selectedTrade.value = null
  executionDetailRows.value = []
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

function performanceMetric(
  strategy: StrategyProgressSnapshot,
  key: keyof NonNullable<StrategyProgressSnapshot['performance']>,
) {
  const value = strategy.performance?.[key]
  return typeof value === 'number'
    ? value.toLocaleString(undefined, { maximumFractionDigits: 2 })
    : '—'
}

function togglePlayback() {
  if (playbackPaused.value) play()
  else pause()
}

watch(() => form.brokerId, applyBrokerSelection)
watch(() => form.precisionMode, applyPrecisionDefaults)
watch(selectedAssetClass, selectFirstFilteredAsset)
watch(assetSearch, selectFirstFilteredAsset)

onMounted(() => {
  void loadSimulationCatalog()
  void refreshJobList()
  void refreshImportedDatasets()
})
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
        <fieldset class="broker-picker">
          <legend>Broker and asset</legend>
          <div class="row">
            <label>
              Historical broker
              <select v-model="form.brokerId" :disabled="brokerCatalogLoading">
                <option
                  v-for="broker in availableBrokers"
                  :key="broker.id"
                  :value="broker.id"
                  :disabled="!broker.isAvailable"
                >
                  {{ broker.displayName }}{{ broker.isAvailable ? '' : ' — unavailable' }}
                </option>
              </select>
            </label>
            <label>
              Environment
              <input :value="selectedBroker.environment" readonly />
            </label>
          </div>
          <p class="muted broker-description">{{ selectedBroker.description }}</p>
          <p v-if="brokerCatalog?.warning" class="warning">{{ brokerCatalog.warning }}</p>
          <p v-if="brokerCatalogError" class="error">{{ brokerCatalogError }}</p>
          <button type="button" class="secondary compact-button" :disabled="brokerCatalogLoading" @click="loadSimulationCatalog(true)">
            {{ brokerCatalogLoading ? 'Refreshing assets…' : 'Refresh broker assets' }}
          </button>

          <template v-if="selectedBroker.id !== 'imported'">
            <div class="row">
              <label>
                Asset class
                <select v-model="selectedAssetClass">
                  <option v-for="assetClass in assetClasses" :key="assetClass" :value="assetClass">
                    {{ assetClass }}
                  </option>
                </select>
              </label>
              <label>
                Search assets
                <input v-model="assetSearch" placeholder="GBP/JPY, gold, BTC…" />
              </label>
            </div>
            <label>
              Instrument
              <select v-model="form.instrument" :disabled="!visibleAssets.length">
                <option v-for="asset in visibleAssets" :key="asset.instrument" :value="asset.instrument">
                  {{ asset.displayName }} · {{ asset.instrument }}
                </option>
              </select>
              <small class="muted">
                Showing {{ visibleAssets.length.toLocaleString() }} of {{ filteredAssets.length.toLocaleString() }} matching assets.
                Narrow the search when the broker has more than 250 matches.
              </small>
            </label>
          </template>
          <label v-else>
            Dataset instrument
            <input v-model="form.instrument" placeholder="FX:GBP/JPY or CRYPTO:BTC/USDT" />
          </label>
        </fieldset>
        <div class="row">
          <label>From <input v-model="form.from" type="date" /></label>
          <label>To <input v-model="form.to" type="date" /></label>
        </div>
        <label>
          Precision mode
          <select v-model="form.precisionMode">
            <option v-for="option in precisionOptions" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
        </label>
        <p class="muted">
          {{ selectedBroker.displayName }} supports execution intervals: {{ selectedBroker.supportedExecutionIntervals.join(', ') || 'none' }}.
          Analysis still begins at 1m unless explicitly changed. No smaller candles are invented from larger OHLC bars.
        </p>
        <label v-if="form.sourceKind === 'ImportedSecondCandles'">
          Server-imported candle CSV
          <input type="file" accept=".csv,text/csv" @change="selectImportedFile" />
          <small class="muted">{{ importedDatasetSummary ?? 'timestamp,open,high,low,close[,volume]' }}</small>
        </label>
        <div v-if="form.sourceKind === 'ImportedSecondCandles'" class="row dataset-row">
          <label>
            Previously imported
            <select v-model="importedDatasetId" @change="selectImportedDataset">
              <option :value="null">Upload a new dataset</option>
              <option v-for="dataset in importedDatasets" :key="dataset.datasetId" :value="dataset.datasetId">
                {{ dataset.fileName }} · {{ dataset.interval }} · {{ dataset.rows.toLocaleString() }} rows
              </option>
            </select>
          </label>
          <button type="button" class="secondary compact-button" :disabled="!importedDatasetId" @click="deleteImportedDataset">
            Delete dataset
          </button>
        </div>
        <div class="row">
          <label>Execution interval <input v-model="form.executionInterval" /></label>
          <label>Analysis base <input v-model="form.analysisBaseInterval" /></label>
        </div>
        <label>Analysis intervals <input v-model="form.analysisIntervals" /></label>
        <fieldset class="management-config">
          <legend>Role-based multi-timeframe confirmation</legend>
          <div class="row">
            <label>Primary trend <input v-model="form.trendInterval" /></label>
            <label>Secondary trend intervals <input v-model="form.secondaryTrendIntervals" placeholder="1h" /></label>
          </div>
          <div class="row">
            <label>Setup intervals <input v-model="form.setupIntervals" placeholder="30m" /></label>
            <label>Primary confirmation <input v-model="form.confirmationInterval" /></label>
          </div>
          <div class="row">
            <label>Additional confirmations <input v-model="form.additionalConfirmationIntervals" placeholder="optional, comma separated" /></label>
            <label>Entry trigger <input v-model="form.entryInterval" /></label>
          </div>
          <div class="row">
            <label>Minimum secondary alignments
              <input v-model.number="form.minimumSecondaryTrendAlignments" type="number" min="0" step="1" />
            </label>
            <label>Minimum setup alignments
              <input v-model.number="form.minimumSetupAlignments" type="number" min="0" step="1" />
            </label>
          </div>
          <div class="row">
            <label>Minimum confirmation alignments
              <input v-model.number="form.minimumConfirmationAlignments" type="number" min="1" step="1" />
            </label>
            <label class="inline-check"><input v-model="form.strongOppositionVeto" type="checkbox" /> Strong opposing structure veto</label>
          </div>
          <small class="muted">The primary trend is the hard directional gate. Secondary trend is soft context, setup intervals locate the opportunity, confirmations validate it, and the entry chart supplies the trigger.</small>
        </fieldset>
        <label>Strategies <input v-model="form.strategies" /></label>
        <fieldset class="management-config">
          <legend>Price-action confirmation</legend>
          <div class="row">
            <label>Mode
              <select v-model="form.priceActionConfirmation">
                <option>Disabled</option>
                <option>Soft</option>
                <option>Required</option>
              </select>
            </label>
            <label>Minimum confidence
              <input v-model.number="form.minimumPriceActionConfidence" type="number" min="0" max="100" step="1" />
            </label>
          </div>
          <label class="inline-check">
            <input v-model="form.rejectStrongOpposingPriceAction" type="checkbox" />
            Reject strong opposing price action
          </label>
          <small class="muted">Soft mode scores BOS/CHoCH, retests, rejection, displacement, sweeps and compression breakouts without making every pattern mandatory.</small>
        </fieldset>
        <fieldset class="management-config">
          <legend>Legacy management</legend>
          <div class="row">
            <label>Trailing mode
              <select v-model="form.legacyPositionManagement.mode">
                <option>Disabled</option><option>BreakEvenOnly</option><option>StructureAtr</option>
              </select>
            </label>
            <label class="inline-check"><input v-model="form.legacyPositionManagement.evaluateMechanicalProtectionOnEveryExecutionFrame" type="checkbox" /> Mechanical protection every execution candle</label>
          </div>
          <div class="row">
            <label>Fast structure <input v-model="form.legacyPositionManagement.fastStructureInterval" /></label>
            <label>Main structure <input v-model="form.legacyPositionManagement.mainStructureInterval" /></label>
            <label>Thesis / runner <input v-model="form.legacyPositionManagement.thesisInterval" /></label>
          </div>
          <div class="row">
            <label>Break-even R <input v-model.number="form.legacyPositionManagement.breakEvenActivationR" type="number" min="0.1" step="0.1" /></label>
            <label>Structure R <input v-model.number="form.legacyPositionManagement.structureTrailActivationR" type="number" min="0.1" step="0.1" /></label>
          </div>
          <div class="row">
            <label>ATR buffer <input v-model.number="form.legacyPositionManagement.atrBufferMultiplier" type="number" min="0" step="0.05" /></label>
            <label>Min improvement ATR <input v-model.number="form.legacyPositionManagement.minimumStopImprovementAtr" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label class="inline-check"><input v-model="form.legacyPositionManagement.enableScaleOut" type="checkbox" /> Scale out in stages</label>
            <label>Minimum runner
              <input v-model.number="form.legacyPositionManagement.minimumRunnerFraction" type="number" min="0.1" max="0.9" step="0.05" />
            </label>
          </div>
          <div class="row checks">
            <label><input v-model="form.legacyPositionManagement.enableProfitFloor" type="checkbox" /> Profit-floor ratchet</label>
            <label><input v-model="form.legacyPositionManagement.enableMaximumGiveback" type="checkbox" /> MFE giveback lock</label>
          </div>
          <div class="row checks">
            <label><input v-model="form.legacyPositionManagement.enableStagnationReduction" type="checkbox" /> Stagnation reduction</label>
            <label><input v-model="form.legacyPositionManagement.enableStructuralDeteriorationReduction" type="checkbox" /> Structure reduction</label>
          </div>
          <div class="row checks">
            <label><input v-model="form.legacyPositionManagement.enableMomentumDecayReduction" type="checkbox" /> Momentum-decay reduction</label>
            <label><input v-model="form.legacyPositionManagement.enableVolatilityExhaustionReduction" type="checkbox" /> Volatility-exhaustion reduction</label>
          </div>
          <div class="row checks">
            <label><input v-model="form.legacyPositionManagement.enableRiskWindowReduction" type="checkbox" /> Configured session-risk reduction</label>
            <label><input v-model="form.legacyPositionManagement.enableExecutionCostStressReduction" type="checkbox" /> Spread/ATR stress reduction</label>
          </div>
          <div v-if="form.legacyPositionManagement.enableRiskWindowReduction" class="row">
            <label>Risk start UTC <input v-model="form.legacyPositionManagement.riskWindowStartUtc" type="time" /></label>
            <label>Risk end UTC <input v-model="form.legacyPositionManagement.riskWindowEndUtc" type="time" /></label>
          </div>
          <label class="inline-check"><input v-model="form.legacyPositionManagement.exitOnAdverseStructureBreak" type="checkbox" /> Exit on adverse structure</label>
          <small class="muted">Legacy scales out at configured R/structure opportunities, protects a runner with profit floors and MFE giveback, and does not require a fixed target.</small>
        </fieldset>
        <fieldset class="management-config">
          <legend>Improved management</legend>
          <div class="row">
            <label>Trailing mode
              <select v-model="form.improvedPositionManagement.mode">
                <option>Disabled</option><option>BreakEvenOnly</option><option>StructureAtr</option>
              </select>
            </label>
            <label class="inline-check"><input v-model="form.improvedPositionManagement.evaluateMechanicalProtectionOnEveryExecutionFrame" type="checkbox" /> Mechanical protection every execution candle</label>
          </div>
          <div class="row">
            <label>Fast structure <input v-model="form.improvedPositionManagement.fastStructureInterval" /></label>
            <label>Main structure <input v-model="form.improvedPositionManagement.mainStructureInterval" /></label>
            <label>Thesis / runner <input v-model="form.improvedPositionManagement.thesisInterval" /></label>
          </div>
          <div class="row">
            <label>Break-even R <input v-model.number="form.improvedPositionManagement.breakEvenActivationR" type="number" min="0.1" step="0.1" /></label>
            <label>Structure R <input v-model.number="form.improvedPositionManagement.structureTrailActivationR" type="number" min="0.1" step="0.1" /></label>
          </div>
          <div class="row">
            <label>ATR buffer <input v-model.number="form.improvedPositionManagement.atrBufferMultiplier" type="number" min="0" step="0.05" /></label>
            <label>Min improvement ATR <input v-model.number="form.improvedPositionManagement.minimumStopImprovementAtr" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label class="inline-check"><input v-model="form.improvedPositionManagement.enableScaleOut" type="checkbox" /> Scale out in stages</label>
            <label>Minimum runner
              <input v-model.number="form.improvedPositionManagement.minimumRunnerFraction" type="number" min="0.1" max="0.9" step="0.05" />
            </label>
          </div>
          <div class="row checks">
            <label><input v-model="form.improvedPositionManagement.enableProfitFloor" type="checkbox" /> Profit-floor ratchet</label>
            <label><input v-model="form.improvedPositionManagement.enableMaximumGiveback" type="checkbox" /> MFE giveback lock</label>
          </div>
          <div class="row checks">
            <label><input v-model="form.improvedPositionManagement.enableStagnationReduction" type="checkbox" /> Stagnation reduction</label>
            <label><input v-model="form.improvedPositionManagement.enableStructuralDeteriorationReduction" type="checkbox" /> Structure reduction</label>
          </div>
          <div class="row checks">
            <label><input v-model="form.improvedPositionManagement.enableMomentumDecayReduction" type="checkbox" /> Momentum-decay reduction</label>
            <label><input v-model="form.improvedPositionManagement.enableVolatilityExhaustionReduction" type="checkbox" /> Volatility-exhaustion reduction</label>
          </div>
          <div class="row checks">
            <label><input v-model="form.improvedPositionManagement.enableRiskWindowReduction" type="checkbox" /> Configured session-risk reduction</label>
            <label><input v-model="form.improvedPositionManagement.enableExecutionCostStressReduction" type="checkbox" /> Spread/ATR stress reduction</label>
          </div>
          <div v-if="form.improvedPositionManagement.enableRiskWindowReduction" class="row">
            <label>Risk start UTC <input v-model="form.improvedPositionManagement.riskWindowStartUtc" type="time" /></label>
            <label>Risk end UTC <input v-model="form.improvedPositionManagement.riskWindowEndUtc" type="time" /></label>
          </div>
          <div class="row checks">
            <label><input v-model="form.improvedPositionManagement.exitOnAdverseStructureBreak" type="checkbox" /> Adverse exit</label>
            <label><input v-model="form.improvedPositionManagement.preserveBracketTarget" type="checkbox" /> Preserve target</label>
          </div>
        </fieldset>
        <p class="behaviour-summary">{{ managementSummary }}</p>
        <fieldset>
          <legend>Account profit lock</legend>
          <div class="row">
            <label>
              Daily profit target
              <input
                v-model.number="form.dailyEquityProfitTarget"
                type="number"
                min="0"
                step="100"
              />
            </label>
            <label>
              Giveback activation
              <input
                v-model.number="form.dailyEquityGivebackActivation"
                type="number"
                min="0"
                step="100"
              />
            </label>
          </div>
          <div class="row">
            <label>
              Maximum peak giveback
              <input
                v-model.number="form.maximumDailyEquityGiveback"
                type="number"
                min="0"
                step="100"
              />
            </label>
          </div>
          <p class="muted">
            Values are in account currency. Use 0 to disable. These rules pause new
            entries for the rest of the UTC day; open positions remain protected and managed.
          </p>
        </fieldset>
        <fieldset class="management-config">
          <legend>Position sizing and capital reservation</legend>
          <div class="row">
            <label>Mode
              <select v-model="form.positionSizingMode">
                <option>FixedQuantity</option>
                <option>FixedCashRisk</option>
                <option>FixedFractionalRisk</option>
              </select>
            </label>
            <label>Fallback fixed quantity <input v-model.number="form.quantity" type="number" min="1" step="1" /></label>
          </div>
          <div class="row">
            <label>Risk per trade (%) <input v-model.number="form.riskPercentOfEquity" type="number" min="0.01" max="100" step="0.05" /></label>
            <label>Fixed cash risk <input v-model.number="form.fixedCashRisk" type="number" min="1" step="10" /></label>
          </div>
          <div class="row">
            <label>Minimum quantity <input v-model.number="form.minimumQuantity" type="number" min="0.00000001" step="1" /></label>
            <label>Maximum quantity (0 = none) <input v-model.number="form.maximumQuantity" type="number" min="0" step="1" /></label>
            <label>Quantity step <input v-model.number="form.quantityStep" type="number" min="0.00000001" step="1" /></label>
          </div>
          <div class="row">
            <label>Maximum account margin usage (%) <input v-model.number="form.maximumAccountMarginUsagePercent" type="number" min="1" max="100" step="1" /></label>
            <label>Maximum one-position margin (%) <input v-model.number="form.maximumSinglePositionMarginPercent" type="number" min="1" max="100" step="1" /></label>
          </div>
          <small class="muted">Fixed-fractional mode calculates quantity from account equity, original stop distance, currency conversion, costs and margin caps. It rounds down so planned risk is not exceeded.</small>
        </fieldset>
        <label>Balance <input v-model.number="form.startingBalance" type="number" /></label>
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
        <button type="submit" :disabled="busy || brokerCatalogLoading || !selectedBroker.isAvailable">Run Simulation</button>
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
            <div><dt>Configuration</dt><dd class="mono">{{ job.simulationConfigurationId ?? '—' }}</dd></div>
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
            <tr>
              <td>Win rate %</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-wr`">{{ performanceMetric(strategy, 'winRatePercent') }}</td>
            </tr>
            <tr>
              <td>Profit factor</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-pf`">{{ performanceMetric(strategy, 'profitFactor') }}</td>
            </tr>
            <tr>
              <td>Average R</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-ar`">{{ performanceMetric(strategy, 'averageR') }}</td>
            </tr>
            <tr>
              <td>Max drawdown</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-dd`">{{ performanceMetric(strategy, 'maximumDrawdown') }}</td>
            </tr>
            <tr>
              <td>Average MFE / MAE</td>
              <td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-exc`">
                {{ performanceMetric(strategy, 'averageMfe') }} / {{ performanceMetric(strategy, 'averageMae') }}
              </td>
            </tr>
            <tr><td>BE / structure activations</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-act`">{{ performanceMetric(strategy, 'breakEvenActivations') }} / {{ performanceMetric(strategy, 'structureTrailingActivations') }}</td></tr>
            <tr><td>Accepted / rejected moves</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-moves`">{{ performanceMetric(strategy, 'acceptedStopAmendments') }} / {{ performanceMetric(strategy, 'rejectedStopAmendments') }}</td></tr>
            <tr><td>Avg amendments / locked R</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-locked`">{{ performanceMetric(strategy, 'averageAmendmentsPerTrade') }} / {{ performanceMetric(strategy, 'averageMaximumLockedR') }}</td></tr>
            <tr><td>Avg MFE giveback R</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-giveback`">{{ performanceMetric(strategy, 'averageProfitGivebackFromMfeR') }}</td></tr>
            <tr><td>Trailing / BE exits</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-trail-exits`">{{ performanceMetric(strategy, 'trailingStopExits') }} / {{ performanceMetric(strategy, 'breakEvenExits') }}</td></tr>
            <tr><td>Partial reductions / avg per trade</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-reductions`">{{ performanceMetric(strategy, 'positionReductions') }} / {{ performanceMetric(strategy, 'averagePartialExitsPerTrade') }}</td></tr>
            <tr><td>Partial-exit net P/L</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-partial-pnl`">{{ performanceMetric(strategy, 'partialExitNetProfit') }}</td></tr>
            <tr><td>Stagnation / structure reductions</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-reduction-types`">{{ performanceMetric(strategy, 'stagnationReductions') }} / {{ performanceMetric(strategy, 'structuralDeteriorationReductions') }}</td></tr>
            <tr><td>Momentum / volatility reductions</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-decay-reductions`">{{ performanceMetric(strategy, 'momentumDecayReductions') }} / {{ performanceMetric(strategy, 'volatilityExhaustionReductions') }}</td></tr>
            <tr><td>Session / cost-stress reductions</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-risk-reductions`">{{ performanceMetric(strategy, 'sessionRiskReductions') }} / {{ performanceMetric(strategy, 'executionCostStressReductions') }}</td></tr>
            <tr><td>Profit-floor stop / market exits</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-floor-exits`">{{ performanceMetric(strategy, 'profitFloorStopExits') }} / {{ performanceMetric(strategy, 'profitFloorExits') }}</td></tr>
            <tr><td>MFE-giveback stop / market exits</td><td v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-mfe-exits`">{{ performanceMetric(strategy, 'mfeGivebackStopExits') }} / {{ performanceMetric(strategy, 'maximumGivebackExits') }}</td></tr>
          </tbody>
        </table>
        <p v-else class="muted">Strategy metrics appear as frames process.</p>

        <h3>Open-position management</h3>
        <div v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-management`" class="management-runtime">
          <strong>{{ strategy.strategyName }}</strong>
          <dl v-if="strategy.openPositionManagement" class="metrics compact-metrics">
            <div><dt>Entry</dt><dd>{{ strategy.openPositionManagement.entryPrice }}</dd></div>
            <div><dt>Initial / remaining quantity</dt><dd>{{ strategy.openPositionManagement.initialQuantity }} / {{ strategy.openPositionManagement.remainingQuantity }}{{ strategy.openPositionManagement.reductionPending ? ' (reduction pending)' : '' }}</dd></div>
            <div><dt>Partial reductions</dt><dd>{{ strategy.openPositionManagement.positionReductionCount }}</dd></div>
            <div><dt>Initial / current stop</dt><dd>{{ strategy.openPositionManagement.initialStop }} / {{ strategy.openPositionManagement.currentStop }}</dd></div>
            <div><dt>Target</dt><dd>{{ strategy.openPositionManagement.target ?? 'none (Legacy compatible)' }}</dd></div>
            <div><dt>Open / maximum R</dt><dd>{{ strategy.openPositionManagement.currentOpenR.toFixed(2) }} / {{ strategy.openPositionManagement.maximumOpenR.toFixed(2) }}</dd></div>
            <div><dt>Locked R</dt><dd>{{ strategy.openPositionManagement.lockedInR.toFixed(2) }}</dd></div>
            <div><dt>Mode / last action</dt><dd>{{ strategy.openPositionManagement.trailingMode }} / {{ strategy.openPositionManagement.lastManagementAction ?? '—' }}</dd></div>
            <div><dt>Reason</dt><dd>{{ strategy.openPositionManagement.lastManagementReason ?? '—' }}</dd></div>
            <div><dt>Next close</dt><dd>{{ strategy.openPositionManagement.nextManagementIntervalClose ?? '—' }}</dd></div>
          </dl>
          <small v-else class="muted">No open position.</small>
        </div>

        <h3>Live trades ({{ flatTrades.length }})</h3>
        <p class="muted">Completed trades arrive through SignalR and remain available from incremental replay storage.</p>
        <div class="trade-list">
          <button
            v-for="item in trades.slice(-12).reverse()"
            :key="`${item.strategyId}-${item.trade.setupId}-${item.trade.closedAt}`"
            type="button"
            class="linkish"
            @click="loadExecutionDetail(item)"
          >
            {{ item.strategyId }} · {{ item.trade.exitReason }} · R {{ item.trade.rMultiple?.toFixed(2) ?? '—' }} · reductions {{ item.trade.positionReductionCount ?? 0 }} · amendments {{ item.trade.stopAmendmentCount ?? 0 }}
          </button>
        </div>
        <p v-if="executionDetailStatus" class="muted">{{ executionDetailStatus }}</p>
        <AnalysisChart
          v-if="selectedTrade && executionDetailFrames.length"
          :frames="executionDetailFrames"
          :selected-index="executionDetailFrames.length - 1"
          :window-size="180"
          :layers="replayLayers"
          :trades="[selectedTrade.trade]"
        />
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
        <AnalysisChart
          v-if="replayFrames.length"
          :frames="replayFrames"
          :selected-index="replayIndex"
          :window-size="180"
          :layers="replayLayers"
          :trades="flatTrades"
        />
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
.inline-check { flex-direction: row; align-items: center; }
.broker-picker {
  border: 1px solid rgba(96, 165, 250, 0.25);
  border-radius: 0.55rem;
  display: grid;
  gap: 0.55rem;
  margin: 0;
}
.broker-picker legend { color: #bfdbfe; padding: 0 0.3rem; }
.broker-description { margin: 0; }
.warning { color: #fbbf24; margin: 0; }
input[readonly] { opacity: 0.8; cursor: default; }
.management-config {
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 0.55rem;
  display: grid;
  gap: 0.5rem;
  margin: 0;
}
.management-config legend { color: #bfdbfe; padding: 0 0.3rem; }
.behaviour-summary {
  background: rgba(59, 130, 246, 0.09);
  border-left: 3px solid #3b82f6;
  border-radius: 0.35rem;
  font-size: 0.82rem;
  margin: 0;
  padding: 0.55rem;
}
.dataset-row { align-items: end; }
.compact-button { align-self: end; }
.management-runtime { border-top: 1px solid rgba(255, 255, 255, 0.08); display: grid; gap: 0.35rem; padding-top: 0.5rem; }
.compact-metrics > div { grid-template-columns: 9rem 1fr; font-size: 0.8rem; }
.trade-list { display: grid; gap: 0.35rem; max-height: 16rem; overflow: auto; }
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
