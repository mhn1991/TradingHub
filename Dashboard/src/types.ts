export interface ReplayDataset {
  schemaVersion: number
  title: string
  instrument: string
  generatedAt: string
  source: string
  parameters: AnnotationParameters
  series: ReplaySeries[]
  trades?: ReplayTrade[]
  performance?: ReplayPerformanceSummary | null
}

export interface BacktestMarketDataset {
  schemaVersion: number
  title: string
  instrument: string
  generatedAt: string
  source: string
  parameters: AnnotationParameters
  series: ReplaySeries[]
}

export interface BacktestRunDataset {
  schemaVersion: number
  strategyName: string
  title: string
  trades: ReplayTrade[]
  performance: ReplayPerformanceSummary
}

export interface AnnotationParameters {
  candleCapacity: number
  swingCapacity: number
  indicatorCapacity: number
  atrPeriod: number
  atrAnalysisHistoryPeriod?: number
  atrAnalysisChangeLookback?: number
  atrAnalysisMinimumSamples?: number
  atrDirectionThresholdPercent?: number
  rsiPeriod: number
  rsiMomentumLookback?: number
  rsiMomentumThreshold?: number
  rsiMinimumDivergenceDifference?: number
  rsiMinimumPriceDifferenceAtr?: number
  rsiSignalLifetimeCandles?: number
  bollingerPeriod: number
  bollingerStandardDeviations: number
  bollingerWidthHistoryPeriod?: number
  bollingerWidthChangeLookback?: number
  bollingerWidthMinimumSamples?: number
  bollingerWidthDirectionThresholdPercent?: number
  bollingerSqueezePercentile?: number
  bollingerWidePercentile?: number
  swingLeftBars: number
  swingRightBars: number
  heavyAnalysisEveryCandles: number
  structureDirectionToleranceAtr?: number
  resetLinesOnStructureChange?: boolean
}


export interface ReplayTrade {
  strategyName: string
  setupId: string
  side: string
  setupStartedAt: string
  confirmationAt: string | null
  signalCreatedAt: string
  openedAt: string | null
  closedAt: string | null
  signalPrice: number | null
  entryPrice: number | null
  exitPrice: number | null
  stopLossPrice: number | null
  takeProfitPrice: number | null
  quantity: number
  expectedRewardRisk: number | null
  stopSource: string | null
  targetSource: string | null
  grossProfitLoss: number
  commission: number
  netProfitLoss: number
  rMultiple: number | null
  exitReason: string
  setupReason: string
  exitReasonText: string | null
}

export interface ReplayPerformanceSummary {
  tradeCount: number
  winningTrades: number
  losingTrades: number
  netProfit: number
  winRatePercent: number
  averageR: number | null
  profitFactor: number | null
  maximumDrawdown: number
  currency: string
  startingBalance: number
  finalBalance: number
  finalEquity: number
  totalCommission: number
}

export interface StrategyProgressSnapshot {
  strategyName: string
  strategyId: string
  balance: number
  equity: number
  unrealizedProfitLoss: number
  openPositions: number
  completedTrades: number
  activeSetups: number
  netProfit: number
  status?: string | null
  lastError?: string | null
}

export interface SimulationJobSnapshot {
  id: string
  revision?: number
  status: string
  createdAt: string
  startedAt?: string | null
  completedAt?: string | null
  instrument: string
  requestedFrom: string
  requestedTo: string
  warmupFrom?: string | null
  currentMarketTime?: string | null
  processedBaseCandles: number
  estimatedBaseCandleCount?: number | null
  progressPercent: number
  candlesPerSecond: number
  strategies: StrategyProgressSnapshot[]
  error?: string | null
  isComplete: boolean
  outputDirectory?: string | null
  inputStreamId?: string | null
  inputRequestId?: string | null
  inputHash?: string | null
  dataSourceStatus?: string | null
  sourceProgress?: {
    phase: string
    fromCache: boolean
    candlesRead: number
    pagesRead: number
    latestCandle?: string | null
    estimatedCandles?: number | null
    percent?: number | null
  } | null
}

export interface BacktestManifestRun {
  id: string
  strategyName: string
  file: string
  performance: ReplayPerformanceSummary
}

export interface BacktestManifest {
  schemaVersion: number
  title: string
  generatedAt: string
  instrument: string
  from: string
  to: string
  executionInterval: string
  marketFile: string
  runs: BacktestManifestRun[]
}

export interface ReplaySeries {
  interval: string
  intervalSeconds: number
  frames: ReplayFrame[]
}

export interface ReplayFrame {
  index: number
  availableAt: string
  candle: ReplayCandle
  indicators: IndicatorSnapshot
  swings: SwingPoint[]
  priceZones: PriceZone[]
  trendlines: Trendline[]
  channels: PriceChannel[]
  marketStructure?: MarketStructureSnapshot
  confidence: ConfidenceScore
  analysisMicroseconds: number
}

export interface ReplayCandle {
  openTime: string
  closeTime: string
  open: number
  high: number
  low: number
  close: number
  volume: number
}

export type MomentumDirection = 'Unknown' | 'Falling' | 'Stable' | 'Rising'
export type VolatilityDirection = 'Unknown' | 'Contracting' | 'Stable' | 'Expanding'
export type AtrVolatilityRegime = 'Unknown' | 'VeryLow' | 'Low' | 'Normal' | 'High' | 'VeryHigh'
export type BollingerWidthRegime = 'Unknown' | 'Squeeze' | 'Narrow' | 'Normal' | 'Wide' | 'Expansion'
export type RsiZone = 'Unknown' | 'Oversold' | 'Bearish' | 'Neutral' | 'Bullish' | 'Overbought'
export type RsiRelationshipType =
  | 'None'
  | 'RegularBullishDivergence'
  | 'RegularBearishDivergence'
  | 'HiddenBullishDivergence'
  | 'HiddenBearishDivergence'
  | 'BullishConvergence'
  | 'BearishConvergence'

export interface AtrAnalysisSnapshot {
  normalizedPercent: number | null
  changePercent: number | null
  percentile: number | null
  direction: VolatilityDirection
  regime: AtrVolatilityRegime
  sampleCount: number
}

export interface BollingerAnalysisSnapshot {
  bandwidthPercent: number | null
  bandwidthChangePercent: number | null
  percentB: number | null
  widthPercentile: number | null
  widthDirection: VolatilityDirection
  widthRegime: BollingerWidthRegime
  isSqueeze: boolean
  isExpansion: boolean
  squeezeReleased: boolean
  sampleCount: number
}

export interface RsiRelationshipSnapshot {
  type: RsiRelationshipType
  firstPivotTime: string
  secondPivotTime: string
  confirmedAt: string
  firstPrice: number
  secondPrice: number
  firstRsi: number
  secondRsi: number
  priceChange: number
  rsiChange: number
  strength: number
  ageCandles: number
  isDivergence: boolean
  isConvergence: boolean
}

export interface RsiAnalysisSnapshot {
  zone: RsiZone
  momentumDirection: MomentumDirection
  momentumChange: number | null
  latestRelationship: RsiRelationshipSnapshot | null
  isNewRelationship: boolean
  sampleCount: number
}

export interface IndicatorSnapshot {
  atr: number | null
  rsi: number | null
  bollingerMiddle: number | null
  bollingerUpper: number | null
  bollingerLower: number | null
  atrAnalysis?: AtrAnalysisSnapshot
  rsiAnalysis?: RsiAnalysisSnapshot
  bollingerAnalysis?: BollingerAnalysisSnapshot
}

export type SwingType = 'High' | 'Low'

export interface SwingPoint {
  pivotTime: string
  confirmedAt: string
  price: number
  type: SwingType
  strength: number
}

export type PriceZoneType = 'Support' | 'Resistance' | 'Mixed'

export interface PriceZone {
  lowerPrice: number
  upperPrice: number
  centrePrice: number
  touchCount: number
  strength: number
  type: PriceZoneType
}

export type TrendlineType = 'Support' | 'Resistance'

export interface Trendline {
  startTime?: string
  endTime?: string
  originTime: string
  originPrice: number
  slopePerSecond: number
  inlierCount: number
  meanAbsoluteError: number
  fitScore: number
  type: TrendlineType
}

export type MarketStructureDirection = 'Unknown' | 'Rising' | 'Falling' | 'Sideways'
export type MarketStructureBreak = 'None' | 'Bullish' | 'Bearish'

export interface MarketStructureSnapshot {
  direction: MarketStructureDirection
  previousDirection: MarketStructureDirection
  break: MarketStructureBreak
  directionChanged: boolean
  segmentStartedAt: string | null
  changedAt: string | null
  lastSwingHigh: SwingPoint | null
  lastSwingLow: SwingPoint | null
  consecutiveHigherHighs: number
  consecutiveHigherLows: number
  consecutiveLowerHighs: number
  consecutiveLowerLows: number
  strength: number
}

export type ChannelDirection = 'Falling' | 'Sideways' | 'Rising'

export interface PriceChannel {
  lowerLine: Trendline
  upperLine: Trendline
  startTime?: string
  endTime?: string
  direction: ChannelDirection
  width: number
  widthAtr: number
  confidence: number
}

export interface ConfidenceContribution {
  rule: string
  score: number
  explanation: string
}

export interface ConfidenceScore {
  total: number
  contributions: ConfidenceContribution[]
}

export interface ChartLayers {
  bollinger: boolean
  bollingerRegimes: boolean
  rsiRelationships: boolean
  atr: boolean
  volume: boolean
  swings: boolean
  zones: boolean
  trendlines: boolean
  channels: boolean
}

export type LiveConnectionState =
  | 'Starting'
  | 'WarmingUp'
  | 'Connected'
  | 'Reconnecting'
  | 'Faulted'
  | 'Stopped'

export interface LiveFeedStatus {
  state: LiveConnectionState
  symbol: string
  interval: string
  message: string
  connectedAt: string | null
  lastMessageAt: string | null
  lastClosedCandleAt: string | null
  reconnectAttempt: number
  gapsDetected: number
}

export interface LiveReplayPayload {
  revision: number
  status: LiveFeedStatus
  dataset: ReplayDataset
}

export interface LiveReplayUpdate {
  revision: number
  status: LiveFeedStatus
  frames: ReplayFrame[]
}

export type WorkspaceEnvironment = 'Demo' | 'Live'
export type WorkspaceDataKind = 'Replay' | 'Market'

export interface WorkspaceAsset {
  symbol: string
  displayName: string
  instrument: string
  timeframes: string[]
}

export interface WorkspaceBroker {
  id: string
  displayName: string
  environment: WorkspaceEnvironment
  dataKind: WorkspaceDataKind
  isConfigured: boolean
  isReadOnly: boolean
  description: string
  assets: WorkspaceAsset[]
  canTrade: boolean
}

export interface WorkspaceStreamSelection {
  brokerId: string
  symbol: string
  interval: string
}

export interface WorkspaceCatalog {
  generatedAt: string
  streamingSelection: WorkspaceStreamSelection
  brokers: WorkspaceBroker[]
  warning?: string | null
}

export interface WorkspaceDefinition {
  id: string
  name: string
  brokerId: string
  symbol: string
  interval: string
}

export interface WorkspaceSnapshot {
  status: LiveFeedStatus
  dataset: ReplayDataset
}

export interface BrokerPosition {
  positionId: string
  instrument: string
  nativeInstrument: string
  side: string
  quantity: number
  averagePrice: number | null
  unrealizedProfitLoss: number | null
}

export interface BrokerOrder {
  brokerOrderId: string
  clientOrderId: string | null
  instrument: string
  nativeInstrument: string
  side: string
  type: string
  status: string
  normalizedStatus: string
  quantity: number
  filledQuantity: number | null
  price: number | null
  createdAt: string | null
}

export interface OandaWorkspaceAccount {
  accountId: string
  currency: string | null
  balance: number | null
  available: number | null
  marginUsed: number | null
  unrealizedProfitLoss: number | null
  canTrade: boolean | null
  positions: BrokerPosition[]
  pendingOrders: BrokerOrder[]
  demoOrderExecutionEnabled: boolean
}

export interface OandaOrderEventBatch {
  revision: number
  events: Array<{
    brokerOrderId: string
    clientOrderId: string | null
    instrument: string
    type: string
    timestamp: string
    message: string | null
  }>
}

export interface OrderSubmission {
  clientOrderId: string
  brokerOrderId: string | null
  status: string
  certainty: string
  rejectionReason: string | null
}
