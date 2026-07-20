<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import type {
  CalibrationExperimentPolicy,
  ExperimentCalibrationMode,
  SimulationProfileReference,
  SimulationStrategyProfile,
} from '../../../types/simulation-experiments'

const props = defineProps<{
  profiles: SimulationStrategyProfile[]
  selected: SimulationProfileReference[]
}>()
const emit = defineEmits<{
  (event: 'revise', value: { profile: SimulationStrategyProfile; policy: CalibrationExperimentPolicy }): void
}>()

const selectedProfiles = computed(() => props.selected
  .map(reference => props.profiles.find(profile => profile.profileId === reference.profileId && profile.revision === reference.revision))
  .filter((profile): profile is SimulationStrategyProfile => !!profile))

const draft = reactive({
  profileId: '',
  mode: 'TrainFreshAndUseForHeldOutEvaluation' as ExperimentCalibrationMode,
  internalFolds: 5,
  internalEmbargoHours: 24,
  artifactIds: '',
})

watch(selectedProfiles, profiles => {
  if (!profiles.some(profile => profile.profileId === draft.profileId)) draft.profileId = profiles[0]?.profileId ?? ''
}, { immediate: true })

watch(() => draft.profileId, profileId => {
  const profile = selectedProfiles.value.find(item => item.profileId === profileId)
  if (!profile) return
  draft.mode = profile.calibration.mode
  draft.internalFolds = profile.calibration.internalFolds
  draft.internalEmbargoHours = profile.calibration.internalEmbargoHours
  draft.artifactIds = profile.calibration.artifactIds.join(', ')
}, { immediate: true })

function saveRevision(): void {
  const profile = selectedProfiles.value.find(item => item.profileId === draft.profileId)
  if (!profile) return
  emit('revise', {
    profile,
    policy: {
      mode: draft.mode,
      internalFolds: draft.internalFolds,
      internalEmbargoHours: draft.internalEmbargoHours,
      artifactIds: draft.artifactIds.split(',').map(item => item.trim()).filter(Boolean),
    },
  })
}
</script>

<template>
  <section class="ex-card">
    <div class="ex-card-heading"><div><span class="ex-step">3</span><h3>Learning and calibration</h3></div></div>
    <p class="ex-muted">Policy belongs to the immutable profile revision. Saving creates a new revision; select that revision for the experiment.</p>
    <form v-if="selectedProfiles.length" class="ex-form-grid" @submit.prevent="saveRevision">
      <label>Selected profile
        <select v-model="draft.profileId">
          <option v-for="profile in selectedProfiles" :key="profile.profileId" :value="profile.profileId">{{ profile.name }} · r{{ profile.revision }}</option>
        </select>
      </label>
      <label>Artifact policy
        <select v-model="draft.mode">
          <option value="TrainFreshAndUseForHeldOutEvaluation">Train fresh and use held-out</option>
          <option value="TrainFreshPendingReviewOnly">Train fresh, pending review only</option>
          <option value="ReuseSpecifiedArtifacts">Reuse frozen artifacts</option>
          <option value="Disabled">Disabled</option>
        </select>
      </label>
      <label>Purged CV folds<input v-model.number="draft.internalFolds" type="number" min="3" /></label>
      <label>Internal embargo hours<input v-model.number="draft.internalEmbargoHours" type="number" min="0" /></label>
      <label v-if="draft.mode === 'ReuseSpecifiedArtifacts'" class="ex-wide">Artifact IDs · setup, meta, management
        <input v-model="draft.artifactIds" placeholder="UUID, UUID, UUID" />
      </label>
      <button type="submit">Save as new revision</button>
    </form>
    <p v-else class="ex-empty">Select at least one profile above to configure its calibration policy.</p>
  </section>
</template>
