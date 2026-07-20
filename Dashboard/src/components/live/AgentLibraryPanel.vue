<script setup lang="ts">
import { computed, ref } from 'vue'
import type { AgentDescriptor, PolicyRevisionSummary, PolicySummary } from '../../types/liveDeployments'
import { policyStatusNames } from '../../types/liveDeployments'

const props = defineProps<{
  agentTypes: AgentDescriptor[]
  policies: PolicySummary[]
  revisions: PolicyRevisionSummary[]
}>()

const query = ref('')
const status = ref('')
const rows = computed(() => props.revisions.map(revision => ({
  revision,
  policy: props.policies.find(item => item.policyId === revision.policyId),
})).filter(({ revision, policy }) => {
  const search = query.value.trim().toLowerCase()
  const matchesText = !search || `${policy?.strategyId ?? ''} ${policy?.strategyVersion ?? ''} ${revision.policyRevisionId}`
    .toLowerCase().includes(search)
  return matchesText && (!status.value || String(revision.status) === status.value)
}))

function short(value: string): string {
  return value.length > 12 ? value.slice(0, 12) : value
}
</script>

<template>
  <section class="lifecycle-card">
    <div class="heading">
      <div><h2>Agent library</h2><p>Immutable PostgreSQL policy revisions available to the live host.</p></div>
      <span class="count">{{ rows.length }} revisions · {{ agentTypes.length }} Agent types</span>
    </div>
    <div class="filters">
      <label>Filter
        <input v-model="query" placeholder="Strategy, version, or exact ID" />
      </label>
      <label>Lifecycle
        <select v-model="status">
          <option value="">All statuses</option>
          <option v-for="(name, index) in policyStatusNames" :key="name" :value="String(index)">{{ name }}</option>
        </select>
      </label>
    </div>
    <div class="table-wrap">
      <table>
        <thead><tr><th>Strategy</th><th>Version</th><th>Revision</th><th>Status</th><th>Configuration hash</th><th>Exact revision ID</th></tr></thead>
        <tbody>
          <tr v-for="row in rows" :key="row.revision.policyRevisionId"
              :class="{ warning: row.revision.status === 3 || row.revision.status === 9 }">
            <td>{{ row.policy?.strategyId ?? 'Unknown policy' }}</td>
            <td>{{ row.policy?.strategyVersion ?? '—' }}</td>
            <td>{{ row.revision.revision }}</td>
            <td>{{ policyStatusNames[row.revision.status] ?? row.revision.status }}</td>
            <td :title="row.revision.configurationHash"><code>{{ short(row.revision.configurationHash) }}</code></td>
            <td :title="row.revision.policyRevisionId"><code>{{ short(row.revision.policyRevisionId) }}</code></td>
          </tr>
          <tr v-if="rows.length === 0"><td colspan="6" class="empty">No matching configured revisions.</td></tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

<style scoped>
.lifecycle-card { border: 1px solid var(--border, #2a2f3a); border-radius: 8px; padding: 16px; }
.heading { display: flex; align-items: start; justify-content: space-between; gap: 16px; }
h2 { margin: 0; font-size: 14px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted, #8b93a7); }
p, .count, label, .empty { color: var(--muted, #8b93a7); font-size: 12px; }
p { margin: 5px 0 12px; }
.filters { display: grid; grid-template-columns: minmax(220px, 1fr) minmax(170px, .35fr); gap: 10px; margin-bottom: 12px; }
label { display: flex; flex-direction: column; gap: 5px; }
input, select { border: 1px solid var(--border, #2a2f3a); border-radius: 5px; padding: 7px; background: var(--panel, #141820); color: inherit; }
.table-wrap { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th, td { text-align: left; padding: 7px 9px; border-bottom: 1px solid var(--border, #2a2f3a); white-space: nowrap; }
.warning { color: var(--coral, #f26c69); }
@media (max-width: 640px) { .heading, .filters { display: flex; flex-direction: column; } .filters > label { width: 100%; } }
</style>
