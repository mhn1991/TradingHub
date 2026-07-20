<script setup lang="ts">
import type { SimulationExperimentComparisonSummary } from '../../../types/simulation-experiments'

defineProps<{ comparison: SimulationExperimentComparisonSummary }>()

const number = (value?: number | null, digits = 2): string => value == null ? '—' : value.toLocaleString(undefined, {
  minimumFractionDigits: digits,
  maximumFractionDigits: digits,
})
</script>

<template>
  <section class="ex-card comparison-card">
    <div class="ex-card-heading"><div><span class="ex-step">7</span><h3>Held-out comparison</h3></div><span class="ex-badge">{{ comparison.objective }}</span></div>
    <div class="ex-table-wrap">
      <table class="ex-table numeric">
        <thead><tr><th>Profile</th><th>Net P&amp;L</th><th>Net R</th><th>Expectancy</th><th>Max DD</th><th>Profit factor</th><th>Trades</th><th>Δ baseline</th></tr></thead>
        <tbody>
          <tr v-for="row in comparison.profiles" :key="row.profileRunId" :class="{ baseline: row.profileRunId === comparison.baselineProfileRunId }">
            <td><strong>{{ row.profileName }}</strong><small v-if="row.warnings.length">{{ row.warnings.join(' · ') }}</small></td>
            <td>{{ number(row.netProfit) }}</td><td>{{ number(row.netR) }}</td><td>{{ number(row.expectancy, 3) }}</td>
            <td>{{ number(row.maximumDrawdown) }}</td><td>{{ number(row.profitFactor) }}</td><td>{{ row.tradeCount }}</td><td>{{ number(row.differenceFromBaseline) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
