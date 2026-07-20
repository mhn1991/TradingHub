<script setup lang="ts">
import { computed } from 'vue'
import type { ExperimentTimelineDraft } from '../../../types/simulation-experiments'
import { resolveExperimentTimeline } from '../../../composables/useSimulationExperiment'

const model = defineModel<ExperimentTimelineDraft>({ required: true })

const resolved = computed(() => {
  try {
    return resolveExperimentTimeline(model.value)
  } catch {
    return null
  }
})

const day = 86_400_000
const durationDays = (from?: string, to?: string): number =>
  from && to ? Math.max(0, (Date.parse(to) - Date.parse(from)) / day) : 0
const dateLabel = (value?: string): string => value ? value.slice(0, 10) : '—'

const actualEmbargoDays = computed(() => durationDays(
  resolved.value?.learningTo,
  resolved.value?.evaluationFrom,
))
const leakageWarning = computed(() => {
  if (!resolved.value) return 'Complete every date before launching.'
  if (Date.parse(resolved.value.learningFrom) >= Date.parse(resolved.value.learningTo))
    return 'Learning must start before its exclusive end.'
  if (Date.parse(resolved.value.learningTo) > Date.parse(resolved.value.evaluationFrom))
    return 'Learning overlaps held-out evaluation. This would leak future information.'
  if (actualEmbargoDays.value < model.value.embargoDays)
    return `The exact gap is ${actualEmbargoDays.value} days, shorter than the ${model.value.embargoDays}-day policy.`
  if (Date.parse(resolved.value.evaluationFrom) >= Date.parse(resolved.value.evaluationTo))
    return 'Evaluation must start before its exclusive end.'
  return null
})

const trainingStreamFrom = computed(() => {
  if (!resolved.value) return undefined
  return new Date(Date.parse(resolved.value.learningFrom) - model.value.trainingWarmupDays * day).toISOString()
})
const evaluationStreamFrom = computed(() => {
  if (!resolved.value) return undefined
  return new Date(Date.parse(resolved.value.evaluationFrom) - model.value.evaluationWarmupDays * day).toISOString()
})
</script>

<template>
  <section class="ex-card">
    <div class="ex-card-heading">
      <div>
        <span class="ex-step">1</span>
        <h3>Dataset and timeline</h3>
      </div>
      <div class="ex-segmented" aria-label="Timeline input mode">
        <button type="button" :class="{ active: model.mode === 'relative' }" @click="model.mode = 'relative'">Relative</button>
        <button type="button" :class="{ active: model.mode === 'explicit' }" @click="model.mode = 'explicit'">Explicit UTC</button>
      </div>
    </div>

    <div class="ex-form-grid">
      <template v-if="model.mode === 'relative'">
        <label>Learning months<input v-model.number="model.learningMonths" type="number" min="0" /></label>
        <label>Additional days<input v-model.number="model.learningDays" type="number" min="0" /></label>
      </template>
      <template v-else>
        <label>Learning from<input v-model="model.learningFrom" type="date" /></label>
        <label>Learning to · exclusive<input v-model="model.learningTo" type="date" /></label>
      </template>
      <label>Evaluation from<input v-model="model.evaluationFrom" type="date" /></label>
      <label>Evaluation to · exclusive<input v-model="model.evaluationTo" type="date" /></label>
      <label>Training warm-up days<input v-model.number="model.trainingWarmupDays" type="number" min="0" /></label>
      <label>Embargo policy days<input v-model.number="model.embargoDays" type="number" min="0" /></label>
      <label>Evaluation warm-up days<input v-model.number="model.evaluationWarmupDays" type="number" min="0" /></label>
    </div>

    <div v-if="resolved" class="ex-timeline" aria-label="Resolved experiment timeline">
      <div class="ex-timeline-lane">
        <span class="warmup">Training warm-up · {{ model.trainingWarmupDays }}d</span>
        <span class="learning">Learning · {{ durationDays(resolved.learningFrom, resolved.learningTo) }}d</span>
        <span class="embargo">No fitting · {{ actualEmbargoDays }}d</span>
        <span class="evaluation">Held-out evaluation · {{ durationDays(resolved.evaluationFrom, resolved.evaluationTo) }}d</span>
      </div>
      <div class="ex-timeline-dates mono">
        <span>{{ dateLabel(trainingStreamFrom) }}</span>
        <span>{{ dateLabel(resolved.learningFrom) }}</span>
        <span>{{ dateLabel(resolved.learningTo) }}</span>
        <span>{{ dateLabel(resolved.evaluationFrom) }}</span>
        <span>{{ dateLabel(resolved.evaluationTo) }}</span>
      </div>
      <div class="ex-evaluation-reader">
        Evaluation reader warm-up: {{ dateLabel(evaluationStreamFrom) }} → {{ dateLabel(resolved.evaluationFrom) }}
        ({{ model.evaluationWarmupDays }} days, no scoring)
      </div>
    </div>
    <p v-if="leakageWarning" class="ex-alert danger">{{ leakageWarning }}</p>
    <p v-else class="ex-alert safe">No overlap · exact external gap is {{ actualEmbargoDays }} full days.</p>
  </section>
</template>
