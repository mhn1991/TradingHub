<script setup lang="ts">
import { computed } from 'vue'
import type { ExperimentDraft, SimulationStrategyProfile } from '../../../types/simulation-experiments'
import { resolveExperimentTimeline } from '../../../composables/useSimulationExperiment'

const props = defineProps<{ draft: ExperimentDraft; profiles: SimulationStrategyProfile[]; busy: boolean }>()
const emit = defineEmits<{ (event: 'launch'): void }>()

const selectedProfiles = computed(() => props.draft.profileReferences.map(reference => ({
  reference,
  profile: props.profiles.find(item => item.profileId === reference.profileId && item.revision === reference.revision),
})))
const timeline = computed(() => {
  try { return resolveExperimentTimeline(props.draft.timeline) } catch { return null }
})
const canLaunch = computed(() => props.draft.name.trim() && selectedProfiles.value.length && timeline.value)
const date = (value?: string): string => value?.slice(0, 10) ?? '—'
</script>

<template>
  <section class="ex-card review-card">
    <div class="ex-card-heading"><div><span class="ex-step">5</span><h3>Review resolved plan</h3></div><span class="ex-badge">server orchestrated</span></div>
    <div class="ex-review-grid">
      <dl>
        <dt>Experiment</dt><dd>{{ draft.name || 'Untitled' }}</dd>
        <dt>Learning [UTC)</dt><dd>{{ date(timeline?.learningFrom) }} → {{ date(timeline?.learningTo) }}</dd>
        <dt>Held-out [UTC)</dt><dd>{{ date(timeline?.evaluationFrom) }} → {{ date(timeline?.evaluationTo) }}</dd>
        <dt>Profile groups</dt><dd>{{ draft.parallelism.maxProfileGroups }}</dd>
      </dl>
      <ul class="ex-profile-summary">
        <li v-for="item in selectedProfiles" :key="item.reference.profileId">
          <strong>{{ item.profile?.name ?? item.reference.profileId }}</strong>
          <span>r{{ item.reference.revision }} · {{ item.profile?.calibration.mode }}</span>
          <span v-if="item.reference.isBaseline" class="ex-badge">baseline</span>
        </li>
      </ul>
    </div>
    <label class="ex-check"><input v-model="draft.allowInsufficientWarmup" type="checkbox" /> Allow launch when available history is shorter than the conservative warm-up plan</label>
    <label>Known earliest available data (optional)<input v-model="draft.availableDataFrom" type="date" /></label>
    <button type="button" class="ex-launch" :disabled="!canLaunch || busy" @click="emit('launch')">
      {{ busy ? 'Enqueueing…' : 'Launch experiment' }}
    </button>
  </section>
</template>
