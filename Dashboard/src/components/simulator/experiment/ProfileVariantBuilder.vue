<script setup lang="ts">
import { computed, reactive } from 'vue'
import type { SimulationStrategyProfile } from '../../../types/simulation-experiments'

const props = defineProps<{ profiles: SimulationStrategyProfile[] }>()
const emit = defineEmits<{
  (event: 'create', value: {
    source: SimulationStrategyProfile
    patch: {
      name: string
      playbook: 'sweep' | 'pullback' | 'break-retest'
      cciMode: 'Disabled' | 'Soft' | 'Required'
      supplyDemandConfluence: 'Disabled' | 'Preferred' | 'Required'
    }
  }): void
}>()

const structuralProfiles = computed(() => props.profiles.filter(item => item.agent.kind === 'StructuralConfluence'))
const draft = reactive({
  sourceId: '',
  name: 'sweep-cci-soft',
  playbook: 'sweep' as 'sweep' | 'pullback' | 'break-retest',
  cciMode: 'Soft' as 'Disabled' | 'Soft' | 'Required',
  supplyDemandConfluence: 'Preferred' as 'Disabled' | 'Preferred' | 'Required',
})

function create(): void {
  const source = structuralProfiles.value.find(item => item.profileId === draft.sourceId) ?? structuralProfiles.value[0]
  if (source) emit('create', { source, patch: { ...draft } })
}
</script>

<template>
  <section class="ex-card ex-subcard">
    <div class="ex-card-heading"><div><h3>Variant builder</h3></div><span class="ex-badge">resolved snapshots</span></div>
    <p class="ex-muted">Create a small playbook/CCI matrix as independent immutable profiles. Required liquidity and supply/demand analyzers are enabled automatically.</p>
    <form class="ex-form-grid variant-grid" @submit.prevent="create">
      <label>Base profile
        <select v-model="draft.sourceId" required>
          <option value="" disabled>Select structural profile</option>
          <option v-for="profile in structuralProfiles" :key="profile.profileId" :value="profile.profileId">{{ profile.name }} · r{{ profile.revision }}</option>
        </select>
      </label>
      <label>Variant name<input v-model="draft.name" required /></label>
      <label>Only playbook
        <select v-model="draft.playbook">
          <option value="sweep">Liquidity sweep reversal</option>
          <option value="pullback">Supply/demand pullback</option>
          <option value="break-retest">Accepted-break retest</option>
        </select>
      </label>
      <label>CCI confirmation
        <select v-model="draft.cciMode"><option>Disabled</option><option>Soft</option><option>Required</option></select>
      </label>
      <label v-if="draft.playbook === 'sweep'">Supply/demand confluence
        <select v-model="draft.supplyDemandConfluence"><option>Disabled</option><option>Preferred</option><option>Required</option></select>
      </label>
      <button type="submit" :disabled="!structuralProfiles.length">Create variant</button>
    </form>
  </section>
</template>
