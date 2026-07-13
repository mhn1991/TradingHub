export interface ReplayDataset {
  schemaVersion: number
  title: string
  instrument: string
  generatedAt: string
  source: string
  parameters: AnnotationParameters
  series: ReplaySeries[]
}

export interface AnnotationParameters {
  candleCapacity: number
  swingCapacity: number
  indicatorCapacity: number
  atrPeriod: number
  rsiPeriod: number
  bollingerPeriod: number
  bollingerStandardDeviations: number
  swingLeftBars: number
  swingRightBars: number
  heavyAnalysisEveryCandles: number
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

export interface IndicatorSnapshot {
  atr: number | null
  rsi: number | null
  bollingerMiddle: number | null
  bollingerUpper: number | null
  bollingerLower: number | null
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
  originTime: string
  originPrice: number
  slopePerSecond: number
  inlierCount: number
  meanAbsoluteError: number
  fitScore: number
  type: TrendlineType
}

export type ChannelDirection = 'Falling' | 'Sideways' | 'Rising'

export interface PriceChannel {
  lowerLine: Trendline
  upperLine: Trendline
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
