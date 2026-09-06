<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import type {
  ChartLayers,
  ImportedDatasetMetadata,
  PromoteTradingPolicyRequest,
  ReplayFrame,
  ReplayTrade,
  ResearchArtifactSelection,
  SimulationBrokerCatalog,
  SimulationBrokerOption,
  SimulationInstrumentOption,
  SimulationJobSnapshot,
  StrategyProgressSnapshot,
  TradingPolicyProfile,
} from '../types'
import { RESEARCH_ARTIFACT_SELECTION_KEY } from '../types'
import AnalysisChart from './AnalysisChart.vue'
import { useMultiTimeframeSelection } from '../composables/useMultiTimeframeSelection'
import { findAnchorIndex } from '../utils/timeframeSeries'
import SimulationExperimentPanel from './SimulationExperimentPanel.vue'
import ProfileBuilderWizard from './simulator/ProfileBuilderWizard.vue'
import AlfonsoTrendComparison from './AlfonsoTrendComparison.vue'
import { useSimulationRealtime } from '../composables/useSimulationRealtime'
import { useSimulationPlayback, type PlaybackRow } from '../composables/useSimulationPlayback'
import { useSimulationProfiles } from '../composables/useSimulationProfiles'
import type { SimulationStrategyProfile } from '../types/simulation-experiments'

const emit = defineEmits<{
  (event: 'open-experiment-report', experimentId: string): void
}>()

const simulatorMode = ref<'single' | 'experiment' | 'trend'>(
  window.location.hash === '#alfonso-trends' ? 'trend' : 'single',
)

/** Full UTC calendar months ending at the start of the current month. */
function evaluationWindowMonths(months: number): { from: string; to: string } {
  const now = new Date()
  const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1))
  const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - months, 1))
  return {
    from: from.toISOString().slice(0, 10),
    to: to.toISOString().slice(0, 10),
  }
}

type SimulationFormState = ReturnType<typeof createBaseSimulationForm>
type PositionManagementForm = SimulationFormState['legacyPositionManagement']

interface SimulationPresetDefinition {
  id: string
  name: string
  badge: string
  description: string
  build: () => SimulationFormState
}

/**
 * Shared baseline form. Presets patch this rather than reinventing every module field.
 */
function createBaseSimulationForm() {
  const window = evaluationWindowMonths(1)
  return {
    brokerId: 'oanda',
    instrument: 'FX:EUR/USD',
    from: window.from,
    to: window.to,
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
    // Opt-in unified surface (PROJECT_STATE.md §4b, Phase 2): role=interval[:influence[:priority]]
    // spec, same DSL as BacktestRunner's --timeframes CLI flag. Blank (the default) leaves every
    // field above in full control, exactly as before this field existed. When non-blank, applies
    // role-by-role on top of the fields above - a role the spec doesn't mention stays on its own
    // field's value.
    timeframesOverride: '',
    strategies: 'legacy,improved',
    // §7 multi-instrument portfolio clock: when enabled, strategyAssignments replaces
    // `strategies` + the single top-level `instrument` entirely - each row trades its
    // own instrument on one shared clock instead of every strategy trading `instrument`.
    strategyAssignmentsEnabled: false,
    strategyAssignments: [
      { strategyType: 'legacy', instrument: '' },
      { strategyType: 'improved', instrument: '' },
    ] as Array<{ strategyType: string; instrument: string }>,
    startingBalance: 100_000,
    dailyEquityProfitTarget: 0,
    dailyEquityGivebackActivation: 0,
    maximumDailyEquityGiveback: 0,
    equityProtectionEnabled: false,
    equityProtectionActivationProfitPercent: 0,
    equityProtectionMaxGivebackPercent: 0,
    equityProtectionActionType: 'PauseNewEntries',
    equityProtectionReductionFraction: 0.25,
    equityProtectionFutureRiskMultiplier: 0.5,
    equityProtectionRecoveryBars: 3,
    quantity: 1_000,
    positionSizingMode: 'FixedFractionalRisk',
    fixedCashRisk: 250,
    riskPercentOfEquity: 0.25,
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
    warmupDays: 21,
    strategyExecutionMode: 'ParallelWorkers',
    ambiguousIntrabarPolicy: 'ConservativeStopFirst',
    accountMode: 'IndependentStrategyAccounts',
    regimeEnabled: false,
    efficiencyRatioPeriod: 14,
    regimeConfirmationBars: 2,
    regimePersistenceBars: 3,
    regimeSoftSpreadAtr: 0.25,
    regimeHardSpreadAtr: 0.50,
    neoWaveEnabled: true,
    neoWaveEvidenceMode: 'RecordOnly',
    neoWaveEvidenceInterval: '2h',
    neoWaveMinimumStructuralScore: 60,
    neoWaveMaximumTrustedConflictScore: 40,
    neoWaveMinimumRiskMultiplier: 0.75,
    neoWaveMaximumConflictRiskReduction: 0.25,
    tradingConditionsEnabled: true,
    allowedSessions: 'Asian,London,NewYork,LondonNewYorkOverlap',
    rolloverBlackoutMinutesBefore: 15,
    rolloverBlackoutMinutesAfter: 15,
    conditionSoftSpreadAtr: 0.25,
    conditionHardSpreadAtr: 0.50,
    economicEventFilterEnabled: false,
    maximumTotalPortfolioHeatPercent: 1.5,
    maximumPendingRiskPercent: 0.75,
    maximumStrategyRiskPercent: 0.75,
    maximumInstrumentRiskPercent: 0.75,
    maximumCurrencyStopRiskPercent: 0.75,
    minimumUnallocatedMarginReservePercent: 30,
    maximumOpenPositions: 3,
    correlationLookbackBars: 120,
    correlationMinimumSamples: 60,
    correlationSoftThreshold: 0.5,
    correlationHardThreshold: 0.75,
    maximumNetCurrencyExposurePercent: 300,
    maximumGrossCurrencyExposurePercent: 300,
    valueLocationEvidenceEnabled: false,
    valueLocationNearAtrThreshold: 0.5,
    valueLocationStretchedAtrThreshold: 2.5,
    valueLocationConfidenceAdjustment: 3,
    currencyStrengthEnabled: false,
    currencyStrengthInterval: '1h',
    currencyStrengthReturnLookbackBars: 12,
    currencyStrengthVolatilityLookbackBars: 120,
    currencyStrengthMinimumCoveragePercent: 60,
    adaptiveRiskEnabled: false,
    // Research calibration artifacts (GUIDs from Research panel / localStorage).
    // When empty and autoCalibrateBeforeRun is true, the server trains setup→meta→management
    // on a past window (default 2 months + 10d embargo) before the sim starts.
    autoCalibrateBeforeRun: true,
    autoCalibrateTrainMonths: 2,
    autoCalibrateEmbargoDays: 10,
    setupCalibrationArtifactId: '',
    managementCalibrationArtifactId: '',
    metaModelArtifactId: '',
    executionFillModel: 'MidpointPlusConfiguredSpread',
    stressExecutionScenario: 'Base',
    maximumFillQuantityPerFrame: 0,
    maximumFillParticipationFraction: 1,
    asianSessionSpreadMultiplier: 1.2,
    rolloverSpreadMultiplier: 3,
    volatilitySlippageFraction: 0,
    gapSlippageFraction: 0,
    financingEnabled: false,
    financingLongAnnualPercent: 0,
    financingShortAnnualPercent: 0,
    refreshCache: false,
    noCache: false,
    // Chart replay costs real throughput (~1.5x candles/second when off vs on in local
    // benchmarks) - on by default so the replay chart still works out of the box, but a run
    // that only needs metrics/trades can turn it off for a meaningfully faster single run.
    captureMarketReplay: true,
    legacyPositionManagement: {
      mode: 'StructureAtr',
      managementInterval: '15m',
      evaluateMechanicalProtectionOnEveryExecutionFrame: true,
      fastStructureInterval: '5m',
      mainStructureInterval: '15m',
      thesisInterval: '1h',
      breakEvenActivationR: 1,
      structureTrailActivationR: 1.5,
      atrBufferMultiplier: 0.25,
      minimumStopImprovementAtr: 0.05,
      exitOnAdverseStructureBreak: false,
      enableNeoWaveInvalidationExit: false,
      neoWaveInvalidationBufferAtr: 0.1,
      preserveBracketTarget: false,
      enableScaleOut: true,
      minimumRunnerFraction: 0.4,
      enableProfitFloor: true,
      enableMaximumGiveback: true,
      enableStagnationReduction: true,
      enableStructuralDeteriorationReduction: true,
      enableMomentumDecayReduction: true,
      enableVolatilityExhaustionReduction: true,
      enableRiskWindowReduction: false,
      riskWindowStartUtc: '21:45',
      riskWindowEndUtc: '22:15',
      enableExecutionCostStressReduction: false,
    },
    improvedPositionManagement: {
      mode: 'StructureAtr',
      managementInterval: '15m',
      evaluateMechanicalProtectionOnEveryExecutionFrame: true,
      fastStructureInterval: '5m',
      mainStructureInterval: '15m',
      thesisInterval: '1h',
      breakEvenActivationR: 1,
      structureTrailActivationR: 2,
      atrBufferMultiplier: 0.25,
      minimumStopImprovementAtr: 0.05,
      exitOnAdverseStructureBreak: false,
      enableNeoWaveInvalidationExit: false,
      neoWaveInvalidationBufferAtr: 0.1,
      preserveBracketTarget: true,
      enableScaleOut: true,
      minimumRunnerFraction: 0.5,
      enableProfitFloor: true,
      enableMaximumGiveback: true,
      enableStagnationReduction: true,
      enableStructuralDeteriorationReduction: true,
      enableMomentumDecayReduction: true,
      enableVolatilityExhaustionReduction: true,
      enableRiskWindowReduction: false,
      riskWindowStartUtc: '21:45',
      riskWindowEndUtc: '22:15',
      enableExecutionCostStressReduction: false,
    },
  }
}

function patchSimulationForm(
  patches: Partial<Omit<SimulationFormState, 'legacyPositionManagement' | 'improvedPositionManagement'>> & {
    legacyPositionManagement?: Partial<PositionManagementForm>
    improvedPositionManagement?: Partial<PositionManagementForm>
  } = {},
): SimulationFormState {
  const next = createBaseSimulationForm()
  const {
    legacyPositionManagement,
    improvedPositionManagement,
    ...rest
  } = patches
  Object.assign(next, rest)
  if (legacyPositionManagement) Object.assign(next.legacyPositionManagement, legacyPositionManagement)
  if (improvedPositionManagement) Object.assign(next.improvedPositionManagement, improvedPositionManagement)
  return next
}

/** @deprecated Prefer createBaseSimulationForm / presets; kept as alias for clarity. */
function createRecommendedSimulationForm() {
  return createBaseSimulationForm()
}

const simulationPresets: SimulationPresetDefinition[] = [
  {
    id: 'research',
    name: 'Research baseline',
    badge: 'Default',
    description: 'Balanced dual-strategy research stack with modules off so the equity curve is unconstrained.',
    build: () => createBaseSimulationForm(),
  },
  {
    id: 'day-trading',
    name: 'Day trading',
    badge: 'Intraday',
    description: 'Intraday progressive stack (1h→30m→15m→5m), London/NY sessions, quicker management. Regime routing off so compression hours do not zero the sample.',
    build: () => patchSimulationForm({
      ...evaluationWindowMonths(1),
      // Progressive agents need unique intervals ordered entry < confirm < setup/secondary < trend.
      // Entry is 5m (not 1m): MaximumEntryCandles defaults to 12 → ~1 hour fill window.
      // With 1m entry that window is only 12 minutes and almost never fills.
      analysisIntervals: '1m,5m,15m,30m,1h',
      trendInterval: '1h',
      secondaryTrendIntervals: '',
      setupIntervals: '30m',
      confirmationInterval: '15m',
      additionalConfirmationIntervals: '',
      entryInterval: '5m',
      minimumSecondaryTrendAlignments: 0,
      minimumSetupAlignments: 1,
      minimumConfirmationAlignments: 1,
      riskPercentOfEquity: 0.35,
      fixedCashRisk: 200,
      minimumRewardRisk: 1.2,
      priceActionConfirmation: 'Soft',
      minimumPriceActionConfidence: 50,
      warmupDays: 14,
      // Classification can stay available via advanced, but routing blocks Compression /
      // IlliquidUnsafe / low-confidence regimes and often produces zero-trade months.
      regimeEnabled: false,
      tradingConditionsEnabled: true,
      allowedSessions: 'London,NewYork,LondonNewYorkOverlap',
      rolloverBlackoutMinutesBefore: 20,
      rolloverBlackoutMinutesAfter: 20,
      maximumOpenPositions: 4,
      maximumTotalPortfolioHeatPercent: 2,
      maximumPendingRiskPercent: 1,
      maximumStrategyRiskPercent: 1,
      maximumInstrumentRiskPercent: 1,
      maximumCurrencyStopRiskPercent: 1,
      // Drawdown/volatility risk reduction is one-directional (never scales risk up,
      // only down at 2%/4%/6% equity drawdown and in the top volatility percentiles),
      // so it's a strict downside safety net worth having on for any live-like profile.
      adaptiveRiskEnabled: true,
      // Soft (confidence-nudge-only, never a veto) pullback/rejection/stretch evidence
      // against the session-anchored value reference - a natural fit for intraday
      // pullback entries. Defaults are fine at this timeframe.
      valueLocationEvidenceEnabled: true,
      legacyPositionManagement: {
        fastStructureInterval: '5m',
        mainStructureInterval: '15m',
        thesisInterval: '1h',
        managementInterval: '5m',
        breakEvenActivationR: 0.75,
        structureTrailActivationR: 1.25,
        minimumRunnerFraction: 0.3,
        atrBufferMultiplier: 0.2,
        minimumStopImprovementAtr: 0.03,
        enableRiskWindowReduction: true,
        enableExecutionCostStressReduction: true,
      },
      improvedPositionManagement: {
        fastStructureInterval: '5m',
        mainStructureInterval: '15m',
        thesisInterval: '1h',
        managementInterval: '5m',
        breakEvenActivationR: 0.75,
        structureTrailActivationR: 1.5,
        minimumRunnerFraction: 0.35,
        atrBufferMultiplier: 0.2,
        minimumStopImprovementAtr: 0.03,
        enableRiskWindowReduction: true,
        enableExecutionCostStressReduction: true,
      },
    }),
  },
  {
    id: 'swing-trading',
    name: 'Swing trading',
    badge: 'Multi-day',
    description: 'Higher-timeframe structure, wider R targets, longer warm-up, financing on, patient runners.',
    build: () => patchSimulationForm({
      ...evaluationWindowMonths(3),
      analysisIntervals: '15m,1h,2h,4h,1d',
      trendInterval: '1d',
      secondaryTrendIntervals: '4h',
      setupIntervals: '2h',
      confirmationInterval: '1h',
      additionalConfirmationIntervals: '',
      entryInterval: '15m',
      minimumSecondaryTrendAlignments: 1,
      minimumSetupAlignments: 1,
      minimumConfirmationAlignments: 1,
      riskPercentOfEquity: 0.5,
      fixedCashRisk: 400,
      minimumRewardRisk: 2,
      minimumPriceActionConfidence: 55,
      warmupDays: 45,
      regimeEnabled: true,
      tradingConditionsEnabled: true,
      allowedSessions: 'Asian,London,NewYork,LondonNewYorkOverlap',
      maximumOpenPositions: 2,
      maximumTotalPortfolioHeatPercent: 1.25,
      maximumPendingRiskPercent: 0.6,
      maximumStrategyRiskPercent: 0.6,
      maximumInstrumentRiskPercent: 0.6,
      maximumCurrencyStopRiskPercent: 0.6,
      // Multi-day holds carry real notional exposure risk beyond planned stop-loss -
      // cap it explicitly rather than relying on the very generous 300%/300% base default.
      maximumNetCurrencyExposurePercent: 150,
      maximumGrossCurrencyExposurePercent: 200,
      financingEnabled: true,
      financingLongAnnualPercent: 2.5,
      financingShortAnnualPercent: -0.5,
      adaptiveRiskEnabled: true,
      // Multi-day swings hold through session/week anchors long enough for value
      // location to matter - wider thresholds than intraday since 4h/1d ATR swings
      // are proportionally larger.
      valueLocationEvidenceEnabled: true,
      valueLocationNearAtrThreshold: 0.75,
      valueLocationStretchedAtrThreshold: 3,
      legacyPositionManagement: {
        fastStructureInterval: '15m',
        mainStructureInterval: '1h',
        thesisInterval: '4h',
        managementInterval: '1h',
        breakEvenActivationR: 1.25,
        structureTrailActivationR: 2.5,
        minimumRunnerFraction: 0.5,
        atrBufferMultiplier: 0.3,
        minimumStopImprovementAtr: 0.08,
        preserveBracketTarget: true,
      },
      improvedPositionManagement: {
        fastStructureInterval: '15m',
        mainStructureInterval: '1h',
        thesisInterval: '4h',
        managementInterval: '1h',
        breakEvenActivationR: 1.5,
        structureTrailActivationR: 3,
        minimumRunnerFraction: 0.55,
        atrBufferMultiplier: 0.3,
        minimumStopImprovementAtr: 0.08,
        preserveBracketTarget: true,
      },
    }),
  },
  {
    id: 'scalping',
    name: 'Scalping',
    badge: 'High frequency',
    description: 'Short progressive stack (30m→15m→5m→1m), soft PA, sessions on, regime off. Expect fewer fills than research — 1m entry windows are short.',
    build: () => patchSimulationForm({
      ...evaluationWindowMonths(1),
      analysisIntervals: '1m,5m,15m,30m',
      trendInterval: '30m',
      secondaryTrendIntervals: '15m',
      setupIntervals: '',
      confirmationInterval: '5m',
      additionalConfirmationIntervals: '',
      entryInterval: '1m',
      minimumSecondaryTrendAlignments: 0,
      minimumSetupAlignments: 0,
      minimumConfirmationAlignments: 1,
      riskPercentOfEquity: 0.15,
      fixedCashRisk: 100,
      minimumRewardRisk: 1,
      priceActionConfirmation: 'Soft',
      minimumPriceActionConfidence: 45,
      warmupDays: 10,
      // Keep routing off; 1m progressive entries are already sparse.
      regimeEnabled: false,
      tradingConditionsEnabled: true,
      allowedSessions: 'London,NewYork,LondonNewYorkOverlap',
      rolloverBlackoutMinutesBefore: 30,
      rolloverBlackoutMinutesAfter: 30,
      maximumOpenPositions: 5,
      maximumTotalPortfolioHeatPercent: 1.75,
      maximumPendingRiskPercent: 0.9,
      maximumStrategyRiskPercent: 0.9,
      maximumInstrumentRiskPercent: 0.9,
      maximumCurrencyStopRiskPercent: 0.9,
      // High trade frequency means a losing streak compounds fast - downside-only
      // drawdown/volatility risk reduction matters more here than anywhere else.
      adaptiveRiskEnabled: true,
      // Tighter thresholds and a smaller nudge than the intraday/swing profiles:
      // 1m signals are noisier, so weight this evidence more conservatively.
      valueLocationEvidenceEnabled: true,
      valueLocationNearAtrThreshold: 0.3,
      valueLocationStretchedAtrThreshold: 2,
      valueLocationConfidenceAdjustment: 2,
      spreadBasisPoints: 1.2,
      slippageBasisPoints: 0.8,
      legacyPositionManagement: {
        fastStructureInterval: '1m',
        mainStructureInterval: '5m',
        thesisInterval: '15m',
        managementInterval: '1m',
        breakEvenActivationR: 0.4,
        structureTrailActivationR: 0.8,
        minimumRunnerFraction: 0.2,
        atrBufferMultiplier: 0.15,
        minimumStopImprovementAtr: 0.02,
        enableScaleOut: true,
        enableRiskWindowReduction: true,
        enableExecutionCostStressReduction: true,
      },
      improvedPositionManagement: {
        fastStructureInterval: '1m',
        mainStructureInterval: '5m',
        thesisInterval: '15m',
        managementInterval: '1m',
        breakEvenActivationR: 0.5,
        structureTrailActivationR: 1,
        minimumRunnerFraction: 0.25,
        atrBufferMultiplier: 0.15,
        minimumStopImprovementAtr: 0.02,
        enableScaleOut: true,
        enableRiskWindowReduction: true,
        enableExecutionCostStressReduction: true,
      },
    }),
  },
  {
    id: 'risk-managed',
    name: 'Risk-managed',
    badge: 'Live-like',
    description: 'Research MTF with daily profit locks, equity protection, portfolio heat/currency exposure caps, correlation, value-location evidence, and adaptive sizing enabled - the most conservative, fully-instrumented profile.',
    build: () => patchSimulationForm({
      ...evaluationWindowMonths(1),
      riskPercentOfEquity: 0.2,
      fixedCashRisk: 200,
      minimumRewardRisk: 1.5,
      warmupDays: 21,
      regimeEnabled: true,
      tradingConditionsEnabled: true,
      // Regime + trading-condition gating already cover the thinnest-liquidity hours;
      // restricting sessions further on top of that just starves the sample, so this
      // stays on the base default (all sessions) unlike day-trading/scalping.
      adaptiveRiskEnabled: true,
      dailyEquityProfitTarget: 1500,
      dailyEquityGivebackActivation: 1000,
      maximumDailyEquityGiveback: 400,
      equityProtectionEnabled: true,
      equityProtectionActivationProfitPercent: 3,
      equityProtectionMaxGivebackPercent: 1.5,
      equityProtectionActionType: 'PauseNewEntries',
      equityProtectionReductionFraction: 0.25,
      equityProtectionFutureRiskMultiplier: 0.5,
      equityProtectionRecoveryBars: 5,
      maximumOpenPositions: 2,
      maximumTotalPortfolioHeatPercent: 1,
      maximumPendingRiskPercent: 0.5,
      maximumStrategyRiskPercent: 0.5,
      maximumInstrumentRiskPercent: 0.5,
      maximumCurrencyStopRiskPercent: 0.5,
      // Real notional currency-exposure caps, distinct from the stop-risk heat above -
      // tightest of any profile, matching its "live-like" conservative intent.
      maximumNetCurrencyExposurePercent: 100,
      maximumGrossCurrencyExposurePercent: 150,
      correlationSoftThreshold: 0.45,
      correlationHardThreshold: 0.7,
      minimumUnallocatedMarginReservePercent: 40,
      maximumAccountMarginUsagePercent: 25,
      maximumSinglePositionMarginPercent: 8,
      // Soft confirmation evidence layered on top of everything else here - never a
      // veto, just a small confidence nudge, consistent with this profile's "every
      // available real signal, conservatively weighted" design.
      valueLocationEvidenceEnabled: true,
    }),
  },
]

const form = reactive(createBaseSimulationForm())
const selectedPresetId = ref(simulationPresets[0]!.id)
const selectedSimulationProfileKey = ref('')
const {
  profiles: simulationProfiles,
  loading: simulationProfilesLoading,
  error: simulationProfilesError,
  refresh: refreshSimulationProfiles,
} = useSimulationProfiles()
const showAdvancedConfig = ref(false)
const selectedPreset = computed(() =>
  simulationPresets.find((preset) => preset.id === selectedPresetId.value) ?? simulationPresets[0]!,
)
const selectedSimulationProfile = computed(() => simulationProfiles.value.find(
  (profile) => simulationProfileKey(profile) === selectedSimulationProfileKey.value,
) ?? null)

function simulationProfileKey(profile: SimulationStrategyProfile): string {
  return `${profile.profileId}:${profile.revision}`
}

function simulationProfileStrategy(profile: SimulationStrategyProfile): string {
  switch (profile.agent.kind) {
    case 'LegacyProgressive': return 'legacy-progressive'
    case 'ImprovedProgressive': return 'improved-progressive'
    case 'StructuralConfluence': return 'structural-confluence'
  }
}

function simulationProfileKind(profile: SimulationStrategyProfile): string {
  return profile.agent.kind.replace(/([a-z])([A-Z])/g, '$1 $2')
}

function applySimulationProfileSelection(): void {
  const profile = selectedSimulationProfile.value
  if (!profile) return
  form.instrument = profile.instrument.value
  form.strategies = simulationProfileStrategy(profile)
  form.strategyAssignmentsEnabled = false
  error.value = null
}

const showProfileBuilder = ref(false)

async function onProfileBuilt(profile: SimulationStrategyProfile): Promise<void> {
  showProfileBuilder.value = false
  await refreshSimulationProfiles()
  selectedSimulationProfileKey.value = simulationProfileKey(profile)
  applySimulationProfileSelection()
}

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
const maxChartRows = 750
const replayLoadsInFlight = new Set<string>()
// §7 multi-instrument portfolio clock: rows from different instruments interleave in
// one sequence-ordered stream, so the chart needs to display only one instrument's
// candles at a time rather than a mixed, meaningless price series.
const selectedChartInstrument = ref<string | null>(null)
const chartInstruments = computed(() => {
  const seen = new Set<string>()
  for (const row of replayRows.value) if (row.instrument) seen.add(row.instrument)
  return [...seen].sort()
})
const filteredReplayRows = computed(() => {
  if (chartInstruments.value.length <= 1) return replayRows.value
  const selected = selectedChartInstrument.value ?? chartInstruments.value[0]
  return replayRows.value.filter((row) => !row.instrument || row.instrument === selected)
})

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
} = useSimulationPlayback(filteredReplayRows)

const activeStrategies = computed(() => job.value?.strategies ?? [])
const flatTrades = computed(() => trades.value.map((payload) => payload.trade))

/** SignalR historically sent enums as integers (e.g. 8 = Cancelled). Map for display. */
const simulationStatusNames = [
  'Queued',
  'PreparingData',
  'DownloadingData',
  'LoadingCache',
  'WarmingUp',
  'Running',
  'Paused',
  'Cancelling',
  'Cancelled',
  'Exporting',
  'Completed',
  'Failed',
] as const

function formatSimulationStatus(status: string | number | null | undefined): string {
  if (status == null || status === '') return '—'
  if (typeof status === 'number' && status >= 0 && status < simulationStatusNames.length) {
    return simulationStatusNames[status]
  }
  if (typeof status === 'string' && /^\d+$/.test(status)) {
    const index = Number(status)
    if (index >= 0 && index < simulationStatusNames.length) {
      return simulationStatusNames[index]
    }
  }
  return String(status)
}

const displayJobStatus = computed(() => formatSimulationStatus(job.value?.status))
const jobIsComplete = computed(() =>
  Boolean(job.value?.isComplete) ||
  ['Completed', 'Failed', 'Cancelled'].includes(displayJobStatus.value),
)
const canPauseCompute = computed(() =>
  ['PreparingData', 'DownloadingData', 'LoadingCache', 'WarmingUp', 'Running', 'Exporting']
    .includes(displayJobStatus.value),
)
const canResumeCompute = computed(() => displayJobStatus.value === 'Paused')
const canCancelCompute = computed(() => !jobIsComplete.value && displayJobStatus.value !== 'Cancelling')
const jobFailureMessage = computed(() => {
  if (displayJobStatus.value !== 'Failed') return null
  return job.value?.error?.trim() || 'The simulation failed without a detailed error message.'
})
const strategyFailures = computed(() =>
  (job.value?.strategies ?? [])
    .map((strategy) => {
      const detail = strategy.lastError?.trim()
      if (!detail) return null
      return `${strategy.strategyName}: ${detail}`
    })
    .filter((item): item is string => item != null),
)

/** Warm-up builds indicators/structure; trading only starts at the evaluation From date. */
const evaluationPhase = computed(() => {
  const current = job.value
  if (!current) return null
  const status = formatSimulationStatus(current.status)
  const marketMs = current.currentMarketTime ? Date.parse(current.currentMarketTime) : Number.NaN
  const fromMs = Date.parse(current.requestedFrom)
  const toMs = Date.parse(current.requestedTo)
  if (Number.isFinite(marketMs) && Number.isFinite(fromMs) && marketMs < fromMs) {
    return 'Warm-up (downloading/processing history; no trading yet)'
  }
  if (status === 'WarmingUp') {
    return 'Warm-up (no trading yet)'
  }
  if (Number.isFinite(marketMs) && Number.isFinite(toMs) && marketMs >= toMs) {
    return 'Past evaluation window'
  }
  if (status === 'Running' || status === 'Paused' || status === 'Exporting' || status === 'Completed') {
    return 'Evaluation window (entries allowed)'
  }
  if (status === 'Cancelling' || status === 'Cancelled') {
    return 'Cancelled — warm-up/evaluation stopped early'
  }
  if (status === 'Failed') {
    return 'Failed — see error below; start a new run to try again'
  }
  return status
})

const dataWindowSummary = computed(() => {
  const current = job.value
  if (!current) return null
  const evalFrom = current.requestedFrom?.slice(0, 10) ?? '—'
  const evalTo = current.requestedTo?.slice(0, 10) ?? '—'
  const warm = current.warmupFrom?.slice(0, 10)
  if (warm && warm !== evalFrom) {
    return `Data ${warm} → ${evalTo} (warm-up ${warm} → ${evalFrom}, trade ${evalFrom} → ${evalTo})`
  }
  return `Evaluation ${evalFrom} → ${evalTo}`
})

const formDataWindowHint = computed(() => {
  if (!form.from || form.warmupDays <= 0) {
    return `Data window matches evaluation (${form.from || '—'} → ${form.to || '—'}).`
  }
  const fromDate = new Date(`${form.from}T00:00:00Z`)
  if (Number.isNaN(fromDate.getTime())) {
    return 'Warm-up history is loaded before the evaluation From date; no trades during warm-up.'
  }
  const warm = new Date(fromDate)
  warm.setUTCDate(warm.getUTCDate() - form.warmupDays)
  const warmIso = warm.toISOString().slice(0, 10)
  return `With ${form.warmupDays} warm-up days, candles load from ${warmIso} (history only). Trading starts ${form.from}.`
})

const activePresetSummary = computed(() => {
  const sizing = form.positionSizingMode === 'FixedFractionalRisk'
    ? `${form.riskPercentOfEquity}% equity / trade`
    : form.positionSizingMode === 'FixedCashRisk'
      ? `${form.fixedCashRisk} cash risk`
      : `${form.quantity} fixed units`
  const modules = [
    form.regimeEnabled ? 'regime' : null,
    form.tradingConditionsEnabled ? 'sessions' : null,
    form.adaptiveRiskEnabled ? 'adaptive risk' : null,
    form.equityProtectionEnabled ? 'equity protection' : null,
    form.dailyEquityProfitTarget > 0 || form.dailyEquityGivebackActivation > 0 ? 'daily lock' : null,
    form.financingEnabled ? 'financing' : null,
  ].filter(Boolean)
  return [
    selectedPreset.value.name,
    `${form.instrument}`,
    `${form.from} → ${form.to}`,
    `MTF ${form.trendInterval}/${form.secondaryTrendIntervals || '—'}/${form.setupIntervals || '—'}/${form.confirmationInterval}/${form.entryInterval}`,
    `${form.strategies}`,
    sizing,
    `min R:R ${form.minimumRewardRisk}`,
    `PA ${form.priceActionConfirmation}`,
    `${form.warmupDays}d warm-up`,
    modules.length ? `modules: ${modules.join(', ')}` : 'modules: off',
    form.precisionMode,
  ].join(' · ')
})

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
const replayFrames = computed<ReplayFrame[]>(() => filteredReplayRows.value.map((row, index) => ({
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
    efficiencyRatio: null,
  },
  swings: row.analysis?.swings ?? [],
  priceZones: row.analysis?.priceZones ?? [],
  trendlines: row.analysis?.trendlines ?? [],
  channels: row.analysis?.channels ?? [],
  marketStructure: row.analysis?.marketStructure,
  priceAction: row.analysis?.priceAction,
  marketRegime: row.analysis?.marketRegime,
  neoWave: row.analysis?.neoWave,
  supplyDemand: row.analysis?.supplyDemand,
  liquidity: row.analysis?.liquidity,
  supplyDemandLiquidityConfluence: row.analysis?.supplyDemandLiquidityConfluence,
  confidence: row.analysis?.confidence ?? { total: 0, contributions: [] },
  analysisMicroseconds: 0,
})))

// Multi-timeframe wiring for the "Visual playback" chart. Only the execution interval streams
// live over SignalR, so every other timeframe here is resampled (no `series` argument) — real
// per-interval data is only available in the static replay view (App.vue). `replayIndex` keeps
// driving live playback and the metrics panel below at the execution interval; `chartIndex` is
// the chart's own position — mirrors `replayIndex` while on that interval, detaches (anchor
// mechanic takes over) once the user switches away or drills, and reattaches when they switch back.
const mtfBaseInterval = computed(() => form.executionInterval)
const {
  activeInterval,
  availableIntervals,
  activeFrames,
  canGoBack,
  switchInterval,
  drillInto,
  goBack,
} = useMultiTimeframeSelection(mtfBaseInterval, replayFrames, computed(() => undefined))

const chartIndex = ref(replayIndex.value)
// True right after drilling: the chart centers on chartIndex (candles both before and after)
// instead of treating it as the live/latest point. See AnalysisChart's `centered` prop.
const centeredView = ref(false)
watch(replayIndex, (next) => {
  if (activeInterval.value === mtfBaseInterval.value) chartIndex.value = next
})

function onSwitchInterval(interval: string) {
  const anchorAt = activeFrames.value[chartIndex.value]?.availableAt ?? null
  switchInterval(interval)
  chartIndex.value = interval === mtfBaseInterval.value
    ? replayIndex.value
    : findAnchorIndex(activeFrames.value, anchorAt)
  centeredView.value = false
}

function onDrillCandle(availableAt: string) {
  const anchorAt = activeFrames.value[chartIndex.value]?.availableAt ?? null
  const resolvedAnchor = drillInto(availableAt, anchorAt)
  if (resolvedAnchor === null) return
  chartIndex.value = findAnchorIndex(activeFrames.value, resolvedAnchor)
  centeredView.value = true
}

function onDrillBack() {
  const resolvedAnchor = goBack()
  if (resolvedAnchor === null) return
  chartIndex.value = activeInterval.value === mtfBaseInterval.value
    ? replayIndex.value
    : findAnchorIndex(activeFrames.value, resolvedAnchor)
  centeredView.value = canGoBack.value
}

const executionDetailFrames = computed<ReplayFrame[]>(() => executionDetailRows.value.map((row, index) => ({
  index,
  availableAt: row.availableAt,
  candle: {
    openTime: row.openTime ?? row.availableAt,
    closeTime: row.availableAt,
    open: row.open, high: row.high, low: row.low, close: row.close, volume: row.volume ?? 0,
  },
  indicators: row.analysis?.indicators ?? {
    atr: null, rsi: null, bollingerMiddle: null, bollingerUpper: null, bollingerLower: null, efficiencyRatio: null,
  },
  swings: row.analysis?.swings ?? [],
  priceZones: row.analysis?.priceZones ?? [],
  trendlines: row.analysis?.trendlines ?? [],
  channels: row.analysis?.channels ?? [],
  marketStructure: row.analysis?.marketStructure,
  priceAction: row.analysis?.priceAction,
  marketRegime: row.analysis?.marketRegime,
  neoWave: row.analysis?.neoWave,
  supplyDemand: row.analysis?.supplyDemand,
  liquidity: row.analysis?.liquidity,
  supplyDemandLiquidityConfluence: row.analysis?.supplyDemandLiquidityConfluence,
  confidence: row.analysis?.confidence ?? { total: 0, contributions: [] },
  analysisMicroseconds: 0,
})))
const replayLayers = reactive<ChartLayers>({
  priceAction: true,
  bollinger: false,
  bollingerRegimes: false,
  movingAverages: true,
  cci: true,
  rsiRelationships: false,
  atr: false,
  volume: true,
  swings: true,
  zones: true,
  supplyDemand: true,
  liquidity: true,
  trendlines: true,
  channels: true,
  donchian: true,
  efficiencyRatio: true,
  marketRegime: true,
  neoWave: true,
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
  stopBackgroundPollers()
})

watch(job, (value) => {
  if (!value) return
  const existingIndex = jobs.value.findIndex(item => item.id === value.id)
  if (existingIndex >= 0) {
    jobs.value.splice(existingIndex, 1, value)
  } else {
    jobs.value.unshift(value)
    jobs.value = jobs.value.slice(0, 20)
  }
  // Surface the real failure reason instead of a later pause/resume conflict.
  const status = formatSimulationStatus(value.status)
  if (status === 'Failed') {
    const detail = value.error?.trim()
    if (detail) error.value = detail
  }
})

watch(() => job.value?.id, () => {
  promotedProfile.value = null
  promoteDraft.strategyId = activeStrategies.value[0]?.strategyId ?? ''
})

watch(completedTradeRevision, () => {
  const payload = completedTrade.value
  if (payload && payload.simulationId.replaceAll('-', '').toLowerCase() === selectedId.value?.replaceAll('-', '').toLowerCase()) {
    appendTrade(payload.simulationId, payload.strategyId, payload.trade)
  }
  if (simulatorMode.value === 'single' && selectedId.value) void loadTrades(selectedId.value)
})

watch(simulatorMode, (mode) => {
  if (mode !== 'single') {
    stopBackgroundPollers()
    // The experiment view does not render playback. Release the large analysis
    // snapshots instead of retaining them in the hidden single-run panel.
    replayRows.value = []
    executionDetailRows.value = []
    loadedChunks.value = new Set()
    return
  }

  if (selectedId.value) startBackgroundPollers(selectedId.value)
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
  selectedChartInstrument.value = null
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
      simulationProfileId: selectedSimulationProfile.value?.profileId ?? null,
      simulationProfileRevision: selectedSimulationProfile.value?.revision ?? null,
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
      timeframes: form.timeframesOverride.trim() || null,
      strategies: form.strategies.split(',').map((item) => item.trim()).filter(Boolean),
      strategyAssignments: form.strategyAssignmentsEnabled
        ? form.strategyAssignments
            .filter((row) => row.strategyType && row.instrument.trim())
            .map((row) => ({ strategyType: row.strategyType, instrument: row.instrument.trim() }))
        : null,
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
      equityProtectionEnabled: form.equityProtectionEnabled,
      equityProtectionActivationProfitPercent: form.equityProtectionActivationProfitPercent > 0
        ? form.equityProtectionActivationProfitPercent
        : null,
      equityProtectionMaxGivebackPercent: form.equityProtectionMaxGivebackPercent > 0
        ? form.equityProtectionMaxGivebackPercent
        : null,
      equityProtectionActionType: form.equityProtectionActionType,
      equityProtectionReductionFraction: form.equityProtectionReductionFraction,
      equityProtectionFutureRiskMultiplier: form.equityProtectionFutureRiskMultiplier,
      equityProtectionRecoveryBars: form.equityProtectionRecoveryBars,
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
      accountMode: form.accountMode,
      regimeEnabled: form.regimeEnabled,
      efficiencyRatioPeriod: form.efficiencyRatioPeriod,
      regimeConfirmationBars: form.regimeConfirmationBars,
      regimePersistenceBars: form.regimePersistenceBars,
      regimeSoftSpreadAtr: form.regimeSoftSpreadAtr,
      regimeHardSpreadAtr: form.regimeHardSpreadAtr,
      neoWaveEnabled: form.neoWaveEnabled,
      neoWaveEvidenceMode: form.neoWaveEvidenceMode,
      neoWaveEvidenceInterval: form.neoWaveEvidenceInterval || null,
      neoWaveMinimumStructuralScore: form.neoWaveMinimumStructuralScore,
      neoWaveMaximumTrustedConflictScore: form.neoWaveMaximumTrustedConflictScore,
      neoWaveMinimumRiskMultiplier: form.neoWaveMinimumRiskMultiplier,
      neoWaveMaximumConflictRiskReduction: form.neoWaveMaximumConflictRiskReduction,
      tradingConditionsEnabled: form.tradingConditionsEnabled,
      allowedSessions: form.allowedSessions.split(',').map((item) => item.trim()).filter(Boolean),
      rolloverBlackoutMinutesBefore: form.rolloverBlackoutMinutesBefore,
      rolloverBlackoutMinutesAfter: form.rolloverBlackoutMinutesAfter,
      conditionSoftSpreadAtr: form.conditionSoftSpreadAtr,
      conditionHardSpreadAtr: form.conditionHardSpreadAtr,
      economicEventFilterEnabled: form.economicEventFilterEnabled,
      maximumTotalPortfolioHeatPercent: form.maximumTotalPortfolioHeatPercent,
      maximumPendingRiskPercent: form.maximumPendingRiskPercent,
      maximumStrategyRiskPercent: form.maximumStrategyRiskPercent,
      maximumInstrumentRiskPercent: form.maximumInstrumentRiskPercent,
      maximumCurrencyStopRiskPercent: form.maximumCurrencyStopRiskPercent,
      minimumUnallocatedMarginReservePercent: form.minimumUnallocatedMarginReservePercent,
      maximumOpenPositions: form.maximumOpenPositions,
      correlationLookbackBars: form.correlationLookbackBars,
      correlationMinimumSamples: form.correlationMinimumSamples,
      correlationSoftThreshold: form.correlationSoftThreshold,
      correlationHardThreshold: form.correlationHardThreshold,
      maximumNetCurrencyExposurePercent: form.maximumNetCurrencyExposurePercent,
      maximumGrossCurrencyExposurePercent: form.maximumGrossCurrencyExposurePercent,
      valueLocationEvidenceEnabled: form.valueLocationEvidenceEnabled,
      valueLocationNearAtrThreshold: form.valueLocationNearAtrThreshold,
      valueLocationStretchedAtrThreshold: form.valueLocationStretchedAtrThreshold,
      valueLocationConfidenceAdjustment: form.valueLocationConfidenceAdjustment,
      currencyStrengthEnabled: form.currencyStrengthEnabled,
      currencyStrengthInterval: form.currencyStrengthInterval,
      currencyStrengthReturnLookbackBars: form.currencyStrengthReturnLookbackBars,
      currencyStrengthVolatilityLookbackBars: form.currencyStrengthVolatilityLookbackBars,
      currencyStrengthMinimumCoveragePercent: form.currencyStrengthMinimumCoveragePercent,
      // No basket editor yet - default to a single basket containing the traded
      // instrument itself so Enabled=true stays valid (§5 leave-one-out means this
      // basket alone never actually informs the traded pair's own differential;
      // configuring real alternative pairs currently requires the API/CLI directly).
      currencyStrengthBaskets: form.currencyStrengthEnabled ? { Majors: [form.instrument] } : {},
      adaptiveRiskEnabled: form.adaptiveRiskEnabled,
      executionFillModel: form.executionFillModel,
      stressExecutionScenario: form.stressExecutionScenario,
      maximumFillQuantityPerFrame: form.maximumFillQuantityPerFrame > 0 ? form.maximumFillQuantityPerFrame : null,
      maximumFillParticipationFraction: form.maximumFillParticipationFraction,
      asianSessionSpreadMultiplier: form.asianSessionSpreadMultiplier,
      rolloverSpreadMultiplier: form.rolloverSpreadMultiplier,
      volatilitySlippageFraction: form.volatilitySlippageFraction,
      gapSlippageFraction: form.gapSlippageFraction,
      financingEnabled: form.financingEnabled,
      financingRates: form.financingEnabled
        ? { [form.instrument]: {
            longAnnualPercent: form.financingLongAnnualPercent,
            shortAnnualPercent: form.financingShortAnnualPercent,
          } }
        : {},
      refreshCache: form.refreshCache,
      noCache: form.noCache,
      captureMarketReplay: form.captureMarketReplay,
      legacyPositionManagement: form.legacyPositionManagement,
      improvedPositionManagement: form.improvedPositionManagement,
      autoCalibrateBeforeRun: form.autoCalibrateBeforeRun,
      autoCalibrateTrainMonths: form.autoCalibrateTrainMonths,
      autoCalibrateEmbargoDays: form.autoCalibrateEmbargoDays,
      setupCalibrationArtifactId: form.setupCalibrationArtifactId.trim() || null,
      managementCalibrationArtifactId: form.managementCalibrationArtifactId.trim() || null,
      metaModelArtifactId: form.metaModelArtifactId.trim() || null,
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

function preferredInstrumentForBroker(broker: SimulationBrokerOption): string | undefined {
  const preferredKeys = broker.id === 'binance'
    ? ['CRYPTO:BTC/USDT', 'CRYPTO:ETH/USDT']
    : ['FX:EUR/USD', 'FX:GBP/USD', 'FX:GBP/JPY', 'FX:USD/JPY', 'FX:EUR/GBP']
  for (const key of preferredKeys) {
    if (broker.instruments.some((asset) => asset.instrument === key)) return key
  }
  return broker.instruments[0]?.instrument
}

function applyBrokerSelection(preferRecommendedInstrument = false) {
  const broker = selectedBroker.value
  form.sourceKind = broker.sourceKind === 'Unavailable' ? 'OandaCandles' : broker.sourceKind
  selectedAssetClass.value = 'All'
  assetSearch.value = ''

  const allowedModes = precisionOptions.value.map((item) => item.value)
  if (!allowedModes.includes(form.precisionMode)) form.precisionMode = allowedModes[0] ?? 'Fast'
  applyPrecisionDefaults()

  if (broker.id === 'imported') return
  const selected = broker.instruments.find((asset) => asset.instrument === form.instrument)
  if (preferRecommendedInstrument || !selected) {
    form.instrument = preferredInstrumentForBroker(broker) ?? form.instrument
    return
  }
  form.instrument = selected.instrument
}

function addStrategyAssignment() {
  form.strategyAssignments.push({ strategyType: 'legacy', instrument: form.instrument })
}

function removeStrategyAssignment(index: number) {
  form.strategyAssignments.splice(index, 1)
}

function toggleStrategyAssignments(enabled: boolean) {
  form.strategyAssignmentsEnabled = enabled
  if (enabled && form.strategyAssignments.every((row) => !row.instrument.trim())) {
    // Seed with today's single-instrument two-row shape as the starting point.
    form.strategyAssignments = [
      { strategyType: 'legacy', instrument: form.instrument },
      { strategyType: 'improved', instrument: form.instrument },
    ]
  }
}

function applyFormState(next: SimulationFormState, options?: { preserveBroker?: boolean; preferRecommendedInstrument?: boolean }) {
  const preserveBroker = options?.preserveBroker !== false
  const brokerId = form.brokerId
  const instrument = form.instrument
  const {
    legacyPositionManagement,
    improvedPositionManagement,
    ...rest
  } = next
  Object.assign(form, rest)
  Object.assign(form.legacyPositionManagement, legacyPositionManagement)
  Object.assign(form.improvedPositionManagement, improvedPositionManagement)
  if (preserveBroker && brokerCatalog.value?.brokers.some((broker) => broker.id === brokerId && broker.isAvailable)) {
    form.brokerId = brokerId
    form.instrument = instrument
  }
  applyBrokerSelection(options?.preferRecommendedInstrument ?? false)
  error.value = null
}

function applyPreset(presetId: string) {
  const preset = simulationPresets.find((item) => item.id === presetId) ?? simulationPresets[0]!
  selectedSimulationProfileKey.value = ''
  selectedPresetId.value = preset.id
  applyFormState(preset.build(), { preserveBroker: true, preferRecommendedInstrument: false })
  showAdvancedConfig.value = false
}

function applyRecommendedDefaults() {
  applyPreset(selectedPresetId.value || 'research')
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
  if (form.positionSizingMode === 'FixedCashRisk' && form.fixedCashRisk <= 0) {
    throw new Error('Fixed cash risk must be greater than zero when that sizing mode is selected.')
  }
  if (form.positionSizingMode === 'FixedFractionalRisk' &&
      (form.riskPercentOfEquity <= 0 || form.riskPercentOfEquity > 100)) {
    throw new Error('Percentage risk must be between 0 (exclusive) and 100 when fractional sizing is selected.')
  }
  if (form.fixedCashRisk < 0 || form.riskPercentOfEquity < 0 || form.riskPercentOfEquity > 100) {
    throw new Error('Cash risk and percentage risk cannot be negative; percentage risk cannot exceed 100.')
  }
  if (form.minimumQuantity <= 0 || form.quantityStep <= 0 ||
      form.maximumQuantity < 0 ||
      (form.maximumQuantity > 0 && form.maximumQuantity < form.minimumQuantity)) {
    throw new Error('Quantity limits and step are invalid. Quantity step may be fractional (e.g. 0.01); maximum 0 means unlimited.')
  }
  if (form.maximumAccountMarginUsagePercent <= 0 || form.maximumAccountMarginUsagePercent > 100 ||
      form.maximumSinglePositionMarginPercent <= 0 || form.maximumSinglePositionMarginPercent > 100 ||
      form.maximumSinglePositionMarginPercent > form.maximumAccountMarginUsagePercent) {
    throw new Error('Margin caps must be within 0-100, and the one-position cap cannot exceed the account cap.')
  }
  // Soft thresholds must remain positive; hard thresholds must be strictly above soft.
  if (form.regimeSoftSpreadAtr <= 0 || form.regimeHardSpreadAtr <= form.regimeSoftSpreadAtr ||
      form.conditionSoftSpreadAtr <= 0 || form.conditionHardSpreadAtr <= form.conditionSoftSpreadAtr) {
    throw new Error('Soft spread/ATR thresholds must be greater than 0 and each hard threshold must be greater than soft.')
  }
  if (form.regimeConfirmationBars < 1 || form.regimePersistenceBars < 0 || form.efficiencyRatioPeriod < 2) {
    throw new Error('Regime confirmation, persistence, and ER period values are invalid.')
  }
  if (form.neoWaveMinimumStructuralScore < 0 || form.neoWaveMinimumStructuralScore > 100 ||
      form.neoWaveMaximumTrustedConflictScore < 0 || form.neoWaveMaximumTrustedConflictScore > 100 ||
      form.neoWaveMinimumRiskMultiplier < 0 || form.neoWaveMinimumRiskMultiplier > 1 ||
      form.neoWaveMaximumConflictRiskReduction < 0 || form.neoWaveMaximumConflictRiskReduction > 1) {
    throw new Error('NEoWave score, conflict, and risk settings are outside their valid ranges.')
  }
  if (form.maximumTotalPortfolioHeatPercent < 0 ||
      form.maximumPendingRiskPercent < 0 ||
      form.maximumStrategyRiskPercent < 0 ||
      form.maximumInstrumentRiskPercent < 0 ||
      form.maximumCurrencyStopRiskPercent < 0 ||
      form.maximumPendingRiskPercent > form.maximumTotalPortfolioHeatPercent ||
      form.maximumStrategyRiskPercent > form.maximumTotalPortfolioHeatPercent ||
      form.maximumInstrumentRiskPercent > form.maximumTotalPortfolioHeatPercent ||
      form.maximumCurrencyStopRiskPercent > form.maximumTotalPortfolioHeatPercent ||
      form.maximumOpenPositions < 1) {
    throw new Error('Portfolio sub-limits cannot exceed total heat, cannot be negative, and maximum positions must be positive.')
  }
  if (form.correlationMinimumSamples < 2 || form.correlationMinimumSamples > form.correlationLookbackBars ||
      form.correlationSoftThreshold < 0 || form.correlationHardThreshold < 0 ||
      form.correlationHardThreshold <= form.correlationSoftThreshold ||
      form.correlationHardThreshold > 1) {
    throw new Error('Correlation lookback, samples, and soft/hard thresholds are inconsistent (soft may be 0).')
  }
  if (form.maximumFillParticipationFraction <= 0 || form.maximumFillParticipationFraction > 1 ||
      form.maximumFillQuantityPerFrame < 0 || form.asianSessionSpreadMultiplier < 0 ||
      form.rolloverSpreadMultiplier < 0 || form.volatilitySlippageFraction < 0 ||
      form.gapSlippageFraction < 0) {
    throw new Error('Execution capacity, spread multipliers, and slippage fractions are invalid (0 is allowed where it means off).')
  }

  if (!Number.isFinite(form.minimumPriceActionConfidence) ||
      form.minimumPriceActionConfidence < 0 ||
      form.minimumPriceActionConfidence > 100) {
    throw new Error('Minimum price-action confidence must be between 0 and 100.')
  }
  if (form.minimumRewardRisk < 0) {
    throw new Error('Minimum reward/risk cannot be negative (0 disables the filter).')
  }

  for (const [name, options] of [
    ['Legacy', form.legacyPositionManagement],
    ['Improved', form.improvedPositionManagement],
  ] as const) {
    // 0 is allowed: means activate break-even immediately / at open when enabled by mode.
    if (options.breakEvenActivationR < 0) throw new Error(`${name} break-even activation cannot be negative.`)
    if (options.structureTrailActivationR < options.breakEvenActivationR) {
      throw new Error(`${name} structure activation must be at or above break-even activation.`)
    }
    if (options.atrBufferMultiplier < 0 ||
        options.minimumStopImprovementAtr < 0 ||
        options.neoWaveInvalidationBufferAtr < 0) {
      throw new Error(`${name} ATR values cannot be negative.`)
    }
    if (options.minimumRunnerFraction < 0 || options.minimumRunnerFraction > 1) {
      throw new Error(`${name} minimum runner fraction must be between 0 and 1.`)
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

  if (form.equityProtectionEnabled) {
    if (form.equityProtectionMaxGivebackPercent <= 0) {
      throw new Error('Equity protection requires a maximum giveback percent above zero.')
    }
    if (form.equityProtectionActivationProfitPercent < 0) {
      throw new Error('Equity protection activation profit percent cannot be negative.')
    }
    if (form.equityProtectionReductionFraction < 0 || form.equityProtectionReductionFraction > 1) {
      throw new Error('Equity protection reduction fraction must be between 0 and 1 inclusive.')
    }
    if (form.equityProtectionFutureRiskMultiplier < 0 || form.equityProtectionFutureRiskMultiplier > 1) {
      throw new Error('Equity protection future-risk multiplier must be between 0 and 1 (never above base risk).')
    }
    if (form.equityProtectionRecoveryBars < 0) {
      throw new Error('Equity protection recovery bars cannot be negative.')
    }
  }
}

function startBackgroundPollers(id: string) {
  stopBackgroundPollers()
  if (simulatorMode.value !== 'single') return
  void loadTrades(id)
  void loadProgressiveReplay(id)
  tradePoll = window.setInterval(() => { void loadTrades(id) }, 5000)
  chunkPoll = window.setInterval(() => { void loadProgressiveReplay(id) }, 1500)
}

function stopBackgroundPollers() {
  if (tradePoll !== undefined) window.clearInterval(tradePoll)
  if (chunkPoll !== undefined) window.clearInterval(chunkPoll)
  tradePoll = undefined
  chunkPoll = undefined
}

async function control(action: 'pause' | 'resume' | 'cancel') {
  if (!job.value) return
  const status = formatSimulationStatus(job.value.status)
  if (action === 'pause' && !canPauseCompute.value) {
    error.value = status === 'Failed'
      ? `This simulation already failed${job.value.error ? `: ${job.value.error}` : '.'} Start a new run — pause is only available while compute is active.`
      : `Cannot pause while status is ${status}.`
    return
  }
  if (action === 'resume' && !canResumeCompute.value) {
    error.value = status === 'Failed'
      ? `This simulation already failed${job.value.error ? `: ${job.value.error}` : '.'} Resume only works from Paused — start a new simulation to run again.`
      : `Cannot resume while status is ${status}. Resume is only available when the job is Paused.`
    return
  }
  if (action === 'cancel' && !canCancelCompute.value) {
    error.value = `Simulation is already ${status}; nothing to cancel.`
    return
  }

  busy.value = true
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${job.value.id}/${action}`, {
      method: 'POST',
    })
    if (!response.ok) {
      const message = await readApiError(response, `${action} failed`)
      // If the job failed between click and response, prefer the snapshot error.
      if (message.includes('SimulationStateConflict') || message.includes('already Failed')) {
        const detail = job.value.error?.trim()
        throw new Error(
          detail
            ? `Simulation already finished as Failed: ${detail}`
            : `${message} Start a new simulation instead of pause/resume.`,
        )
      }
      throw new Error(message)
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

const promoteDraft = reactive({
  strategyId: '',
  revision: 1,
  approveForDemo: false,
  description: '',
})
const promoteBusy = ref(false)
const promotedProfile = ref<TradingPolicyProfile | null>(null)

async function promoteToLivePolicy() {
  if (!job.value || !promoteDraft.strategyId) return
  promoteBusy.value = true
  error.value = null
  promotedProfile.value = null
  try {
    const body: PromoteTradingPolicyRequest = {
      revision: promoteDraft.revision,
      approveForDemo: promoteDraft.approveForDemo,
      description: promoteDraft.description.trim() || null,
      setupCalibrationArtifactId: form.setupCalibrationArtifactId.trim() || null,
      managementCalibrationArtifactId: form.managementCalibrationArtifactId.trim() || null,
      metaModelArtifactId: form.metaModelArtifactId.trim() || null,
    }
    const response = await fetch(
      `${import.meta.env.BASE_URL}api/simulations/${job.value.id}/policy-profiles/${promoteDraft.strategyId}`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      },
    )
    if (!response.ok) {
      throw new Error(await readApiError(response, 'Could not promote this simulation to a live policy'))
    }
    promotedProfile.value = await response.json() as TradingPolicyProfile
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    promoteBusy.value = false
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
  if (simulatorMode.value !== 'single' || selectedId.value !== id || replayLoadsInFlight.has(id)) return
  replayLoadsInFlight.add(id)
  try {
    // Prefer chunk list + incremental fetch; fall back to bounded range API.
    // Composite identity: simulationId + chunkId prevents cross-job pollution.
    const chunksResponse = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}/replay/chunks`)
    if (chunksResponse.ok) {
      const chunks = await chunksResponse.json() as Array<{ chunkId: string }>
      // One analysis-rich chunk is enough to seed the 180-candle chart. Loading
      // three at once used to inflate tens of MB of JSON on every selection.
      const chunk = chunks.at(-1)
      if (!chunk) return
      const key = `${id}:${chunk.chunkId}`
      if (loadedChunks.value.has(key)) return
      const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}/replay/chunks/${chunk.chunkId}?take=250`)
      if (!response.ok) {
        if (response.status === 413) {
          loadedChunks.value.add(key)
          error.value = await readApiError(response, 'Replay chunk is too large for interactive playback')
        }
        return
      }
      const payload = await response.json() as PlaybackRow[] | { rows?: PlaybackRow[] }
      const rows = Array.isArray(payload) ? payload : (payload.rows ?? [])
      if (simulatorMode.value !== 'single' || selectedId.value !== id) return
      appendRows(rows)
      loadedChunks.value.add(key)
      return
    }

    const response = await fetch(
      `${import.meta.env.BASE_URL}api/simulations/${id}/replay?startSequence=${chunkCursor.value}&limit=750`,
    )
    if (!response.ok) return
    const payload = await response.json() as { rows?: PlaybackRow[]; nextCursor?: string | null }
    if (simulatorMode.value !== 'single' || selectedId.value !== id) return
    appendRows(payload.rows ?? [])
    if (payload.nextCursor?.startsWith('seq:')) {
      chunkCursor.value = Number(payload.nextCursor.slice(4)) || chunkCursor.value
    }
  } finally {
    replayLoadsInFlight.delete(id)
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
  selectedChartInstrument.value = null
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

/** wget-style ascii bar: `[###########-----------]`. */
function asciiBar(percent: number, width = 22): string {
  const clamped = Math.max(0, Math.min(100, percent))
  const filled = Math.round((clamped / 100) * width)
  return '█'.repeat(filled) + '░'.repeat(Math.max(0, width - filled))
}

/**
 * All instruments in one job share a single canonical clock, so there is no per-instrument
 * progress percent - only the job-wide phase (preparing/warming up/running/etc) combined with
 * each strategy's own trading status (which can independently fail while others keep running).
 */
function instrumentStatus(strategy: StrategyProgressSnapshot): string {
  const jobStatus = displayJobStatus.value
  if (jobStatus === 'Failed' || strategy.status === 'Failed') return 'Failed'
  if (['Queued', 'PreparingData', 'DownloadingData', 'LoadingCache'].includes(jobStatus)) return 'Preparing'
  if (jobStatus === 'WarmingUp') return 'Warming up'
  if (jobStatus === 'Paused') return 'Paused'
  if (['Cancelling', 'Cancelled'].includes(jobStatus)) return 'Cancelled'
  if (jobStatus === 'Completed') return strategy.status ?? 'Completed'
  return strategy.status ?? 'Running'
}

/**
 * Each instrument in a multi-instrument run downloads independently, but the job snapshot
 * used to collapse all of them onto one shared status slot (whichever fired last). This
 * looks up the download/cache state for this specific instrument instead.
 */
function downloadStatus(strategy: StrategyProgressSnapshot) {
  if (!strategy.instrument) return null
  return job.value?.sourceProgressByInstrument?.[strategy.instrument] ?? null
}

function togglePlayback() {
  if (playbackPaused.value) play()
  else pause()
}

watch(() => form.brokerId, () => applyBrokerSelection())
watch(() => form.precisionMode, applyPrecisionDefaults)
watch(selectedAssetClass, selectFirstFilteredAsset)
watch(assetSearch, selectFirstFilteredAsset)

function loadResearchSelection() {
  try {
    const raw = localStorage.getItem(RESEARCH_ARTIFACT_SELECTION_KEY)
    if (!raw) return
    const parsed = JSON.parse(raw) as Partial<ResearchArtifactSelection>
    form.setupCalibrationArtifactId = parsed.setupCalibrationArtifactId ?? ''
    form.managementCalibrationArtifactId = parsed.managementCalibrationArtifactId ?? ''
    form.metaModelArtifactId = parsed.metaModelArtifactId ?? ''
  } catch {
    // ignore corrupt storage
  }
}

function clearResearchSelection() {
  form.setupCalibrationArtifactId = ''
  form.managementCalibrationArtifactId = ''
  form.metaModelArtifactId = ''
  localStorage.setItem(
    RESEARCH_ARTIFACT_SELECTION_KEY,
    JSON.stringify({
      setupCalibrationArtifactId: '',
      managementCalibrationArtifactId: '',
      metaModelArtifactId: '',
    } satisfies ResearchArtifactSelection),
  )
}

onMounted(() => {
  loadResearchSelection()
  void refreshSimulationProfiles()
  void loadSimulationCatalog()
  void refreshJobList()
  void refreshImportedDatasets()
})
</script>

<template>
  <section class="simulator-panel">
    <header class="simulator-header">
      <div>
        <h2>{{ simulatorMode === 'single' ? 'Dashboard Simulator' : simulatorMode === 'trend' ? 'Alfonso trend comparison' : 'Experiment Lab' }}</h2>
        <p>
          {{ simulatorMode === 'single'
            ? 'Choose a trading-style preset, adjust instrument/dates if needed, then run. Warm-up history is loaded automatically; trading starts at the evaluation From date.'
            : simulatorMode === 'trend' ? 'Compare trend recognition on identical closed candles, independently of entries.' : 'Build immutable profile comparisons with explicit learning, embargo, warm-up, and held-out boundaries.' }}
        </p>
      </div>
      <div class="simulator-mode" aria-label="Simulator mode">
        <button type="button" :class="{ active: simulatorMode === 'single' }" @click="simulatorMode = 'single'">Single Run</button>
        <button type="button" :class="{ active: simulatorMode === 'experiment' }" @click="simulatorMode = 'experiment'">Experiment</button>
        <button type="button" :class="{ active: simulatorMode === 'trend' }" @click="simulatorMode = 'trend'">Trend comparison</button>
      </div>
      <div v-if="simulatorMode === 'single'" class="simulator-status">
        {{ progressLabel }}
        <small v-if="connected"> · SignalR</small>
        <small v-else-if="usingPolling"> · polling fallback</small>
      </div>
    </header>

    <div v-if="simulatorMode === 'single'" class="simulator-grid">
      <form class="card config-card" @submit.prevent="startSimulation">
        <div class="config-heading">
          <h3>Configuration</h3>
          <button type="button" class="secondary compact-button" @click="applyRecommendedDefaults">
            Reset active preset
          </button>
        </div>
        <fieldset class="preset-picker">
          <legend>Trading style presets</legend>
          <div class="preset-grid" role="radiogroup" aria-label="Simulation configuration preset">
            <button
              v-for="preset in simulationPresets"
              :key="preset.id"
              type="button"
              class="preset-card"
              :class="{ active: selectedPresetId === preset.id }"
              role="radio"
              :aria-checked="selectedPresetId === preset.id"
              @click="applyPreset(preset.id)"
            >
              <span class="preset-badge">{{ preset.badge }}</span>
              <strong>{{ preset.name }}</strong>
              <small>{{ preset.description }}</small>
            </button>
          </div>
          <div class="preset-banner">
            <strong>{{ selectedPreset.name }} ready</strong>
            <p class="mono preset-line">{{ activePresetSummary }}</p>
            <p class="muted">
              Pick a style to load MTF roles, risk, management, and module toggles together.
              Change instrument/dates if needed; open advanced only for fine-tuning.
              Fractional fields use 0.01 steps and accept 0 where 0 means off/unlimited/immediate.
            </p>
          </div>
        </fieldset>
        <fieldset class="broker-picker">
          <legend>Saved agent profile</legend>
          <label>
            Immutable profile revision (optional)
            <select
              v-model="selectedSimulationProfileKey"
              :disabled="simulationProfilesLoading"
              @change="applySimulationProfileSelection"
            >
              <option value="">Use the preset/manual strategy configuration</option>
              <option
                v-for="profile in simulationProfiles"
                :key="simulationProfileKey(profile)"
                :value="simulationProfileKey(profile)"
              >
                {{ profile.name }} · r{{ profile.revision }} · {{ simulationProfileKind(profile) }}
              </option>
            </select>
          </label>
          <p v-if="simulationProfilesError" class="error">{{ simulationProfilesError }}</p>
          <small v-if="selectedSimulationProfile" class="muted">
            Uses the saved agent, analysis, runtime, management, and calibration policy exactly.
            Profile instrument: {{ selectedSimulationProfile.instrument.value }} · hash:
            <span class="mono">{{ selectedSimulationProfile.contentHash.slice(0, 12) }}</span>
          </small>
          <small v-else class="muted">
            Profiles created in Experiment Lab appear here. Broker, dates, balance, and trading costs remain run inputs.
          </small>
          <button
            v-if="!showProfileBuilder"
            type="button"
            class="button button-secondary"
            @click="showProfileBuilder = true"
          >
            Build new profile step by step
          </button>
          <ProfileBuilderWizard
            v-if="showProfileBuilder"
            :instrument="form.instrument"
            @saved="onProfileBuilt"
            @close="showProfileBuilder = false"
          />
        </fieldset>
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
          <label>From (evaluation) <input v-model="form.from" type="date" /></label>
          <label>To (evaluation) <input v-model="form.to" type="date" /></label>
        </div>
        <small class="muted">{{ formDataWindowHint }}</small>

        <div class="run-row">
          <button type="submit" :disabled="busy || brokerCatalogLoading || !selectedBroker.isAvailable">
            {{ busy ? 'Starting…' : 'Run Simulation' }}
          </button>
          <button
            type="button"
            class="secondary"
            @click="showAdvancedConfig = !showAdvancedConfig"
          >
            {{ showAdvancedConfig ? 'Hide advanced' : 'Show advanced' }}
          </button>
        </div>
        <p v-if="error || realtimeError" class="error">{{ error || realtimeError }}</p>

        <div v-show="showAdvancedConfig" class="advanced-config">
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
        <fieldset class="research-artifacts-fieldset">
          <legend>Research calibration artifacts</legend>
          <p class="muted">
            By default the server auto-trains setup → meta → management on history before the sim
            (2 months ending 10 days before From; legacy and improved train in parallel). Manual
            GUIDs from the Research tab override auto-calibration when any ID is set.
          </p>
          <label class="checkbox-row">
            <input type="checkbox" v-model="form.autoCalibrateBeforeRun" />
            Auto-calibrate before run (train then simulate)
          </label>
          <div class="row" v-if="form.autoCalibrateBeforeRun">
            <label>Train months <input type="number" min="1" max="24" v-model.number="form.autoCalibrateTrainMonths" /></label>
            <label>Embargo days before From <input type="number" min="0" max="90" v-model.number="form.autoCalibrateEmbargoDays" /></label>
          </div>
          <p class="muted" v-if="form.autoCalibrateBeforeRun">
            Adds real time before the simulation starts: the setup stage runs one full backtest
            over the training window, and the management stage reruns a second full backtest
            under the frozen entry policy - roughly 2x the cost of the training window alone, on
            top of the requested simulation. Uncheck this (or set manual artifact IDs above) for
            the fastest possible run.
          </p>
          <div class="row">
            <label>Setup artifact ID <input v-model.trim="form.setupCalibrationArtifactId" spellcheck="false" placeholder="guid or empty" /></label>
            <label>Management artifact ID <input v-model.trim="form.managementCalibrationArtifactId" spellcheck="false" placeholder="guid or empty" /></label>
            <label>Meta-model artifact ID <input v-model.trim="form.metaModelArtifactId" spellcheck="false" placeholder="guid or empty" /></label>
          </div>
          <button type="button" class="secondary compact-button" @click="clearResearchSelection">Clear research IDs</button>
        </fieldset>
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
          <label>Unified timeframe override (optional)
            <input
              v-model="form.timeframesOverride"
              placeholder="e.g. trigger=5m,context=1h:gate:0,context=4h:vote:1"
            />
          </label>
          <small class="muted">
            When set, overrides the fields above role by role - a role you don't mention here
            keeps its value from the fields above. Roles: trigger, setup, context, confirmation,
            regime, neowave. Influences: gate (must agree), veto, vote (N-of-M), advisory,
            fallback. Format: <code>role=interval[:influence[:priority]]</code>, comma-separated.
          </small>
        </fieldset>
        <label>Strategies <input v-model="form.strategies" :disabled="form.strategyAssignmentsEnabled" /></label>
        <fieldset class="management-config">
          <legend>Multi-instrument portfolio clock</legend>
          <label class="inline-check">
            <input
              :checked="form.strategyAssignmentsEnabled"
              @change="toggleStrategyAssignments(($event.target as HTMLInputElement).checked)"
              type="checkbox"
            />
            Assign each strategy its own instrument
          </label>
          <template v-if="form.strategyAssignmentsEnabled">
            <div class="row" v-for="(row, index) in form.strategyAssignments" :key="index">
              <label>Strategy
                <select v-model="row.strategyType">
                  <option value="legacy">Legacy</option>
                  <option value="improved">Improved</option>
                  <option value="structural-confluence">Structural confluence</option>
                </select>
              </label>
              <label>Instrument
                <input v-model="row.instrument" placeholder="FX:EUR/USD" />
              </label>
              <button type="button" @click="removeStrategyAssignment(index)">Remove</button>
            </div>
            <button type="button" @click="addStrategyAssignment">+ Add strategy/instrument</button>
            <small class="muted">
              Each row trades only its own instrument, evaluated on one shared portfolio
              clock - correlation, margin-netting, and currency-exposure gating in
              SharedPortfolioAccount mode can only engage across more than one distinct
              instrument here. Overrides Strategies and the single Instrument above for
              every assigned strategy while enabled.
            </small>
          </template>
        </fieldset>
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
            <label>Break-even R <input v-model.number="form.legacyPositionManagement.breakEvenActivationR" type="number" min="0" step="0.01" /></label>
            <label>Structure R <input v-model.number="form.legacyPositionManagement.structureTrailActivationR" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label>ATR buffer <input v-model.number="form.legacyPositionManagement.atrBufferMultiplier" type="number" min="0" step="0.01" /></label>
            <label>Min improvement ATR <input v-model.number="form.legacyPositionManagement.minimumStopImprovementAtr" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label class="inline-check"><input v-model="form.legacyPositionManagement.enableScaleOut" type="checkbox" /> Scale out in stages</label>
            <label>Minimum runner
              <input v-model.number="form.legacyPositionManagement.minimumRunnerFraction" type="number" min="0" max="1" step="0.01" />
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
          <div class="row">
            <label class="inline-check"><input v-model="form.legacyPositionManagement.exitOnAdverseStructureBreak" type="checkbox" /> Exit on adverse structure</label>
            <label class="inline-check"><input v-model="form.legacyPositionManagement.enableNeoWaveInvalidationExit" type="checkbox" /> Exit on entry-pinned wave invalidation</label>
            <label>Wave invalidation buffer ATR
              <input v-model.number="form.legacyPositionManagement.neoWaveInvalidationBufferAtr" type="number" min="0" step="0.01" :disabled="!form.legacyPositionManagement.enableNeoWaveInvalidationExit" />
            </label>
          </div>
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
            <label>Break-even R <input v-model.number="form.improvedPositionManagement.breakEvenActivationR" type="number" min="0" step="0.01" /></label>
            <label>Structure R <input v-model.number="form.improvedPositionManagement.structureTrailActivationR" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label>ATR buffer <input v-model.number="form.improvedPositionManagement.atrBufferMultiplier" type="number" min="0" step="0.01" /></label>
            <label>Min improvement ATR <input v-model.number="form.improvedPositionManagement.minimumStopImprovementAtr" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label class="inline-check"><input v-model="form.improvedPositionManagement.enableScaleOut" type="checkbox" /> Scale out in stages</label>
            <label>Minimum runner
              <input v-model.number="form.improvedPositionManagement.minimumRunnerFraction" type="number" min="0" max="1" step="0.01" />
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
            <label><input v-model="form.improvedPositionManagement.enableNeoWaveInvalidationExit" type="checkbox" /> Entry-pinned wave invalidation exit</label>
            <label><input v-model="form.improvedPositionManagement.preserveBracketTarget" type="checkbox" /> Preserve target</label>
          </div>
          <label>Wave invalidation buffer ATR
            <input v-model.number="form.improvedPositionManagement.neoWaveInvalidationBufferAtr" type="number" min="0" step="0.01" :disabled="!form.improvedPositionManagement.enableNeoWaveInvalidationExit" />
          </label>
        </fieldset>
        <p class="behaviour-summary">{{ managementSummary }}</p>
        <fieldset class="management-config">
          <legend>Market regime and trading conditions</legend>
          <div class="row checks">
            <label><input v-model="form.regimeEnabled" type="checkbox" /> Regime classification, routing, and profiles</label>
            <label><input v-model="form.tradingConditionsEnabled" type="checkbox" /> Session/spread condition filter</label>
          </div>
          <div class="row">
            <label>ER period (bars) <input v-model.number="form.efficiencyRatioPeriod" type="number" min="2" step="1" /></label>
            <label>Regime confirmation (bars) <input v-model.number="form.regimeConfirmationBars" type="number" min="1" step="1" /></label>
            <label>Regime persistence (bars) <input v-model.number="form.regimePersistenceBars" type="number" min="0" step="1" /></label>
          </div>
          <div class="row">
            <label>Regime soft spread/ATR <input v-model.number="form.regimeSoftSpreadAtr" type="number" min="0.01" step="0.01" /></label>
            <label>Regime hard spread/ATR <input v-model.number="form.regimeHardSpreadAtr" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label>Allowed sessions <input v-model="form.allowedSessions" :disabled="!form.tradingConditionsEnabled" /></label>
            <label>Rollover before (minutes) <input v-model.number="form.rolloverBlackoutMinutesBefore" type="number" min="0" step="1" /></label>
            <label>Rollover after (minutes) <input v-model.number="form.rolloverBlackoutMinutesAfter" type="number" min="0" step="1" /></label>
          </div>
          <div class="row">
            <label>Condition soft spread/ATR <input v-model.number="form.conditionSoftSpreadAtr" type="number" min="0.01" step="0.01" /></label>
            <label>Condition hard spread/ATR <input v-model.number="form.conditionHardSpreadAtr" type="number" min="0" step="0.01" /></label>
            <label class="inline-check"><input v-model="form.economicEventFilterEnabled" type="checkbox" /> Event blackout (provider required)</label>
          </div>
        </fieldset>
        <fieldset class="management-config">
          <legend>Portfolio and adaptive sizing</legend>
          <div class="row">
            <label>Account mode
              <select v-model="form.accountMode">
                <option>IndependentStrategyAccounts</option>
                <option>SharedPortfolioAccount</option>
              </select>
            </label>
            <label class="inline-check"><input v-model="form.adaptiveRiskEnabled" type="checkbox" /> Drawdown/volatility adaptive sizing</label>
            <label>Maximum positions <input v-model.number="form.maximumOpenPositions" type="number" min="1" step="1" /></label>
          </div>
          <div class="row">
            <label>Total heat (% of equity) <input v-model.number="form.maximumTotalPortfolioHeatPercent" type="number" min="0" step="0.01" /></label>
            <label>Pending heat (%) <input v-model.number="form.maximumPendingRiskPercent" type="number" min="0" step="0.01" /></label>
            <label>Strategy heat (%) <input v-model.number="form.maximumStrategyRiskPercent" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label>Instrument heat (%) <input v-model.number="form.maximumInstrumentRiskPercent" type="number" min="0" step="0.01" /></label>
            <label>Currency stop-risk heat (%) <input v-model.number="form.maximumCurrencyStopRiskPercent" type="number" min="0" step="0.01" /></label>
            <label>Unallocated margin reserve (%) <input v-model.number="form.minimumUnallocatedMarginReservePercent" type="number" min="0" max="99" step="0.01" /></label>
          </div>
          <div class="row">
            <label>Correlation lookback (bars) <input v-model.number="form.correlationLookbackBars" type="number" min="2" step="1" /></label>
            <label>Minimum samples <input v-model.number="form.correlationMinimumSamples" type="number" min="2" step="1" /></label>
            <label>Soft / hard correlation <input v-model.number="form.correlationSoftThreshold" type="number" min="0" max="1" step="0.01" /> / <input v-model.number="form.correlationHardThreshold" type="number" min="0" max="1" step="0.01" /></label>
          </div>
          <div class="row">
            <label>Net currency exposure cap (% of equity)
              <input v-model.number="form.maximumNetCurrencyExposurePercent" type="number" min="0" step="1" />
            </label>
            <label>Gross currency exposure cap (% of equity)
              <input v-model.number="form.maximumGrossCurrencyExposurePercent" type="number" min="0" step="1" />
            </label>
          </div>
          <p class="muted">Currency stop-risk heat splits planned stop-loss risk 50/50 across a pair's base/quote
            currencies. Net/gross exposure caps use real notional exposure instead - a genuinely different
            (usually much larger, since it reflects leveraged notional) metric, not the same number twice.</p>
        </fieldset>
        <fieldset class="management-config">
          <legend>NEoWave structural analysis</legend>
          <div class="row checks">
            <label class="inline-check"><input v-model="form.neoWaveEnabled" type="checkbox" /> Causal monowave and hypothesis analysis</label>
            <label>Agent influence
              <select v-model="form.neoWaveEvidenceMode" :disabled="!form.neoWaveEnabled">
                <option>Disabled</option>
                <option>RecordOnly</option>
                <option>SoftConfidence</option>
                <option>SoftRiskReduction</option>
                <option>SoftConfidenceAndRisk</option>
              </select>
            </label>
            <label>Evidence interval <input v-model="form.neoWaveEvidenceInterval" type="text" style="width:4rem" placeholder="2h" :disabled="!form.neoWaveEnabled" /></label>
            <label>Minimum structural score <input v-model.number="form.neoWaveMinimumStructuralScore" type="number" min="0" max="100" step="1" :disabled="!form.neoWaveEnabled" /></label>
          </div>
          <div class="row checks">
            <label>Maximum trusted conflict <input v-model.number="form.neoWaveMaximumTrustedConflictScore" type="number" min="0" max="100" step="1" :disabled="!form.neoWaveEnabled" /></label>
            <label>Minimum risk multiplier <input v-model.number="form.neoWaveMinimumRiskMultiplier" type="number" min="0" max="1" step="0.05" :disabled="!form.neoWaveEnabled" /></label>
            <label>Maximum conflict reduction <input v-model.number="form.neoWaveMaximumConflictRiskReduction" type="number" min="0" max="1" step="0.05" :disabled="!form.neoWaveEnabled" /></label>
          </div>
          <p class="muted">Start with RecordOnly. Soft modes can adjust confidence or reduce risk, but the wave layer never creates an entry, never increases risk, and uncertain structure remains neutral.</p>
        </fieldset>
        <fieldset class="management-config">
          <legend>Value-location evidence and currency strength</legend>
          <div class="row checks">
            <label class="inline-check"><input v-model="form.valueLocationEvidenceEnabled" type="checkbox" /> Anchored value-location evidence</label>
            <label>Near-value threshold (ATR) <input v-model.number="form.valueLocationNearAtrThreshold" type="number" min="0" step="0.1" :disabled="!form.valueLocationEvidenceEnabled" /></label>
            <label>Stretched threshold (ATR) <input v-model.number="form.valueLocationStretchedAtrThreshold" type="number" min="0" step="0.1" :disabled="!form.valueLocationEvidenceEnabled" /></label>
            <label>Confidence adjustment <input v-model.number="form.valueLocationConfidenceAdjustment" type="number" min="0" max="25" step="0.5" :disabled="!form.valueLocationEvidenceEnabled" /></label>
          </div>
          <p class="muted">Soft, reason-coded evidence of price's location relative to an anchored session/week/swing
            value reference - a small confidence nudge only, never a hard veto.</p>
          <div class="row checks">
            <label class="inline-check"><input v-model="form.currencyStrengthEnabled" type="checkbox" /> Cross-market currency strength</label>
            <label>Interval <input v-model="form.currencyStrengthInterval" type="text" style="width:4rem" :disabled="!form.currencyStrengthEnabled" /></label>
            <label>Return lookback (bars) <input v-model.number="form.currencyStrengthReturnLookbackBars" type="number" min="1" step="1" :disabled="!form.currencyStrengthEnabled" /></label>
            <label>Volatility lookback (bars) <input v-model.number="form.currencyStrengthVolatilityLookbackBars" type="number" min="2" step="1" :disabled="!form.currencyStrengthEnabled" /></label>
            <label>Minimum coverage (%) <input v-model.number="form.currencyStrengthMinimumCoveragePercent" type="number" min="0" max="100" step="1" :disabled="!form.currencyStrengthEnabled" /></label>
          </div>
          <p class="muted">No basket editor yet - enabling this uses a single-instrument basket (the traded pair
            itself), which leave-one-out then excludes from its own differential, so it stays honestly
            unavailable rather than self-referential. Configuring real alternative pairs for a useful
            differential currently requires the API or CLI directly.</p>
        </fieldset>
        <fieldset class="management-config">
          <legend>Execution and financing</legend>
          <div class="row">
            <label>Fill model
              <select v-model="form.executionFillModel">
                <option>MidpointPlusConfiguredSpread</option>
                <option>VariableSyntheticSpread</option>
                <option>StressExecution</option>
              </select>
            </label>
            <label>Stress scenario
              <select v-model="form.stressExecutionScenario">
                <option>Base</option><option>SpreadDouble</option><option>SlippageTriple</option>
                <option>GapStress</option><option>StopAmendmentFailure</option><option>ConnectionLoss</option>
                <option>CorrelationShock</option><option>CombinedStress</option>
              </select>
            </label>
          </div>
          <div class="row">
            <label>Capacity (quantity/frame; 0 = unlimited) <input v-model.number="form.maximumFillQuantityPerFrame" type="number" min="0" step="0.01" /></label>
            <label>Maximum participation <input v-model.number="form.maximumFillParticipationFraction" type="number" min="0.01" max="1" step="0.01" /></label>
          </div>
          <div class="row">
            <label>Asian spread multiplier <input v-model.number="form.asianSessionSpreadMultiplier" type="number" min="0" step="0.01" /></label>
            <label>Rollover spread multiplier <input v-model.number="form.rolloverSpreadMultiplier" type="number" min="0" step="0.01" /></label>
            <label>Volatility slippage (range fraction) <input v-model.number="form.volatilitySlippageFraction" type="number" min="0" step="0.01" /></label>
            <label>Gap slippage (gap fraction) <input v-model.number="form.gapSlippageFraction" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row checks">
            <label><input v-model="form.financingEnabled" type="checkbox" /> Synthetic configured financing</label>
            <label>Long annual % <input v-model.number="form.financingLongAnnualPercent" type="number" step="0.01" :disabled="!form.financingEnabled" /></label>
            <label>Short annual % <input v-model.number="form.financingShortAnnualPercent" type="number" step="0.01" :disabled="!form.financingEnabled" /></label>
          </div>
        </fieldset>
        <fieldset>
          <legend>Account profit lock</legend>
          <div class="row">
            <label>
              Daily profit target
              <input
                v-model.number="form.dailyEquityProfitTarget"
                type="number"
                min="0"
                step="0.01"
              />
            </label>
            <label>
              Giveback activation
              <input
                v-model.number="form.dailyEquityGivebackActivation"
                type="number"
                min="0"
                step="0.01"
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
                step="0.01"
              />
            </label>
          </div>
          <p class="muted">
            Values are in account currency. Use 0 to disable. These rules pause new
            entries for the rest of the UTC day; open positions remain protected and managed.
          </p>
        </fieldset>
        <fieldset>
          <legend>Equity protection</legend>
          <div class="row">
            <label class="inline-check">
              <input v-model="form.equityProtectionEnabled" type="checkbox" />
              Enabled (persistent, non-daily-resetting high-watermark)
            </label>
          </div>
          <div class="row">
            <label>
              Activation profit (% of starting equity)
              <input
                v-model.number="form.equityProtectionActivationProfitPercent"
                type="number"
                min="0"
                step="0.01"
                :disabled="!form.equityProtectionEnabled"
              />
            </label>
            <label>
              Maximum giveback (% of peak equity)
              <input
                v-model.number="form.equityProtectionMaxGivebackPercent"
                type="number"
                min="0"
                step="0.01"
                :disabled="!form.equityProtectionEnabled"
              />
            </label>
          </div>
          <div class="row">
            <label>Action
              <select v-model="form.equityProtectionActionType" :disabled="!form.equityProtectionEnabled">
                <option>PauseNewEntries</option>
                <option>ReduceOpenPositions</option>
                <option>FlattenAllPositions</option>
                <option>ReduceFutureRisk</option>
              </select>
            </label>
            <label>Recovery bars
              <input
                v-model.number="form.equityProtectionRecoveryBars"
                type="number"
                min="0"
                step="1"
                :disabled="!form.equityProtectionEnabled"
              />
            </label>
          </div>
          <div class="row">
            <label>
              Reduction fraction (ReduceOpenPositions)
              <input
                v-model.number="form.equityProtectionReductionFraction"
                type="number"
                min="0"
                max="1"
                step="0.01"
                :disabled="!form.equityProtectionEnabled"
              />
            </label>
            <label>
              Future-risk multiplier (ReduceFutureRisk)
              <input
                v-model.number="form.equityProtectionFutureRiskMultiplier"
                type="number"
                min="0"
                max="1"
                step="0.01"
                :disabled="!form.equityProtectionEnabled"
              />
            </label>
          </div>
          <p class="muted">
            Unlike the daily lock above, this never resets and stays active until equity
            recovers for the configured number of consecutive bars. Risk multipliers can
            only reduce future position size, never increase it above base risk.
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
            <label>Fallback fixed quantity <input v-model.number="form.quantity" type="number" min="0.01" step="0.01" /></label>
          </div>
          <div class="row">
            <label>Risk per trade (%) <input v-model.number="form.riskPercentOfEquity" type="number" min="0" max="100" step="0.01" /></label>
            <label>Fixed cash risk <input v-model.number="form.fixedCashRisk" type="number" min="0" step="0.01" /></label>
          </div>
          <div class="row">
            <label>Minimum quantity <input v-model.number="form.minimumQuantity" type="number" min="0.01" step="0.01" /></label>
            <label>Maximum quantity (0 = none) <input v-model.number="form.maximumQuantity" type="number" min="0" step="0.01" /></label>
            <label>Quantity step <input v-model.number="form.quantityStep" type="number" min="0.01" step="0.01" /></label>
          </div>
          <div class="row">
            <label>Maximum account margin usage (%) <input v-model.number="form.maximumAccountMarginUsagePercent" type="number" min="0" max="100" step="0.01" /></label>
            <label>Maximum one-position margin (%) <input v-model.number="form.maximumSinglePositionMarginPercent" type="number" min="0" max="100" step="0.01" /></label>
          </div>
          <small class="muted">Fixed-fractional mode calculates quantity from account equity, original stop distance, currency conversion, costs and margin caps. It rounds down so planned risk is not exceeded. Quantity step accepts fractional lots (0.01).</small>
        </fieldset>
        <label>Balance <input v-model.number="form.startingBalance" type="number" min="0" step="0.01" /></label>
        <div class="row">
          <label>Leverage <input v-model.number="form.leverage" type="number" min="0" step="0.01" /></label>
          <label>Min R:R <input v-model.number="form.minimumRewardRisk" type="number" min="0" step="0.01" /></label>
        </div>
        <div class="row">
          <label>Spread bps <input v-model.number="form.spreadBasisPoints" type="number" min="0" step="0.01" /></label>
          <label>Slippage bps <input v-model.number="form.slippageBasisPoints" type="number" min="0" step="0.01" /></label>
        </div>
        <div class="row">
          <label>Commission <input v-model.number="form.commissionRate" type="number" min="0" step="0.00001" /></label>
          <label>Warm-up days <input v-model.number="form.warmupDays" type="number" min="0" step="1" /></label>
        </div>
        <small class="muted">Progress % includes warm-up candles; agents do not open trades until the evaluation From date.</small>
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
          <label><input v-model="form.captureMarketReplay" type="checkbox" /> Capture chart replay</label>
        </div>
        <p class="muted">
          Chart replay costs roughly a third of throughput to build (measured locally); turn it
          off for a faster run when you only need metrics/trades and won't view the replay chart.
        </p>
        <p class="muted">Historical bid/ask fills are not enabled until OANDA bid/ask history is wired.</p>
        <button type="submit" :disabled="busy || brokerCatalogLoading || !selectedBroker.isAvailable">
          {{ busy ? 'Starting…' : 'Run Simulation' }}
        </button>
        </div>
      </form>

      <section class="card runtime-card">
        <h3>Runtime</h3>
        <template v-if="job">
          <dl class="metrics">
            <div><dt>Simulation ID</dt><dd class="mono">{{ job.id }}</dd></div>
            <div><dt>Revision</dt><dd>{{ job.revision ?? 0 }}</dd></div>
            <div><dt>Status</dt><dd>{{ displayJobStatus }}</dd></div>
            <div><dt>Phase</dt><dd>{{ evaluationPhase ?? '—' }}</dd></div>
            <div><dt>Window</dt><dd>{{ dataWindowSummary ?? '—' }}</dd></div>
            <div><dt>Market time</dt><dd>{{ job.currentMarketTime ?? '—' }}</dd></div>
            <div><dt>Candles</dt><dd>{{ job.processedBaseCandles.toLocaleString() }}</dd></div>
            <div><dt>Progress</dt><dd>{{ job.progressPercent.toFixed(2) }}% <span class="muted">(includes warm-up)</span></dd></div>
            <div><dt>Candles/sec</dt><dd>{{ job.candlesPerSecond.toFixed(1) }}</dd></div>
            <div><dt>Data source</dt><dd>{{ job.dataSourceStatus ?? '—' }}</dd></div>
            <div><dt>Input request</dt><dd class="mono">{{ job.inputRequestId ?? '—' }}</dd></div>
            <div><dt>Configuration</dt><dd class="mono">{{ job.simulationConfigurationId ?? '—' }}</dd></div>
            <div><dt>Input hash</dt><dd class="mono">{{ job.inputHash ?? '—' }}</dd></div>
          </dl>
          <div v-if="jobFailureMessage" class="job-failure">
            <strong>Simulation failed</strong>
            <p>{{ jobFailureMessage }}</p>
            <ul v-if="strategyFailures.length">
              <li v-for="item in strategyFailures" :key="item">{{ item }}</li>
            </ul>
            <small class="muted">Failed jobs cannot be paused or resumed. Change settings if needed and run a new simulation.</small>
          </div>
          <p
            v-if="evaluationPhase?.startsWith('Warm-up')"
            class="muted"
          >
            Seeing dates before your From field is expected: warm-up loads history for structure/indicators.
            Entries only start once market time reaches {{ job.requestedFrom?.slice(0, 10) }}.
          </p>
          <div class="controls">
            <button type="button" :disabled="busy || !canPauseCompute" title="Only while prepare/download/warm-up/run is active" @click="control('pause')">Pause compute</button>
            <button type="button" :disabled="busy || !canResumeCompute" title="Only when status is Paused" @click="control('resume')">Resume compute</button>
            <button type="button" :disabled="busy || !canCancelCompute" title="Stop an in-flight job" @click="control('cancel')">Cancel</button>
          </div>

          <div v-if="displayJobStatus === 'Completed'" class="promote-block">
            <h4>Promote to live policy</h4>
            <p class="muted">
              Persists a <code>TradingPolicyProfile</code> built from this run's strategy/agent
              configuration into the shared policy store, using whatever calibration artifact IDs
              are currently attached above. Reviewed profiles need a separate approval step before
              live/shadow runtimes will use them unless "Approve for demo" is checked here.
            </p>
            <div class="promote-form">
              <label>
                Strategy
                <select v-model="promoteDraft.strategyId">
                  <option v-for="strategy in activeStrategies" :key="strategy.strategyId" :value="strategy.strategyId">
                    {{ strategy.strategyName }}
                  </option>
                </select>
              </label>
              <label>
                Revision
                <input v-model.number="promoteDraft.revision" type="number" min="1" step="1" />
              </label>
              <label class="checkbox-row">
                <input v-model="promoteDraft.approveForDemo" type="checkbox" />
                Approve for demo immediately
              </label>
              <label class="full">
                Description (optional)
                <input v-model.trim="promoteDraft.description" type="text" placeholder="e.g. EURUSD structural-confluence, Mar window" />
              </label>
            </div>
            <button type="button" class="button" :disabled="promoteBusy || !promoteDraft.strategyId" @click="promoteToLivePolicy">
              {{ promoteBusy ? 'Promoting…' : 'Promote to live policy' }}
            </button>
            <p v-if="promotedProfile" class="research-banner ok">
              Promoted <strong>{{ promotedProfile.strategyId }}</strong> rev {{ promotedProfile.revision }}
              as <strong>{{ promotedProfile.status }}</strong> (profile <code>{{ promotedProfile.profileId }}</code>).
              See it under Research &amp; calibration → Live trading policy profiles.
            </p>
          </div>
        </template>
        <p v-else class="muted">Start a simulation to stream progress. Refresh reconnects via SignalR or polling.</p>

        <template v-if="activeStrategies.length">
          <h3>Per-instrument progress</h3>
          <p class="muted">
            All instruments run on one canonical clock, so the bar advances together; each
            instrument's fund and trades still progress independently.
          </p>
          <div class="instrument-grid">
            <article v-for="strategy in activeStrategies" :key="strategy.strategyId" class="instrument-card">
              <header>
                <strong>{{ strategy.instrument ?? strategy.strategyName }}</strong>
                <span class="status-chip" :class="instrumentStatus(strategy).toLowerCase().replace(' ', '-')">
                  {{ instrumentStatus(strategy) }}
                </span>
              </header>
              <div class="ascii-bar mono">[{{ asciiBar(job?.progressPercent ?? 0) }}] {{ (job?.progressPercent ?? 0).toFixed(0) }}%</div>
              <p v-if="downloadStatus(strategy)" class="muted download-line">
                {{ downloadStatus(strategy)?.phase }}{{ downloadStatus(strategy)?.fromCache ? ' (cache)' : '' }}
                · {{ downloadStatus(strategy)?.candlesRead.toLocaleString() }} candles
                · {{ downloadStatus(strategy)?.pagesRead }} pages
              </p>
              <dl class="metrics compact-metrics">
                <div><dt>Balance</dt><dd>{{ metric(strategy, 'balance') }}</dd></div>
                <div><dt>Equity</dt><dd>{{ metric(strategy, 'equity') }}</dd></div>
                <div><dt>Net P/L</dt><dd>{{ metric(strategy, 'netProfit') }}</dd></div>
                <div><dt>Trades</dt><dd>{{ metric(strategy, 'completedTrades') }}</dd></div>
                <div><dt>Open positions</dt><dd>{{ metric(strategy, 'openPositions') }}</dd></div>
              </dl>
              <p v-if="strategy.lastError" class="error">{{ strategy.lastError }}</p>
            </article>
          </div>
        </template>

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
            <div><dt>Entry / current regime</dt><dd>{{ strategy.openPositionManagement.entryRegime ?? '—' }} / {{ strategy.openPositionManagement.currentRegime ?? '—' }}</dd></div>
            <div><dt>Regime confidence</dt><dd>{{ strategy.openPositionManagement.regimeConfidence?.toFixed(1) ?? '—' }}</dd></div>
            <div><dt>Base / final risk budget</dt><dd>{{ strategy.openPositionManagement.baseRiskBudget?.toFixed(2) ?? '—' }} / {{ strategy.openPositionManagement.finalRiskBudget?.toFixed(2) ?? '—' }}</dd></div>
            <div><dt>Final risk multiplier</dt><dd>{{ strategy.openPositionManagement.finalRiskMultiplier?.toFixed(3) ?? '—' }}×</dd></div>
            <div><dt>Risk multipliers</dt><dd>{{ Object.entries(strategy.openPositionManagement.riskMultipliers).map(([key, value]) => `${key} ${value.toFixed(2)}×`).join(' · ') }}</dd></div>
            <div><dt>Raw / allocated quantity</dt><dd>{{ strategy.openPositionManagement.rawQuantity ?? '—' }} / {{ strategy.openPositionManagement.allocatedQuantity ?? '—' }}</dd></div>
            <div><dt>Planned stop risk</dt><dd>{{ strategy.openPositionManagement.plannedStopRisk?.toFixed(2) ?? '—' }}</dd></div>
            <div><dt>Reservation / cluster</dt><dd>{{ strategy.openPositionManagement.portfolioReservationId ?? '—' }} / {{ strategy.openPositionManagement.correlationClusterId ?? '—' }}</dd></div>
          </dl>
          <small v-else class="muted">No open position.</small>
        </div>

        <h3>Equity protection</h3>
        <div v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-equity-protection`" class="management-runtime">
          <strong>{{ strategy.strategyName }}</strong>
          <dl v-if="strategy.equityProtection" class="metrics compact-metrics">
            <div><dt>Peak equity</dt><dd>{{ strategy.equityProtection.peakEquity.toFixed(2) }}</dd></div>
            <div><dt>Drawdown from peak</dt><dd>{{ strategy.equityProtection.drawdownPercent.toFixed(2) }}%</dd></div>
            <div><dt>Current risk multiplier</dt><dd>{{ strategy.equityProtection.currentRiskMultiplier.toFixed(2) }}×</dd></div>
            <div><dt>Active tiers</dt><dd>{{ strategy.equityProtection.activatedTierIds.length ? strategy.equityProtection.activatedTierIds.join(', ') : 'none' }}</dd></div>
            <div><dt>New entries</dt><dd>{{ strategy.equityProtection.newEntriesPaused ? 'Paused' : 'Allowed' }}</dd></div>
          </dl>
          <small v-else class="muted">Not enabled, or no equity observed yet.</small>
        </div>

        <h3>Shared portfolio risk</h3>
        <div v-for="strategy in activeStrategies" :key="`${strategy.strategyId}-portfolio-risk`" class="management-runtime">
          <strong>{{ strategy.strategyName }}</strong>
          <dl v-if="strategy.portfolioRisk" class="metrics compact-metrics">
            <div><dt>Open / pending / total heat</dt><dd>{{ strategy.portfolioRisk.openHeat.toFixed(2) }} / {{ strategy.portfolioRisk.pendingHeat.toFixed(2) }} / {{ strategy.portfolioRisk.totalHeat.toFixed(2) }}</dd></div>
            <div><dt>Total heat</dt><dd>{{ strategy.portfolioRisk.totalHeatPercent.toFixed(3) }}% of equity</dd></div>
            <div><dt>Strategy / instrument heat</dt><dd>{{ strategy.portfolioRisk.strategyHeat.toFixed(2) }} / {{ strategy.portfolioRisk.instrumentHeat.toFixed(2) }}</dd></div>
            <div><dt>Currency risk</dt><dd>{{ Object.entries(strategy.portfolioRisk.currencyRisk).map(([key, value]) => `${key} ${value.toFixed(2)}`).join(' · ') || 'none' }}</dd></div>
            <div><dt>Cluster heat</dt><dd>{{ Object.entries(strategy.portfolioRisk.clusterHeat).map(([key, value]) => `${key} ${value.toFixed(2)}`).join(' · ') || 'none' }}</dd></div>
            <div><dt>Used / reserved margin</dt><dd>{{ strategy.portfolioRisk.marginUsed.toFixed(2) }} / {{ strategy.portfolioRisk.reservedMargin.toFixed(2) }}</dd></div>
            <div><dt>Unallocated margin</dt><dd>{{ strategy.portfolioRisk.unallocatedMargin.toFixed(2) }}</dd></div>
            <div><dt>Account peak / protected floor</dt><dd>{{ strategy.portfolioRisk.accountPeakEquity.toFixed(2) }} / {{ strategy.portfolioRisk.accountProtectedFloor.toFixed(2) }}</dd></div>
            <div><dt>Account protection tiers</dt><dd>{{ strategy.portfolioRisk.accountActivatedTierIds.join(', ') || 'none' }}</dd></div>
          </dl>
          <small v-else class="muted">Independent account mode.</small>
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
            <template v-if="item.trade.exitPolicy">
              · {{ item.trade.exitPolicy }} · planned {{ item.trade.plannedR?.toFixed(2) ?? '—' }}R · realized {{ item.trade.realizedR?.toFixed(2) ?? '—' }}R
            </template>
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
          <label v-if="chartInstruments.length > 1">
            Chart instrument
            <select v-model="selectedChartInstrument">
              <option v-for="instrument in chartInstruments" :key="instrument" :value="instrument">
                {{ instrument }}
              </option>
            </select>
          </label>
        </div>
        <p v-if="chartInstruments.length > 1" class="muted">
          This run trades {{ chartInstruments.length }} instruments on one shared
          portfolio clock; the chart shows one at a time.
        </p>
        <AnalysisChart
          v-if="replayFrames.length"
          :frames="activeFrames"
          :selected-index="chartIndex"
          :window-size="100"
          :layers="replayLayers"
          :trades="flatTrades"
          :available-intervals="availableIntervals"
          :active-interval="activeInterval"
          :can-go-back="canGoBack"
          :centered="centeredView"
          @switch-interval="onSwitchInterval"
          @drill-candle="onDrillCandle"
          @drill-back="onDrillBack"
        />

        <dl v-if="currentReplayRow" class="metrics">
          <div><dt>Index</dt><dd>{{ replayIndex + 1 }} / {{ filteredReplayRows.length }}</dd></div>
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
              {{ item.instrument }} · {{ formatSimulationStatus(item.status) }} · {{ item.progressPercent.toFixed(0) }}%
            </button>
          </li>
        </ul>
        <button type="button" class="secondary" @click="refreshJobList">Refresh jobs</button>
      </section>
    </div>
    <AlfonsoTrendComparison v-else-if="simulatorMode === 'trend'" />
    <SimulationExperimentPanel v-else @open-report="experimentId => emit('open-experiment-report', experimentId)" />
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
.config-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
}
.config-heading h3 { margin: 0; }
.preset-picker {
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 0.55rem;
  display: grid;
  gap: 0.65rem;
  margin: 0;
  padding: 0.65rem 0.75rem 0.75rem;
}
.preset-picker legend {
  color: #bfdbfe;
  padding: 0 0.3rem;
}
.preset-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(9.5rem, 1fr));
  gap: 0.5rem;
}
.preset-card {
  display: grid;
  gap: 0.3rem;
  align-content: start;
  min-height: 7.5rem;
  padding: 0.6rem 0.65rem;
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 0.55rem;
  background: rgba(12, 16, 22, 0.85);
  color: inherit;
  text-align: left;
  cursor: pointer;
}
.preset-card:hover {
  border-color: rgba(139, 164, 255, 0.4);
  background: rgba(22, 30, 40, 0.95);
}
.preset-card.active {
  border-color: rgba(71, 215, 172, 0.55);
  background: rgba(71, 215, 172, 0.08);
  box-shadow: inset 0 0 0 1px rgba(71, 215, 172, 0.15);
}
.preset-card strong {
  font-size: 0.88rem;
  line-height: 1.2;
}
.preset-card small {
  color: var(--muted, #8b93a7);
  font-size: 0.72rem;
  line-height: 1.35;
}
.preset-badge {
  justify-self: start;
  padding: 0.1rem 0.4rem;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.12);
  background: rgba(255, 255, 255, 0.04);
  color: #9fb0bc;
  font-size: 0.65rem;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}
.preset-card.active .preset-badge {
  border-color: rgba(71, 215, 172, 0.35);
  color: var(--mint, #47d7ac);
}
.preset-banner {
  border: 1px solid rgba(71, 215, 172, 0.28);
  background: rgba(71, 215, 172, 0.07);
  border-radius: 0.65rem;
  padding: 0.65rem 0.75rem;
  display: grid;
  gap: 0.35rem;
}
.preset-banner strong { color: var(--mint, #47d7ac); font-size: 0.9rem; }
.preset-line {
  margin: 0;
  font-size: 0.78rem;
  line-height: 1.35;
  word-break: break-word;
}
.run-row {
  display: grid;
  grid-template-columns: 1.4fr 1fr;
  gap: 0.5rem;
}
.run-row button { width: 100%; }
.advanced-config {
  display: grid;
  gap: 0.65rem;
  padding-top: 0.35rem;
  border-top: 1px solid rgba(255, 255, 255, 0.08);
}
.simulator-status {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.85rem;
  padding: 0.5rem 0.75rem;
  border-radius: 0.5rem;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.08);
}
.simulator-mode {
  display: flex;
  gap: 0.2rem;
  padding: 0.2rem;
  border-radius: 0.6rem;
  background: rgba(0, 0, 0, 0.24);
}
.simulator-mode button {
  background: transparent;
  color: var(--muted, #8b93a7);
  white-space: nowrap;
}
.simulator-mode button.active {
  background: var(--mint, #47d7ac);
  color: #07110e;
  font-weight: 800;
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
.instrument-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: 0.6rem; }
.instrument-card { display: grid; gap: 0.4rem; padding: 0.65rem; border: 1px solid rgba(255, 255, 255, 0.085); border-radius: 0.52rem; background: rgba(0, 0, 0, 0.12); }
.instrument-card header { display: flex; justify-content: space-between; align-items: center; gap: 0.5rem; }
.download-line { margin: 0; font-size: 0.72rem; }
.ascii-bar { padding: 0.3rem 0.5rem; overflow-x: auto; border-radius: 0.4rem; color: #7ee2b8; background: rgba(0, 0, 0, 0.35); font-size: 0.75rem; letter-spacing: -0.02em; white-space: pre; }
.status-chip { padding: 0.16rem 0.44rem; border-radius: 999px; color: #9fb0c3; background: rgba(255, 255, 255, 0.06); font-size: 0.66rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.03em; }
.status-chip.running { color: #75e6c1; background: rgba(71, 215, 172, 0.1); }
.status-chip.warming-up, .status-chip.preparing { color: #93c5fd; background: rgba(96, 165, 250, 0.1); }
.status-chip.completed { color: #a7f3d0; background: rgba(71, 215, 172, 0.08); }
.status-chip.failed { color: #fca5a5; background: rgba(248, 113, 113, 0.1); }
.status-chip.paused, .status-chip.cancelled, .status-chip.incomplete { color: #fde68a; background: rgba(245, 158, 11, 0.08); }
.job-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.35rem; }
.linkish {
  background: transparent;
  border: 1px solid rgba(255, 255, 255, 0.1);
  width: 100%;
  text-align: left;
}
.error { color: #f87171; margin: 0; }
.job-failure {
  display: grid;
  gap: 0.35rem;
  margin: 0.35rem 0 0.15rem;
  padding: 0.65rem 0.75rem;
  border: 1px solid rgba(248, 113, 113, 0.35);
  border-radius: 0.55rem;
  background: rgba(248, 113, 113, 0.08);
}
.job-failure strong { color: #fca5a5; }
.job-failure p {
  margin: 0;
  color: #fecaca;
  font-size: 0.88rem;
  line-height: 1.4;
  word-break: break-word;
}
.job-failure ul {
  margin: 0;
  padding-left: 1.1rem;
  color: #fecaca;
  font-size: 0.82rem;
}
.promote-block {
  display: grid;
  gap: 0.5rem;
  margin: 0.5rem 0 0;
  padding: 0.65rem 0.75rem;
  border: 1px solid rgba(94, 234, 212, 0.25);
  border-radius: 0.55rem;
  background: rgba(94, 234, 212, 0.06);
}
.promote-block h4 { margin: 0; }
.promote-form {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr));
  gap: 0.5rem;
}
.promote-form label.full { grid-column: 1 / -1; }
@media (max-width: 1100px) {
  .simulator-grid { grid-template-columns: 1fr; }
}
</style>
