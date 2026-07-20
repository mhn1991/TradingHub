<script setup lang="ts">
import type { SimulationExperimentParallelism } from '../../../types/simulation-experiments'

const model = defineModel<SimulationExperimentParallelism>({ required: true })
</script>

<template>
  <section class="ex-card">
    <div class="ex-card-heading"><div><span class="ex-step">4</span><h3>Parallelism and resources</h3></div></div>
    <div class="ex-form-grid">
      <label>Concurrent profile groups<input v-model.number="model.maxProfileGroups" type="number" min="1" max="16" /></label>
      <label>Total strategy workers<input v-model.number="model.maxTotalStrategyWorkers" type="number" min="1" max="64" /></label>
      <label>Downloads per broker<input v-model.number="model.maxHistoricalDownloadsPerBroker" type="number" min="1" max="8" /></label>
      <label>Memory budget · MiB (optional)
        <input
          :value="model.estimatedMemoryBudgetBytes ? Math.round(model.estimatedMemoryBudgetBytes / 1048576) : ''"
          type="number"
          min="1"
          placeholder="Server default"
          @input="model.estimatedMemoryBudgetBytes = Number(($event.target as HTMLInputElement).value) * 1048576 || undefined"
        />
      </label>
    </div>
    <p class="ex-muted">The server governor enforces the lower of these plan limits and its process-wide limits.</p>
  </section>
</template>
