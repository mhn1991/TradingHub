<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import type { SimulationStrategyProfile } from '../../types/simulation-experiments'

const props = defineProps<{
  /** Instrument prefill from the main simulator form, e.g. "FX:EUR/USD". */
  instrument: string
}>()
const emit = defineEmits<{
  (event: 'saved', profile: SimulationStrategyProfile): void
  (event: 'close'): void
}>()

// ---------------------------------------------------------------------------
// Wizard state
// ---------------------------------------------------------------------------

type AgentKind = 'LegacyProgressive' | 'ImprovedProgressive' | 'StructuralConfluence'
type CciMode = 'Disabled' | 'Soft' | 'Required'

const step = ref(1)
const totalSteps = 4
const saving = ref(false)
const error = ref<string | null>(null)

const timeframes = reactive({
  // Higher-timeframe directional context ("trend" for progressive, "context" for structural).
  context: '2h',
  // Optional extra context/secondary-trend intervals, comma separated ("1h, 4h").
  additionalContext: '1h',
  setup: '30m',
  // Progressive-only confirmation layer; structural has no confirmation interval role.
  confirmation: '15m',
  entry: '5m',
})

const agent = reactive({
  kind: 'ImprovedProgressive' as AgentKind,
  quantity: 1000,
  minimumRewardRisk: 1.5,
  stopBufferAtr: 0.2,
  targetBufferAtr: 0.1,
  // Progressive-only knobs.
  minimumTrendConfidence: 55,
  minimumSetupConfidence: 50,
  minimumEntryConfidence: 58,
  priceActionConfirmation: 'Soft' as CciMode,
  enableDmiConfirmation: true,
  strongOppositionVeto: true,
  supplyDemandEvidence: true,
  liquidityEvidence: true,
  // Structural-only knobs.
  sweepEnabled: true,
  sweepCciMode: 'Soft' as CciMode,
  sweepSupplyDemandConfluence: 'Preferred' as 'Disabled' | 'Preferred' | 'Required',
  pullbackEnabled: true,
  pullbackCciMode: 'Soft' as CciMode,
  breakRetestEnabled: true,
  breakRetestCciMode: 'Soft' as CciMode,
  adaptiveTargets: false,
})

const attachments = reactive({
  customManagement: false,
  management: {
    mode: 'StructureAtr' as 'Disabled' | 'BreakEvenOnly' | 'StructureAtr',
    breakEvenActivationR: 1,
    structureTrailActivationR: 1.5,
    atrBufferMultiplier: 0.25,
    minimumStopImprovementAtr: 0.05,
    exitOnAdverseStructureBreak: false,
    enableScaleOut: false,
    minimumRunnerFraction: 0.4,
    supplyDemandManagementEnabled: false,
    liquidityManagementEnabled: false,
  },
  calibrationEnabled: false,
  calibration: {
    mode: 'TrainFreshAndUseForHeldOutEvaluation' as
      | 'ReuseSpecifiedArtifacts'
      | 'TrainFreshAndUseForHeldOutEvaluation'
      | 'TrainFreshPendingReviewOnly',
    internalFolds: 5,
    internalEmbargoHours: 24,
    artifactIds: '',
  },
  // Analysis modules; may be forced on by the agent's own requirements (see below).
  supplyDemandAnalysis: false,
  liquidityAnalysis: false,
})

const save = reactive({
  name: '',
  instrument: props.instrument || 'FX:EUR/USD',
  tags: '',
})

// ---------------------------------------------------------------------------
// Derived requirements
// ---------------------------------------------------------------------------

const isStructural = computed(() => agent.kind === 'StructuralConfluence')
const isProgressive = computed(() => !isStructural.value)

/** Mirror of the backend's ValidateForExecution coupling rules. */
const requiredAnalysis = computed(() => {
  if (isStructural.value) {
    return {
      liquidity: agent.sweepEnabled || agent.breakRetestEnabled,
      supplyDemand:
        agent.pullbackEnabled ||
        (agent.sweepEnabled && agent.sweepSupplyDemandConfluence !== 'Disabled'),
    }
  }
  // Progressive agents consume these as evidence when the toggles are on.
  return { liquidity: agent.liquidityEvidence, supplyDemand: agent.supplyDemandEvidence }
})

const effectiveSupplyDemandAnalysis = computed(
  () => attachments.supplyDemandAnalysis || requiredAnalysis.value.supplyDemand,
)
const effectiveLiquidityAnalysis = computed(
  () => attachments.liquidityAnalysis || requiredAnalysis.value.liquidity,
)

const structuralPlaybookCount = computed(() =>
  [agent.sweepEnabled, agent.pullbackEnabled, agent.breakRetestEnabled].filter(Boolean).length,
)

// ---------------------------------------------------------------------------
// Interval parsing ("2h" -> {value: 2, unit: 'Hour'})
// ---------------------------------------------------------------------------

interface WireInterval { value: number; unit: string }

const unitMap: Record<string, string> = {
  s: 'Second', m: 'Minute', h: 'Hour', d: 'Day', w: 'Week', mo: 'Month',
}

function parseInterval(text: string, field: string): WireInterval {
  const match = /^\s*(\d+)\s*(mo|[smhdw])\s*$/i.exec(text)
  if (!match) {
    throw new Error(`${field}: "${text}" is not a valid interval. Use forms like 30s, 15m, 2h, 1d.`)
  }
  return { value: Number(match[1]), unit: unitMap[match[2]!.toLowerCase()]! }
}

function parseIntervalList(text: string, field: string): WireInterval[] {
  return text
    .split(',')
    .map(part => part.trim())
    .filter(Boolean)
    .map(part => parseInterval(part, field))
}

const unitSeconds: Record<string, number> = {
  Second: 1, Minute: 60, Hour: 3600, Day: 86400, Week: 604800, Month: 2592000,
}

function seconds(interval: WireInterval): number {
  return interval.value * unitSeconds[interval.unit]!
}

/** Mirrors the backend's interval-ordering validation so failures surface per step, not at save. */
function validateIntervalOrdering(): string | null {
  const context = parseInterval(timeframes.context, 'Context/trend interval')
  const setup = parseInterval(timeframes.setup, 'Setup interval')
  const entry = parseInterval(timeframes.entry, 'Entry/trigger interval')
  const additional = parseIntervalList(timeframes.additionalContext, 'Additional context intervals')

  if (isProgressive.value) {
    const confirmation = parseInterval(timeframes.confirmation, 'Confirmation interval')
    if (seconds(entry) >= seconds(confirmation)) return 'Entry must be finer than confirmation.'
    if (seconds(confirmation) >= seconds(context)) return 'Confirmation must be finer than the context/trend interval.'
    if (seconds(setup) >= seconds(context) || seconds(setup) <= seconds(confirmation)) {
      return 'Setup must sit strictly between confirmation and the context/trend interval.'
    }
    for (const interval of additional) {
      if (seconds(interval) >= seconds(context) || seconds(interval) <= seconds(confirmation)) {
        return 'Additional context (secondary trend) intervals must sit strictly between confirmation and the context/trend interval.'
      }
    }
    const all = [entry, confirmation, setup, context, ...additional].map(seconds)
    if (new Set(all).size !== all.length) return 'Each timeframe role needs a distinct interval.'
    return null
  }

  if (seconds(entry) >= seconds(setup)) return 'Entry/trigger must be finer than setup.'
  if (seconds(setup) >= seconds(context)) return 'Setup must be finer than the context interval.'
  for (const interval of additional) {
    if (seconds(interval) < seconds(setup)) return 'Additional context intervals must be no finer than setup.'
    if ([context, setup, entry].some(role => seconds(role) === seconds(interval))) {
      return 'Additional context intervals must be distinct from the context, setup, and entry roles.'
    }
  }
  const distinct = additional.map(seconds)
  if (new Set(distinct).size !== distinct.length) return 'Additional context intervals must be unique.'
  return null
}

// ---------------------------------------------------------------------------
// Step validation and navigation
// ---------------------------------------------------------------------------

function validateStep(current: number): string | null {
  try {
    if (current === 1) {
      parseInterval(timeframes.context, 'Context/trend interval')
      parseIntervalList(timeframes.additionalContext, 'Additional context intervals')
      parseInterval(timeframes.setup, 'Setup interval')
      if (isProgressive.value) parseInterval(timeframes.confirmation, 'Confirmation interval')
      parseInterval(timeframes.entry, 'Entry/trigger interval')
    }
    if (current === 2) {
      if (agent.quantity <= 0) return 'Quantity must be positive.'
      if (agent.minimumRewardRisk <= 0) return 'Minimum reward:risk must be positive.'
      if (isStructural.value && structuralPlaybookCount.value === 0) {
        return 'At least one structural playbook must be enabled.'
      }
      // The ordering rules depend on the agent family, so they are checked here (after the
      // agent is chosen), not in step 1.
      const ordering = validateIntervalOrdering()
      if (ordering) return `${ordering} Adjust the intervals in step 1.`
    }
    if (current === 3 && attachments.calibrationEnabled &&
        attachments.calibration.mode === 'ReuseSpecifiedArtifacts' &&
        !attachments.calibration.artifactIds.trim()) {
      return 'Artifact reuse requires at least one artifact ID.'
    }
    if (current === 4) {
      if (!save.name.trim()) return 'A profile name is required.'
      if (!save.instrument.trim()) return 'An instrument is required.'
    }
    return null
  } catch (problem) {
    return problem instanceof Error ? problem.message : String(problem)
  }
}

function next(): void {
  const problem = validateStep(step.value)
  if (problem) { error.value = problem; return }
  error.value = null
  if (step.value < totalSteps) step.value += 1
}

function back(): void {
  error.value = null
  if (step.value > 1) step.value -= 1
}

// ---------------------------------------------------------------------------
// Payload assembly and save
// ---------------------------------------------------------------------------

function buildAgentPayload(): Record<string, unknown> {
  if (isStructural.value) {
    const structural: Record<string, unknown> = {
      contextInterval: parseInterval(timeframes.context, 'Context interval'),
      additionalContextIntervals: parseIntervalList(timeframes.additionalContext, 'Additional context intervals'),
      setupInterval: parseInterval(timeframes.setup, 'Setup interval'),
      triggerInterval: parseInterval(timeframes.entry, 'Trigger interval'),
      quantity: agent.quantity,
      minimumRewardRisk: agent.minimumRewardRisk,
      stopBufferAtr: agent.stopBufferAtr,
      targetBufferAtr: agent.targetBufferAtr,
      liquiditySweepReversal: {
        enabled: agent.sweepEnabled,
        cciMode: agent.sweepCciMode,
        supplyDemandConfluence: agent.sweepSupplyDemandConfluence,
      },
      supplyDemandPullback: { enabled: agent.pullbackEnabled, cciMode: agent.pullbackCciMode },
      liquidityBreakRetest: { enabled: agent.breakRetestEnabled, cciMode: agent.breakRetestCciMode },
    }
    if (agent.adaptiveTargets) {
      // The backend rejects adaptive targets under the v1 strategy version by design.
      structural.strategyVersion = 'structural-confluence-v2'
      structural.adaptiveTargetManagement = { enabled: true }
    }
    return { kind: 'StructuralConfluence', structuralConfluence: structural }
  }

  return {
    kind: agent.kind,
    progressive: {
      trendInterval: parseInterval(timeframes.context, 'Trend interval'),
      secondaryTrendIntervals: parseIntervalList(timeframes.additionalContext, 'Secondary trend intervals'),
      setupIntervals: [parseInterval(timeframes.setup, 'Setup interval')],
      confirmationInterval: parseInterval(timeframes.confirmation, 'Confirmation interval'),
      entryInterval: parseInterval(timeframes.entry, 'Entry interval'),
      quantity: agent.quantity,
      minimumRewardRisk: agent.minimumRewardRisk,
      stopBufferAtr: agent.stopBufferAtr,
      targetBufferAtr: agent.targetBufferAtr,
      minimumTrendConfidence: agent.minimumTrendConfidence,
      minimumSetupConfidence: agent.minimumSetupConfidence,
      minimumEntryConfidence: agent.minimumEntryConfidence,
      priceActionConfirmation: agent.priceActionConfirmation,
      enableDmiConfirmation: agent.enableDmiConfirmation,
      strongOppositionVeto: agent.strongOppositionVeto,
      supplyDemandEnabled: agent.supplyDemandEvidence,
      liquidityEnabled: agent.liquidityEvidence,
    },
  }
}

function buildManagementPayload(): Record<string, unknown> {
  if (!attachments.customManagement) return {}
  const management = attachments.management
  return {
    mode: management.mode,
    breakEvenActivationR: management.breakEvenActivationR,
    structureTrailActivationR: management.structureTrailActivationR,
    atrBufferMultiplier: management.atrBufferMultiplier,
    minimumStopImprovementAtr: management.minimumStopImprovementAtr,
    exitOnAdverseStructureBreak: management.exitOnAdverseStructureBreak,
    enableScaleOut: management.enableScaleOut,
    minimumRunnerFraction: management.minimumRunnerFraction,
    supplyDemandManagementEnabled: management.supplyDemandManagementEnabled,
    liquidityManagementEnabled: management.liquidityManagementEnabled,
  }
}

function buildCalibrationPayload(): Record<string, unknown> {
  if (!attachments.calibrationEnabled) {
    return { mode: 'Disabled', internalFolds: 5, internalEmbargoHours: 24, artifactIds: [] }
  }
  return {
    mode: attachments.calibration.mode,
    internalFolds: attachments.calibration.internalFolds,
    internalEmbargoHours: attachments.calibration.internalEmbargoHours,
    artifactIds: attachments.calibration.artifactIds
      .split(',')
      .map(part => part.trim())
      .filter(Boolean),
  }
}

async function saveProfile(): Promise<void> {
  const problem = validateStep(4)
  if (problem) { error.value = problem; return }
  saving.value = true
  error.value = null
  try {
    const body = {
      profileId: crypto.randomUUID(),
      revision: 1,
      name: save.name.trim(),
      agent: buildAgentPayload(),
      instrument: { value: save.instrument.trim() },
      analysis: {
        supplyDemand: { enabled: effectiveSupplyDemandAnalysis.value },
        liquidity: { enabled: effectiveLiquidityAnalysis.value },
      },
      runtime: { options: {} },
      management: buildManagementPayload(),
      calibration: buildCalibrationPayload(),
      tags: [...new Set(save.tags.split(',').map(part => part.trim()).filter(Boolean))],
      contentHash: '',
    }
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulation-profiles`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (!response.ok) {
      const detail = await response.json().catch(() => null) as { error?: string } | null
      throw new Error(detail?.error ?? `Saving the profile failed with HTTP ${response.status}.`)
    }
    emit('saved', await response.json() as SimulationStrategyProfile)
  } catch (problem2) {
    error.value = problem2 instanceof Error ? problem2.message : String(problem2)
  } finally {
    saving.value = false
  }
}

const agentLabel = computed(() => {
  switch (agent.kind) {
    case 'LegacyProgressive': return 'Legacy progressive'
    case 'ImprovedProgressive': return 'Improved progressive'
    case 'StructuralConfluence': return 'Structural confluence'
  }
})
</script>

<template>
  <section class="pbw" aria-label="Interactive profile builder">
    <header class="pbw-head">
      <div>
        <h3>Build agent profile</h3>
        <ol class="pbw-steps">
          <li :class="{ active: step === 1, done: step > 1 }">Timeframes</li>
          <li :class="{ active: step === 2, done: step > 2 }">Agent</li>
          <li :class="{ active: step === 3, done: step > 3 }">Attachments</li>
          <li :class="{ active: step === 4 }">Save</li>
        </ol>
      </div>
      <button type="button" class="pbw-close" @click="emit('close')">Close</button>
    </header>

    <p v-if="error" class="pbw-error">{{ error }}</p>

    <!-- Step 1: timeframes -->
    <div v-if="step === 1" class="pbw-body">
      <p class="pbw-hint">
        Define the timeframe ladder once — it is mapped onto whichever agent you pick next
        (trend/secondary/setup/confirmation/entry for progressive agents; context/setup/trigger
        for structural confluence).
      </p>
      <div class="pbw-grid">
        <label>
          Context / trend interval
          <input v-model.trim="timeframes.context" placeholder="2h" spellcheck="false" />
        </label>
        <label>
          Additional context intervals
          <input v-model.trim="timeframes.additionalContext" placeholder="1h, 4h (optional)" spellcheck="false" />
        </label>
        <label>
          Setup interval
          <input v-model.trim="timeframes.setup" placeholder="30m" spellcheck="false" />
        </label>
        <label>
          Confirmation interval
          <input v-model.trim="timeframes.confirmation" placeholder="15m" spellcheck="false" />
          <small>Used by legacy/improved agents only.</small>
        </label>
        <label>
          Entry / trigger interval
          <input v-model.trim="timeframes.entry" placeholder="5m" spellcheck="false" />
        </label>
      </div>
      <p class="pbw-hint">Formats: <code>30s</code>, <code>15m</code>, <code>2h</code>, <code>1d</code>. Lists are comma separated.</p>
    </div>

    <!-- Step 2: agent -->
    <div v-if="step === 2" class="pbw-body">
      <div class="pbw-agent-cards">
        <button
          v-for="option in ([
            { kind: 'LegacyProgressive', title: 'Legacy', text: 'Original progressive MTF stack. Baseline behavior.' },
            { kind: 'ImprovedProgressive', title: 'Improved', text: 'Progressive stack with the full evidence suite (S&D, liquidity, RSI/Bollinger, value location).' },
            { kind: 'StructuralConfluence', title: 'Structural', text: 'Playbook-driven: liquidity sweeps, S&D pullbacks, accepted break/retest.' },
          ] as const)"
          :key="option.kind"
          type="button"
          class="pbw-agent-card"
          :class="{ selected: agent.kind === option.kind }"
          @click="agent.kind = option.kind"
        >
          <strong>{{ option.title }}</strong>
          <span>{{ option.text }}</span>
        </button>
      </div>

      <div class="pbw-grid">
        <label>Quantity<input v-model.number="agent.quantity" type="number" min="1" step="1" /></label>
        <label>Minimum reward:risk<input v-model.number="agent.minimumRewardRisk" type="number" min="0.1" step="0.1" /></label>
        <label>Stop buffer (ATR)<input v-model.number="agent.stopBufferAtr" type="number" min="0" step="0.05" /></label>
        <label>Target buffer (ATR)<input v-model.number="agent.targetBufferAtr" type="number" min="0" step="0.05" /></label>
      </div>

      <template v-if="isProgressive">
        <h4>Confidence gates</h4>
        <div class="pbw-grid">
          <label>Trend<input v-model.number="agent.minimumTrendConfidence" type="number" min="0" max="100" /></label>
          <label>Setup<input v-model.number="agent.minimumSetupConfidence" type="number" min="0" max="100" /></label>
          <label>Entry<input v-model.number="agent.minimumEntryConfidence" type="number" min="0" max="100" /></label>
          <label>
            Price-action confirmation
            <select v-model="agent.priceActionConfirmation">
              <option value="Disabled">Disabled</option>
              <option value="Soft">Soft</option>
              <option value="Required">Required</option>
            </select>
          </label>
        </div>
        <div class="pbw-checks">
          <label><input v-model="agent.enableDmiConfirmation" type="checkbox" /> DMI confirmation</label>
          <label><input v-model="agent.strongOppositionVeto" type="checkbox" /> Strong-opposition veto</label>
          <label><input v-model="agent.supplyDemandEvidence" type="checkbox" /> Supply/demand evidence</label>
          <label><input v-model="agent.liquidityEvidence" type="checkbox" /> Liquidity evidence</label>
        </div>
      </template>

      <template v-else>
        <h4>Playbooks <small v-if="structuralPlaybookCount === 0" class="pbw-error-inline">enable at least one</small></h4>
        <div class="pbw-playbooks">
          <div class="pbw-playbook" :class="{ off: !agent.sweepEnabled }">
            <label class="pbw-playbook-head">
              <input v-model="agent.sweepEnabled" type="checkbox" /> Liquidity-sweep reversal
            </label>
            <div v-if="agent.sweepEnabled" class="pbw-grid tight">
              <label>CCI mode
                <select v-model="agent.sweepCciMode">
                  <option value="Disabled">Disabled</option><option value="Soft">Soft</option><option value="Required">Required</option>
                </select>
              </label>
              <label>S&amp;D confluence
                <select v-model="agent.sweepSupplyDemandConfluence">
                  <option value="Disabled">Disabled</option><option value="Preferred">Preferred</option><option value="Required">Required</option>
                </select>
              </label>
            </div>
          </div>
          <div class="pbw-playbook" :class="{ off: !agent.pullbackEnabled }">
            <label class="pbw-playbook-head">
              <input v-model="agent.pullbackEnabled" type="checkbox" /> Supply/demand pullback
            </label>
            <div v-if="agent.pullbackEnabled" class="pbw-grid tight">
              <label>CCI mode
                <select v-model="agent.pullbackCciMode">
                  <option value="Disabled">Disabled</option><option value="Soft">Soft</option><option value="Required">Required</option>
                </select>
              </label>
            </div>
          </div>
          <div class="pbw-playbook" :class="{ off: !agent.breakRetestEnabled }">
            <label class="pbw-playbook-head">
              <input v-model="agent.breakRetestEnabled" type="checkbox" /> Accepted break / retest
            </label>
            <div v-if="agent.breakRetestEnabled" class="pbw-grid tight">
              <label>CCI mode
                <select v-model="agent.breakRetestCciMode">
                  <option value="Disabled">Disabled</option><option value="Soft">Soft</option><option value="Required">Required</option>
                </select>
              </label>
            </div>
          </div>
        </div>
        <div class="pbw-checks">
          <label>
            <input v-model="agent.adaptiveTargets" type="checkbox" />
            Adaptive target management (v2 exit policy: tiered target map instead of the fixed bracket)
          </label>
        </div>
      </template>
    </div>

    <!-- Step 3: attachments -->
    <div v-if="step === 3" class="pbw-body">
      <div class="pbw-attach">
        <label class="pbw-attach-head">
          <input v-model="attachments.customManagement" type="checkbox" />
          Attach custom trade management
        </label>
        <p v-if="!attachments.customManagement" class="pbw-hint">
          Off: the run uses the built-in management defaults for the chosen agent.
        </p>
        <div v-else class="pbw-grid">
          <label>Trailing mode
            <select v-model="attachments.management.mode">
              <option value="Disabled">Disabled</option>
              <option value="BreakEvenOnly">Break-even only</option>
              <option value="StructureAtr">Structure + ATR</option>
            </select>
          </label>
          <label>Break-even activation (R)<input v-model.number="attachments.management.breakEvenActivationR" type="number" min="0" step="0.1" /></label>
          <label>Structure-trail activation (R)<input v-model.number="attachments.management.structureTrailActivationR" type="number" min="0" step="0.1" /></label>
          <label>ATR buffer multiplier<input v-model.number="attachments.management.atrBufferMultiplier" type="number" min="0" step="0.05" /></label>
          <label>Min stop improvement (ATR)<input v-model.number="attachments.management.minimumStopImprovementAtr" type="number" min="0" step="0.01" /></label>
          <label>Minimum runner fraction<input v-model.number="attachments.management.minimumRunnerFraction" type="number" min="0" max="1" step="0.05" /></label>
        </div>
        <div v-if="attachments.customManagement" class="pbw-checks">
          <label><input v-model="attachments.management.exitOnAdverseStructureBreak" type="checkbox" /> Exit on adverse structure break</label>
          <label><input v-model="attachments.management.enableScaleOut" type="checkbox" /> Scale out in stages</label>
          <label><input v-model="attachments.management.supplyDemandManagementEnabled" type="checkbox" /> S&amp;D thesis management</label>
          <label><input v-model="attachments.management.liquidityManagementEnabled" type="checkbox" /> Liquidity thesis management</label>
        </div>
      </div>

      <div class="pbw-attach">
        <label class="pbw-attach-head">
          <input v-model="attachments.calibrationEnabled" type="checkbox" />
          Attach calibration policy
        </label>
        <p v-if="!attachments.calibrationEnabled" class="pbw-hint">
          Off: the profile runs uncalibrated (mode Disabled) — fastest, no extra training backtests.
        </p>
        <div v-else class="pbw-grid">
          <label>Mode
            <select v-model="attachments.calibration.mode">
              <option value="TrainFreshAndUseForHeldOutEvaluation">Train fresh and evaluate</option>
              <option value="TrainFreshPendingReviewOnly">Train, pending review only (Experiment Lab only)</option>
              <option value="ReuseSpecifiedArtifacts">Reuse specified artifacts</option>
            </select>
          </label>
          <label>Internal folds<input v-model.number="attachments.calibration.internalFolds" type="number" min="3" step="1" /></label>
          <label>Embargo (hours)<input v-model.number="attachments.calibration.internalEmbargoHours" type="number" min="0" step="1" /></label>
          <label v-if="attachments.calibration.mode === 'ReuseSpecifiedArtifacts'" class="full">
            Artifact IDs (comma separated)
            <input v-model.trim="attachments.calibration.artifactIds" placeholder="guid, guid" spellcheck="false" />
          </label>
        </div>
        <p v-if="attachments.calibrationEnabled && attachments.calibration.mode === 'TrainFreshAndUseForHeldOutEvaluation'" class="pbw-hint">
          Adds real time before each run: the training stage runs extra full backtests over the
          training window before the visible simulation starts.
        </p>
      </div>

      <div class="pbw-attach">
        <span class="pbw-attach-head">Analysis modules</span>
        <div class="pbw-checks">
          <label>
            <input
              type="checkbox"
              :checked="effectiveSupplyDemandAnalysis"
              :disabled="requiredAnalysis.supplyDemand"
              @change="attachments.supplyDemandAnalysis = ($event.target as HTMLInputElement).checked"
            />
            Supply/demand analyzer
            <small v-if="requiredAnalysis.supplyDemand">required by the current agent configuration</small>
          </label>
          <label>
            <input
              type="checkbox"
              :checked="effectiveLiquidityAnalysis"
              :disabled="requiredAnalysis.liquidity"
              @change="attachments.liquidityAnalysis = ($event.target as HTMLInputElement).checked"
            />
            Liquidity analyzer
            <small v-if="requiredAnalysis.liquidity">required by the current agent configuration</small>
          </label>
        </div>
      </div>
    </div>

    <!-- Step 4: review + save -->
    <div v-if="step === 4" class="pbw-body">
      <div class="pbw-grid">
        <label>Profile name<input v-model.trim="save.name" placeholder="e.g. EURUSD improved swing" /></label>
        <label>Instrument<input v-model.trim="save.instrument" placeholder="FX:EUR/USD" spellcheck="false" /></label>
        <label class="full">Tags (comma separated, optional)<input v-model.trim="save.tags" placeholder="swing, eurusd" /></label>
      </div>
      <dl class="pbw-review">
        <div><dt>Agent</dt><dd>{{ agentLabel }}</dd></div>
        <div><dt>Timeframes</dt><dd>
          {{ timeframes.context }} context<template v-if="timeframes.additionalContext"> (+{{ timeframes.additionalContext }})</template>
          → {{ timeframes.setup }} setup<template v-if="isProgressive"> → {{ timeframes.confirmation }} confirm</template>
          → {{ timeframes.entry }} entry
        </dd></div>
        <div v-if="isStructural"><dt>Playbooks</dt><dd>
          {{ [agent.sweepEnabled ? 'sweep' : null, agent.pullbackEnabled ? 'pullback' : null, agent.breakRetestEnabled ? 'break/retest' : null].filter(Boolean).join(', ') }}
          <template v-if="agent.adaptiveTargets"> · adaptive targets (v2)</template>
        </dd></div>
        <div><dt>Management</dt><dd>{{ attachments.customManagement ? `custom (${attachments.management.mode})` : 'agent defaults' }}</dd></div>
        <div><dt>Calibration</dt><dd>{{ attachments.calibrationEnabled ? attachments.calibration.mode : 'Disabled' }}</dd></div>
        <div><dt>Analysis</dt><dd>
          {{ [effectiveSupplyDemandAnalysis ? 'supply/demand' : null, effectiveLiquidityAnalysis ? 'liquidity' : null].filter(Boolean).join(', ') || 'none' }}
        </dd></div>
      </dl>
      <p class="pbw-hint">
        Saved profiles are immutable (revision 1). They appear in the "Saved agent profile"
        dropdown here and in Experiment Lab; later changes create new revisions there.
      </p>
    </div>

    <footer class="pbw-foot">
      <button type="button" class="secondary" :disabled="step === 1 || saving" @click="back">Back</button>
      <button v-if="step < totalSteps" type="button" @click="next">Next</button>
      <button v-else type="button" :disabled="saving" @click="saveProfile">
        {{ saving ? 'Saving…' : 'Save profile' }}
      </button>
    </footer>
  </section>
</template>

<style scoped>
.pbw {
  display: grid;
  gap: 0.75rem;
  padding: 0.85rem;
  border: 1px solid rgba(96, 165, 250, 0.35);
  border-radius: 0.6rem;
  background: rgba(96, 165, 250, 0.05);
}
.pbw-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 0.75rem; }
.pbw-head h3 { margin: 0 0 0.4rem; }
.pbw-steps { display: flex; gap: 0.35rem; list-style: none; margin: 0; padding: 0; font-size: 0.78rem; }
.pbw-steps li {
  padding: 0.15rem 0.55rem;
  border: 1px solid rgba(255, 255, 255, 0.14);
  border-radius: 999px;
  color: #8b93a7;
}
.pbw-steps li.active { border-color: #60a5fa; color: #bfdbfe; background: rgba(96, 165, 250, 0.12); }
.pbw-steps li.done { border-color: rgba(74, 222, 128, 0.5); color: #86efac; }
.pbw-close {
  background: transparent;
  border: 1px solid rgba(255, 255, 255, 0.14);
  padding: 0.25rem 0.7rem;
}
.pbw-body { display: grid; gap: 0.7rem; }
.pbw-body h4 { margin: 0.15rem 0 0; }
.pbw-hint { margin: 0; color: #8b93a7; font-size: 0.8rem; line-height: 1.45; }
.pbw-error { margin: 0; color: #fca5a5; font-size: 0.85rem; }
.pbw-error-inline { color: #fca5a5; font-weight: normal; margin-left: 0.4rem; }
.pbw-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(11.5rem, 1fr));
  gap: 0.55rem;
}
.pbw-grid.tight { grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); }
.pbw-grid label { display: grid; gap: 0.25rem; font-size: 0.8rem; }
.pbw-grid label.full { grid-column: 1 / -1; }
.pbw-grid small { color: #8b93a7; }
.pbw-checks { display: flex; flex-wrap: wrap; gap: 0.4rem 1.2rem; font-size: 0.82rem; }
.pbw-checks label { display: flex; align-items: center; gap: 0.4rem; }
.pbw-checks small { color: #8b93a7; }
.pbw-agent-cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr)); gap: 0.55rem; }
.pbw-agent-card {
  display: grid;
  gap: 0.3rem;
  text-align: left;
  padding: 0.65rem 0.75rem;
  border: 1px solid rgba(255, 255, 255, 0.14);
  border-radius: 0.55rem;
  background: rgba(255, 255, 255, 0.02);
  cursor: pointer;
}
.pbw-agent-card span { color: #8b93a7; font-size: 0.78rem; line-height: 1.4; }
.pbw-agent-card.selected { border-color: #60a5fa; background: rgba(96, 165, 250, 0.1); }
.pbw-playbooks { display: grid; gap: 0.5rem; }
.pbw-playbook {
  padding: 0.55rem 0.65rem;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 0.5rem;
  display: grid;
  gap: 0.5rem;
}
.pbw-playbook.off { opacity: 0.65; }
.pbw-playbook-head { display: flex; align-items: center; gap: 0.45rem; font-weight: 600; font-size: 0.85rem; }
.pbw-attach {
  display: grid;
  gap: 0.5rem;
  padding: 0.6rem 0.7rem;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 0.5rem;
}
.pbw-attach-head { display: flex; align-items: center; gap: 0.45rem; font-weight: 600; font-size: 0.87rem; }
.pbw-review { display: grid; gap: 0.35rem; margin: 0; }
.pbw-review > div { display: grid; grid-template-columns: 8rem 1fr; gap: 0.5rem; font-size: 0.85rem; }
.pbw-review dt { margin: 0; color: #8b93a7; }
.pbw-review dd { margin: 0; }
.pbw-foot { display: flex; justify-content: flex-end; gap: 0.5rem; }
</style>
