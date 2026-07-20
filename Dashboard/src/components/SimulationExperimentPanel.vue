<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useSimulationExperiment } from '../composables/useSimulationExperiment'
import { useSimulationProfiles } from '../composables/useSimulationProfiles'
import type {
  ExperimentDraft,
  SimulationProfileReference,
  SimulationStrategyProfile,
} from '../types/simulation-experiments'
import CalibrationPlanEditor from './simulator/experiment/CalibrationPlanEditor.vue'
import ExperimentComparison from './simulator/experiment/ExperimentComparison.vue'
import ExperimentMonitor from './simulator/experiment/ExperimentMonitor.vue'
import ExperimentResourceControls from './simulator/experiment/ExperimentResourceControls.vue'
import ExperimentReview from './simulator/experiment/ExperimentReview.vue'
import ExperimentTimelineEditor from './simulator/experiment/ExperimentTimelineEditor.vue'
import ProfileVariantBuilder from './simulator/experiment/ProfileVariantBuilder.vue'
import StrategyProfileTable from './simulator/experiment/StrategyProfileTable.vue'

const emit = defineEmits<{
  (event: 'open-report', experimentId: string): void
}>()

const profileService = useSimulationProfiles()
const experiment = useSimulationExperiment()
const localError = ref<string | null>(null)

function monthStart(offset: number): string {
  const now = new Date()
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + offset, 1)).toISOString().slice(0, 10)
}

const draft = reactive<ExperimentDraft>({
  name: `Structural confluence · ${monthStart(0)}`,
  description: 'Leakage-safe structural playbook and confirmation comparison.',
  timeline: {
    mode: 'relative',
    learningFrom: monthStart(-3),
    learningTo: monthStart(-1),
    evaluationFrom: monthStart(-1),
    evaluationTo: monthStart(0),
    learningMonths: 2,
    learningDays: 0,
    trainingWarmupDays: 21,
    evaluationWarmupDays: 21,
    embargoDays: 10,
  },
  profileReferences: [],
  parallelism: {
    // Matches SimulationExperimentRequest's own backend default (reduced from 2 to 1 in a prior
    // session as a memory-safety fix) - the Dashboard always sends its own explicit value, so
    // leaving this at 2 would have silently made that fix a no-op for every Dashboard-initiated
    // experiment. Users who have the memory headroom can still raise it via
    // ExperimentResourceControls.vue.
    maxProfileGroups: 1,
    maxTotalStrategyWorkers: 4,
    maxHistoricalDownloadsPerBroker: 1,
  },
  allowInsufficientWarmup: false,
  availableDataFrom: '',
})

function replaceSelected(value: SimulationProfileReference[]): void {
  draft.profileReferences = value
}

async function safely(action: () => Promise<unknown>): Promise<void> {
  localError.value = null
  try { await action() } catch (problem) {
    localError.value = problem instanceof Error ? problem.message : String(problem)
  }
}

async function reviseCalibration(value: Parameters<typeof profileService.reviseCalibration>[1] extends never ? never : {
  profile: Parameters<typeof profileService.reviseCalibration>[0]
  policy: Parameters<typeof profileService.reviseCalibration>[1]
}): Promise<void> {
  const revised = await profileService.reviseCalibration(value.profile, value.policy)
  draft.profileReferences = draft.profileReferences.map(reference => reference.profileId === revised.profileId
    ? { ...reference, revision: revised.revision }
    : reference)
}

async function archiveProfile(profile: SimulationStrategyProfile): Promise<void> {
  await profileService.archive(profile.profileId)
  draft.profileReferences = draft.profileReferences.filter(
    reference => reference.profileId !== profile.profileId,
  )
}

onMounted(() => {
  void profileService.refresh()
  void safely(() => experiment.refreshRecent())
})
</script>

<template>
  <div class="experiment-panel">
    <header class="experiment-hero">
      <div><span class="ex-eyebrow">Leakage-safe research orchestration</span><h2>Simulation Experiment</h2><p>Train, freeze, and compare immutable strategy profiles over one explicitly held-out timeline.</p></div>
      <div class="ex-hero-facts"><span>UTC half-open ranges</span><span>Frozen artifacts</span><span>Resumable parent job</span></div>
    </header>

    <form class="ex-card ex-identity" @submit.prevent>
      <label>Experiment name<input v-model="draft.name" required /></label>
      <label>Description<input v-model="draft.description" /></label>
    </form>

    <ExperimentTimelineEditor v-model="draft.timeline" />
    <StrategyProfileTable
      :profiles="profileService.profiles.value"
      :selected="draft.profileReferences"
      :loading="profileService.loading.value"
      @update:selected="replaceSelected"
      @refresh="profileService.refresh"
      @create="value => safely(() => profileService.createStructural(value))"
      @clone="value => safely(() => profileService.clone(value.profile, value.name))"
      @archive="profile => safely(() => archiveProfile(profile))"
    />
    <p v-if="profileService.error.value" class="ex-alert danger">{{ profileService.error.value }}</p>
    <ProfileVariantBuilder
      :profiles="profileService.profiles.value"
      @create="value => safely(() => profileService.createVariant(value.source, value.patch))"
    />
    <CalibrationPlanEditor
      :profiles="profileService.profiles.value"
      :selected="draft.profileReferences"
      @revise="value => safely(() => reviseCalibration(value))"
    />
    <ExperimentResourceControls v-model="draft.parallelism" />
    <ExperimentReview
      :draft="draft"
      :profiles="profileService.profiles.value"
      :busy="experiment.loading.value"
      @launch="safely(() => experiment.start(draft))"
    />

    <p v-if="localError || experiment.error.value" class="ex-alert danger">{{ localError ?? experiment.error.value }}</p>
    <ExperimentMonitor
      v-if="experiment.snapshot.value"
      :snapshot="experiment.snapshot.value"
      :recent="experiment.recent.value"
      @action="action => safely(() => experiment.apply(action))"
      @select="experiment.select"
      @refresh="safely(() => experiment.refreshRecent())"
      @open-report="experimentId => emit('open-report', experimentId)"
    />
    <section v-else-if="experiment.recent.value.length" class="ex-card">
      <div class="ex-card-heading"><div><h3>Recent experiments</h3></div></div>
      <div class="ex-recent-list">
        <button v-for="item in experiment.recent.value" :key="item.id" type="button" class="secondary" @click="experiment.select(item)">
          <strong>{{ item.name }}</strong><span>{{ item.state }} · {{ item.stage }}</span>
        </button>
      </div>
    </section>
    <ExperimentComparison v-if="experiment.snapshot.value?.comparison" :comparison="experiment.snapshot.value.comparison" />
  </div>
</template>

<style src="../styles/simulation-experiment.css"></style>
