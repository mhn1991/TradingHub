<script setup lang="ts">
import { ref } from 'vue'
import type { DeploymentDetail, PolicyRevisionSummary, PolicySummary } from '../../types/liveDeployments'
import { agentModeNames, agentStatusNames, deploymentStatusNames } from '../../types/liveDeployments'

defineProps<{
  deployments: DeploymentDetail[]
  revisions: PolicyRevisionSummary[]
  policies: PolicySummary[]
  busy: boolean
  realtime: boolean
}>()

const emit = defineEmits<{
  deploymentCommand: [detail: DeploymentDetail, command: string]
  agentCommand: [detail: DeploymentDetail, agentId: string, command: string, revisionId?: string]
  refresh: []
}>()
const replacements = ref<Record<string, string>>({})

function short(value: string): string { return value.slice(0, 12) }
function revisionLabel(item: PolicyRevisionSummary, policies: PolicySummary[]): string {
  const policy = policies.find(value => value.policyId === item.policyId)
  return `${policy?.strategyId ?? 'Unknown'} r${item.revision}`
}
function dangerous(message: string, action: () => void) {
  if (window.confirm(message)) action()
}
</script>

<template>
  <section class="lifecycle-card">
    <div class="heading">
      <div><h2>Active deployments</h2><p>Parent runtimes and their exact, independently controlled Agent assignments.</p></div>
      <div class="transport"><span :class="['dot', realtime ? 'online' : '']"></span>{{ realtime ? 'SignalR' : 'Polling fallback' }} <button type="button" @click="emit('refresh')">Refresh</button></div>
    </div>
    <p v-if="deployments.length === 0" class="empty">No lifecycle deployments have been requested.</p>
    <article v-for="detail in deployments" :key="detail.deployment.deploymentId" class="deployment">
      <div class="deployment-title">
        <div><strong>{{ deploymentStatusNames[detail.deployment.status] ?? detail.deployment.status }}</strong> · {{ detail.deployment.environment }} · account <code>{{ short(detail.deployment.brokerAccountId) }}</code></div>
        <span>v{{ detail.deployment.version }} · host {{ detail.deployment.hostInstanceId }}</span>
      </div>
      <div class="actions">
        <button :disabled="busy" type="button" @click="emit('deploymentCommand', detail, 'pause')">Pause entries</button>
        <button :disabled="busy" type="button" @click="emit('deploymentCommand', detail, 'resume')">Resume</button>
        <button :disabled="busy" type="button" @click="emit('deploymentCommand', detail, 'reconcile')">Reconcile</button>
        <button class="danger" :disabled="busy" type="button" @click="dangerous('Stop this deployment? Executable agents will drain before runtime removal.', () => emit('deploymentCommand', detail, 'stop'))">Stop deployment</button>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Agent</th><th>Strategy</th><th>Instrument</th><th>Mode</th><th>Status</th><th>Package</th><th>Fault</th><th>Controls</th><th>Replacement</th></tr></thead>
          <tbody><tr v-for="agent in detail.agents" :key="agent.deploymentAgentId">
            <td :title="agent.deploymentAgentId"><code>{{ short(agent.deploymentAgentId) }}</code></td>
            <td>{{ agent.strategyId }}<small :title="agent.policyRevisionId">rev {{ short(agent.policyRevisionId) }}</small></td>
            <td>{{ agent.instrumentId }}</td>
            <td>{{ agentModeNames[agent.agentMode] ?? agent.agentMode }}</td>
            <td>{{ agentStatusNames[agent.status] ?? agent.status }}</td>
            <td :title="agent.packageHash"><code>{{ short(agent.packageHash) }}</code></td>
            <td class="fault">{{ agent.faultCode ?? agent.faultMessage ?? '—' }}</td>
            <td><div class="agent-actions">
              <button :disabled="busy" type="button" @click="emit('agentCommand', detail, agent.deploymentAgentId, 'pause')">Pause</button>
              <button :disabled="busy" type="button" @click="emit('agentCommand', detail, agent.deploymentAgentId, 'resume')">Resume</button>
              <button :disabled="busy" type="button" @click="emit('agentCommand', detail, agent.deploymentAgentId, 'drain')">Drain</button>
              <button class="danger" :disabled="busy" type="button" @click="dangerous('Stop this Agent? Open live positions remain managed by their pinned entry package.', () => emit('agentCommand', detail, agent.deploymentAgentId, 'stop'))">Stop</button>
            </div></td>
            <td><div class="replace">
              <select v-model="replacements[agent.deploymentAgentId]">
                <option value="">Exact challenger revision</option>
                <option v-for="revision in revisions.filter(item => item.policyRevisionId !== agent.policyRevisionId)" :key="revision.policyRevisionId" :value="revision.policyRevisionId">{{ revisionLabel(revision, policies) }}</option>
              </select>
              <button :disabled="busy || !replacements[agent.deploymentAgentId]" type="button" @click="dangerous('Preflight and hot-swap to this exact revision at a decision-epoch boundary?', () => emit('agentCommand', detail, agent.deploymentAgentId, 'replace', replacements[agent.deploymentAgentId]))">Replace</button>
            </div></td>
          </tr></tbody>
        </table>
      </div>
      <details v-if="detail.recentEvents.length"><summary>Recent audit events ({{ detail.recentEvents.length }})</summary>
        <ul><li v-for="event in detail.recentEvents" :key="event.eventId"><time>{{ new Date(event.occurredAt).toLocaleString() }}</time> · {{ event.eventType }} · {{ event.actorIdentity }}<span v-if="event.reasonCode"> · {{ event.reasonCode }}</span></li></ul>
      </details>
    </article>
  </section>
</template>

<style scoped>
.lifecycle-card { border: 1px solid var(--border, #2a2f3a); border-radius: 8px; padding: 16px; }
.heading, .deployment-title, .actions, .transport, .agent-actions, .replace { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.heading, .deployment-title { justify-content: space-between; align-items: start; }
h2 { margin: 0; font-size: 14px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted, #8b93a7); }
p, .transport, .deployment-title span, .empty, details, small { color: var(--muted, #8b93a7); font-size: 12px; }
.heading p { margin: 5px 0 12px; }
.dot { width: 7px; height: 7px; border-radius: 50%; background: #d39b4a; }.dot.online { background: #55c57a; }
.deployment { border-top: 1px solid var(--border, #2a2f3a); padding-top: 14px; margin-top: 14px; }
.deployment-title { margin-bottom: 8px; }
.actions { margin: 8px 0; }
button, select { border: 1px solid var(--border, #2a2f3a); border-radius: 5px; padding: 6px 8px; background: var(--panel, #141820); color: inherit; }
button { cursor: pointer; }button:disabled { cursor: not-allowed; opacity: .45; }button.danger, .fault { color: var(--coral, #f26c69); }
.table-wrap { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th, td { text-align: left; padding: 7px 9px; border-bottom: 1px solid var(--border, #2a2f3a); white-space: nowrap; vertical-align: top; }
small { display: block; margin-top: 3px; }.agent-actions { max-width: 190px; }.replace { flex-wrap: nowrap; }
summary { cursor: pointer; margin-top: 10px; }ul { padding-left: 20px; }
</style>
