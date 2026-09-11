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
  cciPeriod?: number
  smaFastPeriod?: number
  smaSlowPeriod?: number
  swingLeftBars: number
  swingRightBars: number
  heavyAnalysisEveryCandles: number
  structureDirectionToleranceAtr?: number
  resetLinesOnStructureChange?: boolean
}


export interface ReplayTrade {
  strategyId?: string
  strategyName: string
  setupId: string
  positionId?: string | null
  side: string
  setupStartedAt: string
  confirmationAt: string | null
  signalCreatedAt: string
  openedAt: string | null
  closedAt: string | null
  signalPrice: number | null
  entryPrice: number | null
  exitPrice: number | null
  averageExitPrice?: number | null
  initialQuantity?: number
  remainingQuantity?: number
  stopLossPrice: number | null
  initialStopLossPrice?: number | null
  currentStopLossPrice?: number | null
  finalStopLossPrice?: number | null
  takeProfitPrice: number | null
  quantity: number
  expectedRewardRisk: number | null
  stopSource: string | null
  targetSource: string | null
  grossProfitLoss: number
  commission: number
  netProfitLoss: number
  rMultiple: number | null
  stopAmendmentCount?: number
  breakEvenActivatedAt?: string | null
  structureTrailingActivatedAt?: string | null
  maximumLockedInR?: number
  positionReductionCount?: number
  runnerActivatedAt?: string | null
  profitFloorActivatedAt?: string | null
  maximumGivebackProtectionActivatedAt?: string | null
  completedReductionStageIds?: string[]
  partialExits?: PartialExitRecord[]
  stopAmendments?: StopAmendmentRecord[]
  maximumFavourableExcursionPrice?: number | null
  maximumFavourableExcursionAmount?: number
  maximumFavourableExcursionR?: number | null
  maximumFavourableExcursionAt?: string | null
  maximumAdverseExcursionPrice?: number | null
  maximumAdverseExcursionAmount?: number
  maximumAdverseExcursionR?: number | null
  maximumAdverseExcursionAt?: string | null
  exitReason: string
  setupReason: string
  exitReasonText: string | null
  totalFinancing?: number
  netProfitAfterFinancing?: number
  entryRegime?: string
  currentRegime?: string
  entryManagementProfileId?: string
  currentManagementProfileId?: string
  managementProfileSwitchReason?: string | null
  /** Structural Indicator and Adaptive Target Management plan: null for legacy/v1 trades and
   *  any non-structural agent - only set when the entry decision came from a
   *  structural-confluence-v2 managed policy. */
  exitPolicy?: 'FixedStructuralTarget' | 'PartialThenRunner' | 'ManagedExpansion' | null
  targetPlan?: TradeTargetPlan | null
  targetPlanRevisions?: TargetPlanRevision[]
  /** Weighted planned reward at entry (never changes after entry, even after stop amendments). */
  plannedR?: number | null
  /** Net trade P&L / original never-recalculated risk cash. Same ratio as rMultiple for v2
   *  trades - populated separately under the plan's own name for report consumers. */
  realizedR?: number | null
  initialRiskCash?: number | null
}

export type TradeTargetRole = 'Checkpoint' | 'Terminal' | 'HardBarrier' | 'Projection'
export type TradeTargetSourceKind = 'Swing' | 'LiquidityPool' | 'SupplyDemandZone' | 'RMultipleProjection'
export type TradeTargetSignificanceTier = 'TierA' | 'TierB' | 'TierC'

export interface TradeTargetCandidate {
  candidateId: string
  clusterId: string
  sourceKind: TradeTargetSourceKind
  sourceId: string
  sourceInterval: string
  lifecycleState: string
  lowerBoundary: number
  upperBoundary: number
  executionPrice: number
  role: TradeTargetRole
  tier: TradeTargetSignificanceTier
  quality: number
  prominence: number
  freshness: number
  priorTouchCount?: number | null
  distanceAtr: number
  targetR?: number | null
  constituentSourceIds: string[]
  availableAt: string
  reasonCodes: string[]
}

export interface TradeTargetPlan {
  planVersion: number
  revision: number
  exitPolicy: 'FixedStructuralTarget' | 'PartialThenRunner' | 'ManagedExpansion'
  originalEntry: number
  originalStop: number
  initialRiskPrice: number
  selectedCheckpointId?: string | null
  selectedTerminalId?: string | null
  candidates: TradeTargetCandidate[]
  partialFraction: number
  minimumRunnerFraction: number
  plannedR: number
  conservativeOpportunityR: number
  createdAt: string
  lastRevisedAt: string
  lastRevisionReason?: string | null
}

export interface TargetPlanRevision {
  previousRevision: number
  newRevision: number
  triggeringEvent: string
  consumedCandidateId?: string | null
  newlySelectedCandidateId?: string | null
  decisionAt: string
}

export interface PartialExitRecord {
  exitId: string
  stageId: string
  requestedSequence: number
  executionSequence: number
  requestedAt: string
  executedAt: string
  quantityBefore: number
  quantityClosed: number
  quantityRemaining: number
  exitPrice: number
  grossProfitLoss: number
  allocatedEntryCommission: number
  exitCommission: number
  netProfitLoss: number
  realizedR: number
  openProfitRBeforeExit: number
  reason: string
  structureSource?: string | null
  structuralLevel?: number | null
  explanation: string
  brokerOrderId?: string | null
}

export interface StopAmendmentRecord {
  requestedSequence: number
  effectiveSequence?: number | null
  requestedAt: string
  acceptedAt?: string | null
  previousStopPrice: number
  proposedStopPrice: number
  acceptedStopPrice?: number | null
  openProfitR: number
  lockedProfitR: number
  reason: string
  explanation: string
  status: string
  previousStopOrderId?: string | null
  currentStopOrderId?: string | null
  rejectionReason?: string | null
  analysisInterval?: string | null
  analysisSnapshotVersion?: number | null
  atr?: number | null
  structuralLevel?: number | null
  structureSource?: string | null
  rawEntryPrice?: number | null
  costAdjustedBreakEvenPrice?: number | null
  atrBufferPrice?: number | null
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
  peakEquity: number
  equityProtectionActivationCount: number
}

export interface EquityProtectionStatusSnapshot {
  peakEquity: number
  drawdownPercent: number
  currentRiskMultiplier: number
  activatedTierIds: string[]
  newEntriesPaused: boolean
}

export interface StrategyProgressSnapshot {
  strategyName: string
  strategyId: string
  /** Canonical instrument key string (e.g. FX:GBP/JPY) this strategy trades. */
  instrument?: string | null
  balance: number
  equity: number
  unrealizedProfitLoss: number
  openPositions: number
  completedTrades: number
  activeSetups: number
  netProfit: number
  status?: string | null
  lastError?: string | null
  performance?: StrategyRuntimePerformance | null
  openPositionManagement?: OpenPositionManagementSnapshot | null
  equityProtection?: EquityProtectionStatusSnapshot | null
  portfolioRisk?: PortfolioRiskStatusSnapshot | null
}

export interface PortfolioRiskStatusSnapshot {
  openHeat: number
  pendingHeat: number
  totalHeat: number
  totalHeatPercent: number
  strategyHeat: number
  instrumentHeat: number
  currencyRisk: Record<string, number>
  clusterHeat: Record<string, number>
  marginUsed: number
  reservedMargin: number
  unallocatedMargin: number
  accountPeakEquity: number
  accountProtectedFloor: number
  accountActivatedTierIds: string[]
}

export interface OpenPositionManagementSnapshot {
  setupId: string
  side: string
  entryPrice: number
  initialQuantity: number
  remainingQuantity: number
  positionReductionCount: number
  reductionPending: boolean
  initialStop: number
  currentStop: number
  target?: number | null
  currentOpenR: number
  maximumOpenR: number
  lockedInR: number
  trailingMode: string
  lastManagementAction?: string | null
  lastManagementReason?: string | null
  nextManagementIntervalClose?: string | null
  entryRegime?: string | null
  currentRegime?: string | null
  regimeConfidence?: number | null
  baseRiskBudget?: number | null
  finalRiskBudget?: number | null
  finalRiskMultiplier?: number | null
  riskMultipliers: Record<string, number>
  rawQuantity?: number | null
  allocatedQuantity?: number | null
  plannedStopRisk?: number | null
  portfolioReservationId?: string | null
  correlationClusterId?: string | null
}

export interface StrategyRuntimePerformance {
  grossProfit: number
  netProfit: number
  commissions: number
  tradeCount: number
  wins: number
  losses: number
  winRatePercent: number
  profitFactor?: number | null
  averageR?: number | null
  medianR?: number | null
  expectancy: number
  maximumDrawdown: number
  averageHoldingSeconds?: number | null
  averageSetupSeconds?: number | null
  averageMfe: number
  averageMae: number
  mfeCapturedPercent?: number | null
  exitReasons: Record<string, number>
  breakEvenActivations: number
  structureTrailingActivations: number
  acceptedStopAmendments: number
  rejectedStopAmendments: number
  unsupportedStopAmendments: number
  averageAmendmentsPerTrade: number
  averageMaximumLockedR: number
  averageProfitGivebackFromMfeR: number
  trailingStopExits: number
  breakEvenExits: number
  positionReductions: number
  averagePartialExitsPerTrade: number
  partialExitNetProfit: number
  stagnationReductions: number
  structuralDeteriorationReductions: number
  momentumDecayReductions: number
  volatilityExhaustionReductions: number
  sessionRiskReductions: number
  executionCostStressReductions: number
  runnerActivations: number
  profitFloorStopExits: number
  mfeGivebackStopExits: number
  profitFloorExits: number
  maximumGivebackExits: number
  financing?: number
  tradesByEntryRegime?: Record<string, number>
  expectancyByEntryRegime?: Record<string, number>
}

export interface ImportedDatasetMetadata {
  datasetId: string
  fileName: string
  interval: string
  rows: number
  sizeBytes: number
  createdAt: string
  expiresAt: string
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
  simulationConfigurationId?: string | null
  inputHash?: string | null
  dataSourceStatus?: string | null
  sourceProgress?: HistoricalSourceProgress | null
  /** Per-instrument download/cache status, keyed by canonical instrument key string (e.g. FX:EUR/USD). */
  sourceProgressByInstrument?: Record<string, HistoricalSourceProgress>
}

export interface HistoricalSourceProgress {
  phase: string
  fromCache: boolean
  candlesRead: number
  pagesRead: number
  latestCandle?: string | null
  estimatedCandles?: number | null
  percent?: number | null
}

export type CalibrationArtifactType = 'Setup' | 'Management' | 'MetaModel'

export interface CalibrationArtifactMetadata {
  id: string
  type: CalibrationArtifactType | string
  schemaVersion: number
  calibrationId: string
  createdAt: string
  contentHash: string
  description?: string | null
}

export type ResearchJobStatus = 'Queued' | 'Running' | 'Completed' | 'Failed'

export interface ResearchJobSnapshot {
  jobId: string
  kind: string
  status: ResearchJobStatus | string
  createdAt: string
  startedAt?: string | null
  completedAt?: string | null
  instrument: string
  strategy: string
  from: string
  to: string
  message?: string | null
  error?: string | null
  artifactId?: string | null
  artifactType?: string | null
  calibrationId?: string | null
  tradeCount: number
  outcomeCount: number
  bucketOrCohortCount: number
}

/** Shared between Research panel and Simulator panel via localStorage. */
export interface ResearchArtifactSelection {
  setupCalibrationArtifactId: string
  managementCalibrationArtifactId: string
  metaModelArtifactId: string
}

export const RESEARCH_ARTIFACT_SELECTION_KEY = 'tradinghub.research.artifacts.v1'

export type TradingPolicyProfileStatus = 'Research' | 'Reviewed' | 'ApprovedForDemo' | 'Retired'

/** Display-only subset of the backend's TradingPolicyProfile - the full record also carries the
 *  agent definition and every risk/management option, which the Dashboard never edits directly. */
export interface TradingPolicyProfile {
  profileId: string
  revision: number
  strategyId: string
  strategyVersion: string
  status: TradingPolicyProfileStatus | string
  setupCalibrationArtifactId?: string | null
  managementCalibrationArtifactId?: string | null
  metaModelArtifactId?: string | null
  createdAt: string
  sourceCommit: string
  description?: string | null
  configurationHash: string
}

export interface PromoteTradingPolicyRequest {
  revision?: number
  approveForDemo: boolean
  strategyVersion?: string | null
  setupCalibrationArtifactId?: string | null
  managementCalibrationArtifactId?: string | null
  metaModelArtifactId?: string | null
  description?: string | null
}

export type CalibrationBundleCandidateStatus = 'PendingReview' | 'Approved' | 'Rejected' | 'Superseded'

export interface CalibrationBundleCandidate {
  id: string
  setupArtifactId: string
  metaModelArtifactId: string
  managementArtifactId: string
  proposedProfile: TradingPolicyProfile
  status: CalibrationBundleCandidateStatus | string
  createdAt: string
  reviewedBy?: string | null
  reviewedAt?: string | null
  rejectionReason?: string | null
  approvedProfileId?: string | null
  approvedProfileRevision?: number | null
}

export interface SimulationProfileDiff {
  leftResolvedJson: string
  rightResolvedJson: string
  changedPaths: string[]
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
  priceAction?: PriceActionSnapshot
  marketRegime?: MarketRegimeSnapshot
  neoWave?: NeoWaveSnapshot
  supplyDemand?: SupplyDemandAnalysisSnapshot
  liquidity?: LiquidityAnalysisSnapshot
  supplyDemandLiquidityConfluence?: SupplyDemandLiquidityConfluenceSnapshot
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

export type MarketEfficiencyState =
  | 'Unknown'
  | 'HighlyChoppy'
  | 'Choppy'
  | 'Transitional'
  | 'Efficient'
  | 'HighlyEfficient'

export interface EfficiencyAnalysisSnapshot {
  percentile: number | null
  direction: MomentumDirection
  state: MarketEfficiencyState
  sampleCount: number
}

export interface DonchianSnapshot {
  upper: number | null
  lower: number | null
  middle: number | null
  width: number | null
  widthAtr: number | null
  closedAbovePreviousUpper: boolean
  closedBelowPreviousLower: boolean
  barsSinceUpperBreak: number
  barsSinceLowerBreak: number
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
  /** RSI on the immediately preceding candle; null on the first sample. */
  previousValue: number | null
  latestRelationship: RsiRelationshipSnapshot | null
  isNewRelationship: boolean
  sampleCount: number
}

export type PriceActionDirection = 'Neutral' | 'Bullish' | 'Bearish'
export type PriceActionEventType =
  | 'BullishBreakOfStructure'
  | 'BearishBreakOfStructure'
  | 'BullishChangeOfCharacter'
  | 'BearishChangeOfCharacter'
  | 'BullishRetestHeld'
  | 'BearishRetestHeld'
  | 'BullishRejection'
  | 'BearishRejection'
  | 'BullishDisplacement'
  | 'BearishDisplacement'
  | 'SellSideLiquiditySweep'
  | 'BuySideLiquiditySweep'
  | 'BullishCompressionBreakout'
  | 'BearishCompressionBreakout'
  | 'BullishImpulse'
  | 'BearishImpulse'
  | 'BullishPullback'
  | 'BearishPullback'

export type BreakRetestState =
  | 'None'
  | 'AwaitingRetest'
  | 'RetestInProgress'
  | 'RetestHeld'
  | 'RetestFailed'
  | 'Expired'

export interface PriceActionEvent {
  eventId: string
  type: PriceActionEventType
  direction: PriceActionDirection
  confirmedAt: string
  confirmedSequence: number
  referenceLevel: number | null
  brokenLevel: number | null
  retestLevel: number | null
  atr: number | null
  strength: number
  confidence: number
  sourceSwingKey: string | null
  sourceZoneKey: string | null
  reasonCode: string
  explanation: string
}

export interface PriceActionDiagnostic {
  candidate: string
  accepted: boolean
  reasonCode: string
  explanation: string
}

export interface BreakRetestSnapshot {
  setupId: string | null
  direction: PriceActionDirection
  state: BreakRetestState
  brokenLevel: number | null
  breakConfirmedAt: string | null
  breakSequence: number | null
  barsSinceBreak: number
  closestRetestDistanceAtr: number | null
  invalidReason: string | null
}

export interface PriceLegMetrics {
  direction: PriceActionDirection
  startedAt: string | null
  endedAt: string | null
  distance: number
  distanceAtr: number | null
  barCount: number
  efficiencyRatio: number
  retracementPercent: number
}

export interface PriceActionCalibrationSnapshot {
  sampleCount: number
  isReady: boolean
  isFrozen: boolean
  frozenAt: string | null
  medianBodyAtr: number
  medianRangeAtr: number
  medianWickToBodyRatio: number
  bodyAtr70: number
  rangeAtr70: number
  rangeAtr90: number
}

export type PriceActionSetupType =
  | 'BullishBreakRetestHold'
  | 'BearishBreakRetestHold'
  | 'BullishChoChRetestHold'
  | 'BearishChoChRetestHold'
  | 'BullishSweepDisplacement'
  | 'BearishSweepDisplacement'
  | 'BullishSweepChoCh'
  | 'BearishSweepChoCh'

export type PriceActionSetupPhase = 'Armed' | 'Triggered' | 'Invalidated' | 'Expired'

export interface PriceActionSetup {
  setupId: string
  type: PriceActionSetupType
  direction: PriceActionDirection
  phase: PriceActionSetupPhase
  armedAt: string
  triggeredAt?: string | null
  armedSequence: number
  triggeredSequence?: number | null
  confidence: number
  referenceLevel?: number | null
  entryReference?: number | null
  reasonCode: string
  explanation: string
  sourceEventIds: string[]
}

export interface PriceActionSnapshot {
  bias: PriceActionDirection
  bullishScore: number
  bearishScore: number
  events: PriceActionEvent[]
  diagnostics: PriceActionDiagnostic[]
  activeRetest: BreakRetestSnapshot
  latestLeg: PriceLegMetrics | null
  calibration: PriceActionCalibrationSnapshot
  /** Composite setups; may be absent on older replay files. */
  setups?: PriceActionSetup[]
}

export interface AdxAnalysisSnapshot {
  adx: number | null
  plusDi: number | null
  minusDi: number | null
  strengthDirection: MomentumDirection
  directionalBias: PriceActionDirection
  isTrendStrengthening: boolean
}

export type CciZone =
  | 'Unknown'
  | 'ExtremeNegative'
  | 'Negative'
  | 'Neutral'
  | 'Positive'
  | 'ExtremePositive'

export type CciRelationshipType =
  | 'None'
  | 'RegularBullishDivergence'
  | 'RegularBearishDivergence'
  | 'HiddenBullishDivergence'
  | 'HiddenBearishDivergence'
  | 'BullishConvergence'
  | 'BearishConvergence'

export interface CciRelationshipSnapshot {
  type: CciRelationshipType
  firstPivotTime: string
  secondPivotTime: string
  confirmedAt: string
  firstPrice: number
  secondPrice: number
  firstCci: number
  secondCci: number
  priceChange: number
  cciChange: number
  strength: number
  ageCandles: number
  isDivergence: boolean
}

export interface CciAnalysisSnapshot {
  zone: CciZone
  momentumDirection: MomentumDirection
  momentumChange: number | null
  previousValue: number | null
  crossedUpFromExtremeNegative: boolean
  crossedDownFromExtremePositive: boolean
  crossedUpZero: boolean
  crossedDownZero: boolean
  barsSinceExtremeNegative: number
  barsSinceExtremePositive: number
  latestRelationship: CciRelationshipSnapshot | null
  isNewRelationship: boolean
  sampleCount: number
}

export interface IndicatorSnapshot {
  atr: number | null
  rsi: number | null
  bollingerMiddle: number | null
  bollingerUpper: number | null
  bollingerLower: number | null
  cci?: number | null
  sma50?: number | null
  sma200?: number | null
  efficiencyRatio: number | null
  atrAnalysis?: AtrAnalysisSnapshot
  rsiAnalysis?: RsiAnalysisSnapshot
  cciAnalysis?: CciAnalysisSnapshot
  bollingerAnalysis?: BollingerAnalysisSnapshot
  adxAnalysis?: AdxAnalysisSnapshot
  efficiencyAnalysis?: EfficiencyAnalysisSnapshot
  donchian?: DonchianSnapshot
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

export type SupplyDemandZoneState = 'Forming' | 'ConfirmedFresh' | 'Approached' | 'Tested' | 'PartiallyMitigated' | 'Mitigated' | 'Invalidated' | 'Expired' | 'Merged'
export interface SupplyDemandZone {
  zoneId: string
  type: 'Demand' | 'Supply'
  pattern: string
  proximalPrice: number
  distalPrice: number
  baseStartedAt: string
  baseEndedAt: string
  departureStartedAt: string
  confirmedAt: string
  availableAt: string
  state: SupplyDemandZoneState
  touchCount: number
  penetrationRatio: number
  freshnessScore: number
  qualityScore: number
  brokeStructure: boolean
  hasFairValueGap: boolean
  boundaryMode: string
  profileHash: string
}

export interface SupplyDemandAnalysisSnapshot {
  isEnabled: boolean
  profileHash: string
  snapshotVersion: number
  availableAt: string
  zones?: SupplyDemandZone[]
  activeZones: SupplyDemandZone[]
  recentEvents: Array<{ zoneId: string; eventType: string; availableAt: string }>
}

export type LiquidityPoolState = 'Forming' | 'Active' | 'Approached' | 'Touched' | 'Swept' | 'Consumed' | 'AcceptedBreak' | 'Broken' | 'Expired' | 'Merged'
export interface LiquidityPool {
  poolId: string
  side: 'BuySide' | 'SellSide'
  type: string
  lowerPrice: number
  upperPrice: number
  referencePrice: number
  originatedAt: string
  confirmedAt: string
  availableAt: string
  state: LiquidityPoolState
  sourcePointCount: number
  touchCount: number
  equalnessScore: number
  visibilityScore: number
  compressionScore: number
  prominenceScore: number
  freshnessScore: number
  qualityScore: number
  profileHash: string
}

export interface LiquiditySweepEvent {
  sweepId: string
  poolId: string
  sweepStartedAt: string
  confirmedAt: string
  availableAt: string
  extremePrice: number
  penetrationAtr: number
  closedBackInside: boolean
  displacementConfirmed: boolean
  structureShiftConfirmed: boolean
  rejectionStrength: number
  qualityScore: number
}

export interface LiquidityAnalysisSnapshot {
  isEnabled: boolean
  profileHash: string
  snapshotVersion: number
  availableAt: string
  pools?: LiquidityPool[]
  activePools: LiquidityPool[]
  recentEvents: Array<{ poolId: string; eventType: string; availableAt: string; price: number }>
  recentSweeps?: LiquiditySweepEvent[]
}

export interface SupplyDemandLiquidityConfluenceSnapshot {
  isEnabled: boolean
  snapshotVersion: number
  availableAt: string
  relationships: Array<{
    confluenceId: string
    zoneId: string
    poolId?: string | null
    sweepId?: string | null
    direction: 'Bullish' | 'Bearish'
    qualityScore: number
    availableAt: string
  }>
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

export type MarketRegimeValue =
  | 'Unknown'
  | 'TrendingUp'
  | 'TrendingDown'
  | 'Range'
  | 'Compression'
  | 'BreakoutExpansionUp'
  | 'BreakoutExpansionDown'
  | 'HighVolatilityDisorder'
  | 'IlliquidUnsafe'

export interface RegimeContribution {
  rule: string
  score: number
  explanation: string
}

export interface MarketRegimeSnapshot {
  regime: MarketRegimeValue
  confidence: number
  confirmedAt: string
  ageCandles: number
  contributions: RegimeContribution[]
  reasonCode: string
  isTradeable: boolean
}


export type NeoWaveDirection = 'Neutral' | 'Up' | 'Down'
export type NeoWavePatternType =
  | 'Unknown'
  | 'TrendSequence'
  | 'ImpulseCandidate'
  | 'ZigZagCorrection'
  | 'FlatCorrection'
  | 'TriangleCorrection'
  | 'ComplexCorrection'
export type NeoWaveHypothesisStatus = 'Possible' | 'Confirmed' | 'Preferred' | 'Invalidated'

export interface MonoWave {
  waveId: string
  startTime: string
  endTime: string
  startConfirmedAt: string
  endConfirmedAt: string
  availableAt: string
  startPrice: number
  endPrice: number
  direction: NeoWaveDirection
  priceLength: number
  timeLength: string
  lengthAtr?: number | null
  isConfirmed: boolean
}

export interface ProvisionalMonoWave {
  waveId: string
  startTime: string
  currentTime: string
  availableAt: string
  startPrice: number
  currentPrice: number
  direction: NeoWaveDirection
  priceLength: number
  lengthAtr?: number | null
}

export interface NeoWaveInvalidationCondition {
  comparison: 'None' | 'Below' | 'Above'
  price?: number | null
  description: string
}

export interface NeoWaveHypothesis {
  hypothesisId: string
  patternType: NeoWavePatternType
  direction: NeoWaveDirection
  degree: 'Micro' | 'Minor' | 'Intermediate' | 'Primary'
  componentWaveIds: string[]
  status: NeoWaveHypothesisStatus
  structuralScore: number
  maturity: number
  availableAt: string
  supportingRuleIds: string[]
  violatedRuleIds: string[]
  invalidation: NeoWaveInvalidationCondition
}

export interface NeoWaveQuality {
  isReady: boolean
  confirmedSwingCount: number
  confirmedMonoWaveCount: number
  hypothesisCount: number
  prunedHypothesisCount: number
  reasonCode: string
}

export interface NeoWaveSnapshot {
  enabled: boolean
  availableAt: string
  confirmedMonoWaves: MonoWave[]
  provisionalWave?: ProvisionalMonoWave | null
  hypotheses: NeoWaveHypothesis[]
  preferredHypothesisId?: string | null
  structuralBias: NeoWaveDirection
  structuralScore: number
  maturity: number
  conflictScore: number
  invalidationPrice?: number | null
  invalidationDistanceAtr?: number | null
  reasonCodes: string[]
  quality: NeoWaveQuality
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
  priceAction?: boolean
  bollinger: boolean
  bollingerRegimes: boolean
  movingAverages: boolean
  cci: boolean
  rsi?: boolean
  rsiRelationships: boolean
  atr: boolean
  volume: boolean
  swings: boolean
  zones: boolean
  supplyDemand?: boolean
  liquidity?: boolean
  trendlines: boolean
  channels: boolean
  donchian: boolean
  efficiencyRatio: boolean
  marketRegime: boolean
  neoWave: boolean
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

/**
 * A historical window of analysed candles centred on one anchor candle, from
 * `GET /api/workspaces/oanda/window`. Every frame is fully warmed up server-side, so unlike a
 * client-resampled series these carry real indicators and can be annotated.
 */
export interface WorkspaceWindowSnapshot {
  status: LiveFeedStatus
  dataset: ReplayDataset
  interval: string
  requestedAnchorAt: string
  /** The candle the anchor actually resolved to — at or before the request, never after. */
  resolvedAnchorAt: string
  /** Index of the anchor inside `dataset.series[0].frames`; not always `beforeCount`. */
  anchorIndex: number
  beforeCount: number
  afterCount: number
  warmupCandles: number
  /** False when history ran out before the full warm-up span — earliest indicators are less settled. */
  warmupSatisfied: boolean
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

export interface SimulationInstrumentOption {
  symbol: string
  displayName: string
  instrument: string
  assetClass: string
}

export interface SimulationBrokerOption {
  id: string
  displayName: string
  environment: string
  sourceKind: string
  isAvailable: boolean
  requiresCredentials: boolean
  description: string
  supportedExecutionIntervals: string[]
  instruments: SimulationInstrumentOption[]
}

export interface SimulationBrokerCatalog {
  generatedAt: string
  brokers: SimulationBrokerOption[]
  warning?: string | null
}
