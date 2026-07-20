<script setup lang="ts">
import { reactive, ref } from 'vue'
import type {
  ExperimentCalibrationMode,
  SimulationProfileReference,
  SimulationStrategyProfile,
} from '../../../types/simulation-experiments'
import type { SimulationProfileDiff } from '../../../types'

const props = defineProps<{
  profiles: SimulationStrategyProfile[]
  selected: SimulationProfileReference[]
  loading: boolean
}>()
const emit = defineEmits<{
  (event: 'update:selected', value: SimulationProfileReference[]): void
  (event: 'create', value: { name: string; instrument: string; calibrationMode: ExperimentCalibrationMode }): void
  (event: 'clone', value: { profile: SimulationStrategyProfile; name: string }): void
  (event: 'archive', profile: SimulationStrategyProfile): void
  (event: 'refresh'): void
}>()

const showCreate = ref(false)
const createDraft = reactive({
  name: 'Structural baseline',
  instrument: 'FX:EUR/USD',
  calibrationMode: 'TrainFreshAndUseForHeldOutEvaluation' as ExperimentCalibrationMode,
})

function reference(profile: SimulationStrategyProfile): SimulationProfileReference | undefined {
  return props.selected.find(item => item.profileId === profile.profileId && item.revision === profile.revision)
}

function toggle(profile: SimulationStrategyProfile): void {
  const existing = reference(profile)
  emit('update:selected', existing
    ? props.selected.filter(item => item !== existing)
    : [...props.selected, { profileId: profile.profileId, revision: profile.revision, isBaseline: false }])
}

function baseline(profile: SimulationStrategyProfile): void {
  const selected = reference(profile)
    ? props.selected
    : [...props.selected, { profileId: profile.profileId, revision: profile.revision, isBaseline: false }]
  emit('update:selected', selected.map(item => ({
    ...item,
    isBaseline: item.profileId === profile.profileId && item.revision === profile.revision,
  })))
}

function cloneProfile(profile: SimulationStrategyProfile): void {
  const name = window.prompt('Name for the immutable clone', `${profile.name} copy`)
  if (name?.trim()) emit('clone', { profile, name })
}

const historyOpenId = ref<string | null>(null)
const compareRevision = ref(1)
const diffResult = ref<SimulationProfileDiff | null>(null)
const diffError = ref<string | null>(null)
const diffLoading = ref(false)

function toggleHistory(profile: SimulationStrategyProfile): void {
  const isOpen = historyOpenId.value === profile.profileId
  historyOpenId.value = isOpen ? null : profile.profileId
  diffResult.value = null
  diffError.value = null
  compareRevision.value = Math.max(1, profile.revision - 1)
}

async function loadDiff(profile: SimulationStrategyProfile): Promise<void> {
  diffLoading.value = true
  diffError.value = null
  diffResult.value = null
  try {
    const query = new URLSearchParams({
      rightId: profile.profileId,
      rightRevision: String(profile.revision),
    })
    const response = await fetch(
      `${import.meta.env.BASE_URL}api/simulation-profiles/${profile.profileId}/revisions/${compareRevision.value}/diff?${query}`,
    )
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { error?: string } | null
      throw new Error(body?.error ?? `Request failed with HTTP ${response.status}.`)
    }
    diffResult.value = await response.json() as SimulationProfileDiff
  } catch (err) {
    diffError.value = err instanceof Error ? err.message : String(err)
  } finally {
    diffLoading.value = false
  }
}
</script>

<template>
  <section class="ex-card">
    <div class="ex-card-heading">
      <div><span class="ex-step">2</span><h3>Profiles</h3></div>
      <div class="ex-actions">
        <button type="button" class="secondary" :disabled="loading" @click="emit('refresh')">Refresh</button>
        <button type="button" @click="showCreate = !showCreate">New structural profile</button>
      </div>
    </div>

    <form v-if="showCreate" class="ex-inline-editor" @submit.prevent="emit('create', { ...createDraft })">
      <label>Name<input v-model="createDraft.name" required /></label>
      <label>Instrument<input v-model="createDraft.instrument" required placeholder="FX:EUR/USD" /></label>
      <label>Calibration
        <select v-model="createDraft.calibrationMode">
          <option value="TrainFreshAndUseForHeldOutEvaluation">Train fresh and evaluate</option>
          <option value="TrainFreshPendingReviewOnly">Train, pending review only</option>
          <option value="Disabled">Disabled</option>
        </select>
      </label>
      <button type="submit">Create immutable rev 1</button>
    </form>

    <div v-if="profiles.length" class="ex-table-wrap">
      <table class="ex-table">
        <thead><tr><th>Use</th><th>Profile revision</th><th>Strategy / instrument</th><th>Calibration</th><th>Baseline</th><th></th></tr></thead>
        <tbody>
          <template v-for="profile in profiles" :key="`${profile.profileId}:${profile.revision}`">
            <tr :class="{ selected: reference(profile) }">
              <td><input type="checkbox" :checked="!!reference(profile)" :aria-label="`Use ${profile.name}`" @change="toggle(profile)" /></td>
              <td><strong>{{ profile.name }}</strong><small class="mono">rev {{ profile.revision }} · {{ profile.contentHash.slice(0, 10) }}</small></td>
              <td>{{ profile.agent.kind }}<small>{{ profile.instrument.value }}</small></td>
              <td>{{ profile.calibration.mode.replaceAll(/([A-Z])/g, ' $1').trim() }}</td>
              <td><input type="radio" name="experiment-baseline" :checked="reference(profile)?.isBaseline" @change="baseline(profile)" /></td>
              <td class="ex-row-actions">
                <button
                  v-if="reference(profile)"
                  type="button"
                  class="secondary danger"
                  @click="toggle(profile)"
                >Remove from experiment</button>
                <button type="button" class="secondary" @click="cloneProfile(profile)">Clone</button>
                <button type="button" class="secondary" @click="toggleHistory(profile)">
                  {{ historyOpenId === profile.profileId ? 'Hide history' : 'History' }}
                </button>
                <button type="button" class="secondary danger" @click="emit('archive', profile)">Archive stored profile</button>
              </td>
            </tr>
            <tr v-if="historyOpenId === profile.profileId" class="ex-history-row">
              <td colspan="6">
                <div class="ex-history-panel">
                  <p class="ex-empty">
                    Compare revision <strong>{{ profile.revision }}</strong> (current) against an earlier one.
                  </p>
                  <div class="ex-history-controls">
                    <label>
                      Compare against revision
                      <input v-model.number="compareRevision" type="number" min="1" :max="profile.revision" step="1" />
                    </label>
                    <button type="button" :disabled="diffLoading" @click="loadDiff(profile)">
                      {{ diffLoading ? 'Diffing…' : 'Show diff' }}
                    </button>
                  </div>
                  <p v-if="diffError" class="ex-history-error">{{ diffError }}</p>
                  <div v-if="diffResult">
                    <p v-if="diffResult.changedPaths.length === 0" class="ex-empty">No field-level differences detected.</p>
                    <ul v-else class="ex-history-paths">
                      <li v-for="path in diffResult.changedPaths" :key="path"><code>{{ path }}</code></li>
                    </ul>
                  </div>
                </div>
              </td>
            </tr>
          </template>
        </tbody>
      </table>
    </div>
    <p v-else class="ex-empty">No profiles yet. Create a validated structural baseline to begin.</p>
  </section>
</template>
