<script setup lang="ts">
import { computed, reactive, ref, watchEffect } from 'vue'
import type { LiveAgentStatus, LiveMarketStatus } from '../composables/useLiveEngineStatus'

const props = defineProps<{ markets: LiveMarketStatus[], agents: LiveAgentStatus[] }>()
const selectedInstrument = ref('')
const visible = reactive<Record<string, boolean>>({})
const width = 900
const height = 280
const top = 28
const bottom = 36

const instruments = computed(() => props.markets.map(item => item.instrument))
watchEffect(() => {
  if (!instruments.value.includes(selectedInstrument.value))
    selectedInstrument.value = instruments.value[0] ?? ''
  for (const agent of props.agents) {
    const key = agentKey(agent)
    if (!(key in visible)) visible[key] = true
  }
})

const market = computed(() => props.markets.find(item => item.instrument === selectedInstrument.value) ?? null)
const instrumentAgents = computed(() => props.agents.filter(item => item.instrument === selectedInstrument.value))
const plottedAgents = computed(() => instrumentAgents.value.filter(item => visible[agentKey(item)] && item.lastReferencePrice !== null))
const prices = computed(() => {
  const values = [market.value?.bid, market.value?.ask]
  for (const agent of plottedAgents.value)
    values.push(agent.lastReferencePrice, agent.lastStopLossPrice, agent.lastTakeProfitPrice)
  const finite = values.filter((value): value is number => value !== null && value !== undefined && Number.isFinite(value))
  if (finite.length === 0) return { min: 0, max: 1 }
  const min = Math.min(...finite)
  const max = Math.max(...finite)
  const padding = Math.max((max - min) * 0.12, Math.abs(max || 1) * 0.0001)
  return { min: min - padding, max: max + padding }
})

function agentKey(agent: LiveAgentStatus): string {
  return `${agent.deploymentId}:${agent.instrument}:${agent.strategyId}:${agent.policyBundleId}:${agent.policyRevision}`
}

function y(price: number | null): number {
  if (price === null) return height / 2
  return top + ((prices.value.max - price) / (prices.value.max - prices.value.min)) * (height - top - bottom)
}

function color(agent: LiveAgentStatus): string {
  let hash = 0
  for (const char of agent.analysisProfileHash) hash = ((hash << 5) - hash + char.charCodeAt(0)) | 0
  return `hsl(${Math.abs(hash) % 360} 72% 58%)`
}

function short(value: string): string { return value.slice(0, 8) }
function price(value: number): string { return value.toFixed(5) }
</script>

<template>
  <section class="live-chart" aria-label="Shared live market and agent decision overlays">
    <div class="chart-toolbar">
      <label>Instrument
        <select v-model="selectedInstrument">
          <option v-for="item in instruments" :key="item" :value="item">{{ item }}</option>
        </select>
      </label>
      <span>One shared market layer · independently selectable Agent overlays</span>
    </div>
    <div class="overlay-controls">
      <label v-for="agent in instrumentAgents" :key="agentKey(agent)">
        <input v-model="visible[agentKey(agent)]" type="checkbox" />
        <i :style="{ background: color(agent) }"></i>
        {{ agent.strategyId }} r{{ agent.policyRevision }} · {{ short(agent.analysisProfileHash) }}
      </label>
    </div>
    <svg :viewBox="`0 0 ${width} ${height}`" role="img">
      <rect x="0" y="0" :width="width" :height="height" class="plot" />
      <template v-if="market && market.bid !== null && market.ask !== null">
        <rect x="0" :y="y(market.ask)" :width="width" :height="Math.max(2, y(market.bid) - y(market.ask))" class="spread" />
        <line x1="0" :x2="width" :y1="y(market.bid)" :y2="y(market.bid)" class="market-line" />
        <text x="8" :y="y(market.bid) - 6" class="market-label">shared {{ market.instrument }} bid {{ price(market.bid) }}</text>
      </template>
      <g v-for="(agent, index) in plottedAgents" :key="agentKey(agent)">
        <line v-if="agent.lastStopLossPrice !== null" :x1="80 + index * 24" :x2="width - 20" :y1="y(agent.lastStopLossPrice)" :y2="y(agent.lastStopLossPrice)" class="risk-line" :stroke="color(agent)" />
        <line v-if="agent.lastTakeProfitPrice !== null" :x1="80 + index * 24" :x2="width - 20" :y1="y(agent.lastTakeProfitPrice)" :y2="y(agent.lastTakeProfitPrice)" class="target-line" :stroke="color(agent)" />
        <circle :cx="100 + index * 42" :cy="y(agent.lastReferencePrice)" r="7" :fill="color(agent)" />
        <text :x="112 + index * 42" :y="y(agent.lastReferencePrice) + 4" :fill="color(agent)">{{ agent.strategyId }} r{{ agent.policyRevision }} {{ agent.lastAction ?? '' }}</text>
        <title>{{ agent.strategyId }} · policy {{ agent.policyBundleId }} r{{ agent.policyRevision }} · profile {{ agent.analysisProfileHash }} · decision {{ agent.lastDecisionId ?? 'none' }}</title>
      </g>
      <text v-if="plottedAgents.length === 0" x="450" y="140" text-anchor="middle" class="empty">No selected Agent has emitted a price marker yet.</text>
    </svg>
  </section>
</template>

<style scoped>
.live-chart { display: grid; gap: .75rem; }
.chart-toolbar, .overlay-controls { display: flex; flex-wrap: wrap; align-items: center; gap: .75rem 1rem; }
.chart-toolbar label, .overlay-controls label { display: flex; align-items: center; gap: .4rem; }
.chart-toolbar span { color: var(--muted, #8d99a8); font-size: .85rem; }
.overlay-controls i { width: .7rem; height: .7rem; border-radius: 50%; }
svg { width: 100%; min-height: 240px; border: 1px solid rgba(140, 160, 180, .25); border-radius: .5rem; }
.plot { fill: rgba(14, 22, 34, .65); }
.spread { fill: rgba(83, 174, 255, .14); }
.market-line { stroke: #6ec7ff; stroke-width: 1.5; }
.market-label, .empty { fill: #aebdca; font-size: 12px; }
.risk-line { stroke-width: 1; stroke-dasharray: 5 5; opacity: .65; }
.target-line { stroke-width: 1; stroke-dasharray: 2 4; opacity: .75; }
g text { font-size: 11px; }
</style>
