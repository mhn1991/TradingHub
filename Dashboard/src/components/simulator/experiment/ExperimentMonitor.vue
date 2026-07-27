<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import type { SimulationJobSnapshot, StrategyProgressSnapshot } from '../../../types'
import type { SimulationExperimentSnapshot, SimulationProfileRunProgress } from '../../../types/simulation-experiments'

const props = defineProps<{ snapshot: SimulationExperimentSnapshot; recent: SimulationExperimentSnapshot[] }>()
const emit = defineEmits<{
  (event: 'action', action: 'pause' | 'resume' | 'cancel'): void
  (event: 'select', snapshot: SimulationExperimentSnapshot): void
  (event: 'refresh'): void
  (event: 'open-report', experimentId: string): void
}>()

const copied = ref(false)
const evaluationJobs = ref<Record<string, SimulationJobSnapshot>>({})
let jobPollTimer: number | undefined

const averageProgress = computed(() => {
  if (!props.snapshot.profiles.length) return 0
  return props.snapshot.profiles.reduce((sum, profile) => sum + profile.progressPercent, 0) /
    props.snapshot.profiles.length
})

const stageLabels: Record<string, string> = {
  Queued: 'Queued',
  ResolvingProfiles: 'Resolving profiles',
  PreparingDatasets: 'Preparing datasets',
  TrainingWarmup: 'Warming up (training)',
  Learning: 'Running (learning)',
  FreezingArtifacts: 'Freezing artifacts',
  EmbargoReady: 'Embargo ready',
  EvaluationWarmup: 'Warming up (evaluation)',
  Evaluating: 'Running (evaluation)',
  Aggregating: 'Aggregating results',
  Completed: 'Completed',
  Failed: 'Failed',
  Cancelled: 'Cancelled',
}
function stageLabel(stage: string): string {
  return stageLabels[stage] ?? stage
}

/** wget-style ascii bar: `[###########-----------]`. */
function asciiBar(percent: number, width = 22): string {
  const clamped = Math.max(0, Math.min(100, percent))
  const filled = Math.round((clamped / 100) * width)
  return '█'.repeat(filled) + '░'.repeat(Math.max(0, width - filled))
}
const canPause = computed(() => ['Queued', 'Running'].includes(props.snapshot.state))
const canResume = computed(() => ['Paused', 'Interrupted'].includes(props.snapshot.state))
const canCancel = computed(() => !['Completed', 'Failed', 'Cancelled'].includes(props.snapshot.state))
const time = (value?: string | null): string => value ? new Date(value).toLocaleString() : '—'
const evaluationJobKey = computed(() => props.snapshot.profiles
  .map(profile => profile.evaluationJobId)
  .filter((id): id is string => Boolean(id))
  .sort()
  .join('|'))

async function copyExperimentId(): Promise<void> {
  await navigator.clipboard.writeText(props.snapshot.id)
  copied.value = true
  window.setTimeout(() => { copied.value = false }, 1500)
}

function evaluationJob(profile: SimulationProfileRunProgress): SimulationJobSnapshot | null {
  return profile.evaluationJobId ? evaluationJobs.value[profile.evaluationJobId] ?? null : null
}

function evaluationStrategy(profile: SimulationProfileRunProgress): StrategyProgressSnapshot | null {
  return evaluationJob(profile)?.strategies[0] ?? null
}

function number(value: number | null | undefined, digits = 2): string {
  return value == null ? '—' : value.toLocaleString(undefined, { maximumFractionDigits: digits })
}

async function refreshEvaluationJobs(): Promise<void> {
  const ids = evaluationJobKey.value ? evaluationJobKey.value.split('|') : []
  await Promise.all(ids.map(async id => {
    try {
      const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}`, { cache: 'no-store' })
      if (!response.ok) return
      evaluationJobs.value = { ...evaluationJobs.value, [id]: await response.json() as SimulationJobSnapshot }
    } catch {
      // The parent experiment poll remains authoritative; retry the child snapshot next tick.
    }
  }))
}

function restartEvaluationPolling(): void {
  if (jobPollTimer !== undefined) window.clearInterval(jobPollTimer)
  jobPollTimer = undefined
  void refreshEvaluationJobs()
  if (evaluationJobKey.value && !['Completed', 'Failed', 'Cancelled'].includes(props.snapshot.state)) {
    jobPollTimer = window.setInterval(() => void refreshEvaluationJobs(), 1500)
  }
}

watch([evaluationJobKey, () => props.snapshot.state], restartEvaluationPolling, { immediate: true })
onUnmounted(() => {
  if (jobPollTimer !== undefined) window.clearInterval(jobPollTimer)
})
</script>

<template>
  <section class="ex-card monitor-card">
    <div class="ex-card-heading">
      <div><span class="ex-step">6</span><h3>Monitor</h3></div>
      <div class="ex-actions">
        <button type="button" class="secondary" :disabled="!canPause" @click="emit('action', 'pause')">Pause</button>
        <button type="button" class="secondary" :disabled="!canResume" @click="emit('action', 'resume')">Resume</button>
        <button type="button" class="secondary danger" :disabled="!canCancel" @click="emit('action', 'cancel')">Cancel</button>
      </div>
    </div>
    <div class="ex-parent-status">
      <div><strong>{{ snapshot.name }}</strong><span>{{ snapshot.state }} · {{ stageLabel(snapshot.stage) }} · rev {{ snapshot.revision }}</span></div>
      <strong>{{ averageProgress.toFixed(0) }}%</strong>
    </div>
    <div class="ex-id-row">
      <span>Experiment UUID</span>
      <code>{{ snapshot.id }}</code>
      <button type="button" class="secondary" @click="copyExperimentId">{{ copied ? 'Copied' : 'Copy' }}</button>
      <button type="button" @click="emit('open-report', snapshot.id)">Open report</button>
    </div>
    <div class="ex-ascii-bar mono">[{{ asciiBar(averageProgress) }}] {{ averageProgress.toFixed(0) }}%</div>
    <p class="ex-muted">Updated {{ time(snapshot.updatedAt) }} · Dataset {{ snapshot.manifest.datasetStatus }}</p>
    <p v-if="snapshot.failureReason" class="ex-alert danger">{{ snapshot.failureReason }}</p>
    <p v-for="warning in snapshot.warnings" :key="warning" class="ex-alert warning">{{ warning }}</p>

    <div class="ex-child-grid">
      <article v-for="profile in snapshot.profiles" :key="profile.profileRunId">
        <header><strong>{{ profile.profileName }}</strong><span>{{ profile.progressPercent.toFixed(0) }}%</span></header>
        <span class="ex-badge">{{ stageLabel(profile.stage) }}</span>
        <div class="ex-ascii-bar small mono">[{{ asciiBar(profile.progressPercent) }}]</div>
        <p v-if="profile.statusDetail">{{ profile.statusDetail }}</p>
        <small v-if="profile.learningJobId">Learning UUID: <code>{{ profile.learningJobId }}</code></small>
        <small v-if="profile.evaluationJobId">Evaluation UUID: <code>{{ profile.evaluationJobId }}</code></small>
        <template v-if="evaluationJob(profile)">
          <div class="ex-child-runtime">
            <span>Evaluation child · {{ evaluationJob(profile)?.status }}</span>
            <span>{{ number(evaluationJob(profile)?.processedBaseCandles, 0) }} candles</span>
            <span>{{ number(evaluationJob(profile)?.candlesPerSecond, 0) }} c/s</span>
          </div>
          <dl v-if="evaluationStrategy(profile)" class="ex-agent-stats">
            <div><dt>Balance</dt><dd>{{ number(evaluationStrategy(profile)?.balance) }}</dd></div>
            <div><dt>Equity</dt><dd>{{ number(evaluationStrategy(profile)?.equity) }}</dd></div>
            <div><dt>Net P/L</dt><dd>{{ number(evaluationStrategy(profile)?.netProfit) }}</dd></div>
            <div><dt>Trades</dt><dd>{{ number(evaluationStrategy(profile)?.completedTrades, 0) }}</dd></div>
            <div><dt>Open</dt><dd>{{ number(evaluationStrategy(profile)?.openPositions, 0) }}</dd></div>
            <div><dt>Win rate</dt><dd>{{ number(evaluationStrategy(profile)?.performance?.winRatePercent) }}%</dd></div>
            <div><dt>Profit factor</dt><dd>{{ number(evaluationStrategy(profile)?.performance?.profitFactor) }}</dd></div>
            <div><dt>Avg R</dt><dd>{{ number(evaluationStrategy(profile)?.performance?.averageR) }}</dd></div>
            <div><dt>Max drawdown</dt><dd>{{ number(evaluationStrategy(profile)?.performance?.maximumDrawdown) }}</dd></div>
          </dl>
        </template>
        <small v-else-if="profile.stage === 'Learning'">Agent trading metrics begin with held-out evaluation; learning progress is shown above.</small>
        <small v-else-if="profile.stage === 'Evaluating'">Waiting for the evaluation child run…</small>
        <small v-if="profile.artifacts.length">Frozen: {{ profile.artifacts.map(item => item.kind).join(', ') }}</small>
        <small v-if="profile.failureReason" class="danger-text">{{ profile.failureReason }}</small>
      </article>
    </div>

    <details v-if="recent.length" class="ex-recent">
      <summary>Recent experiments</summary>
      <button v-for="item in recent" :key="item.id" type="button" class="secondary" @click="emit('select', item)">
        {{ item.name }} · {{ item.state }} · {{ time(item.updatedAt) }}
      </button>
      <button type="button" class="secondary" @click="emit('refresh')">Refresh list</button>
    </details>
  </section>
</template>
