<script setup lang="ts">
import { ref } from 'vue'
import { useLiveDeployments } from '../../composables/useLiveDeployments'
import type {
  DeploymentDetail,
  DeploymentPreflightRequest,
  DeploymentPreflightResult,
  LifecycleMutationContext,
} from '../../types/liveDeployments'
import ActiveDeploymentsPanel from './ActiveDeploymentsPanel.vue'
import AgentLibraryPanel from './AgentLibraryPanel.vue'
import DeploymentWizard from './DeploymentWizard.vue'

const {
  agentTypes, policies, revisions, accounts, instruments, deployments, busy, error, connected,
  refreshDeployments, loadInstruments, preflight, start, deploymentCommand, agentCommand,
} = useLiveDeployments()

const actor = ref('operator')
const reason = ref('Dashboard lifecycle operation')
const controlToken = ref('')
const preflightResult = ref<DeploymentPreflightResult | null>(null)
const notice = ref<string | null>(null)

function context(): LifecycleMutationContext {
  return { actor: actor.value, reason: reason.value, controlToken: controlToken.value }
}

async function runPreflight(request: DeploymentPreflightRequest) {
  preflightResult.value = null
  notice.value = null
  try {
    preflightResult.value = await preflight(request)
  } catch {
    // The composable exposes the structured server failure.
  }
}

async function startDeployment(request: DeploymentPreflightRequest) {
  if (!preflightResult.value?.passed) return
  const selected = accounts.value.find(item => item.brokerAccountId === request.brokerAccountId)
  if (selected?.isLive && !window.confirm('This targets a LIVE broker environment. Start the exact preflighted deployment?')) return
  try {
    await start({ ...request, expectedPackageHash: preflightResult.value.packageHash ?? undefined }, context())
    notice.value = 'Deployment request persisted. The live host will validate, warm, reconcile, and activate it.'
    preflightResult.value = null
  } catch {
    // The composable exposes the structured server failure.
  }
}

async function runDeploymentCommand(detail: DeploymentDetail, command: string) {
  try { await deploymentCommand(detail, command, context()) } catch { /* shown below */ }
}

async function runAgentCommand(detail: DeploymentDetail, agentId: string, command: string,
    replacementRevisionId?: string) {
  try { await agentCommand(detail, agentId, command, context(), replacementRevisionId) } catch { /* shown below */ }
}
</script>

<template>
  <section class="lifecycle-shell" aria-label="Agent lifecycle deployment">
    <header>
      <div><h1>Agent deployment control</h1><p>PostgreSQL-authoritative, exact-revision lifecycle management. Secrets never enter this page.</p></div>
      <div class="operator-fields">
        <label>Operator<input v-model="actor" autocomplete="off" /></label>
        <label>Audit reason<input v-model="reason" autocomplete="off" /></label>
        <label>Control token<input v-model="controlToken" type="password" autocomplete="off" placeholder="When configured" /></label>
      </div>
    </header>
    <p v-if="notice" class="notice">{{ notice }}</p>
    <p v-if="error" class="error">{{ error }}</p>
    <AgentLibraryPanel :agent-types="agentTypes" :policies="policies" :revisions="revisions" />
    <DeploymentWizard
      :accounts="accounts" :instruments="instruments" :policies="policies" :revisions="revisions"
      :busy="busy" :result="preflightResult"
      @account-changed="loadInstruments" @changed="preflightResult = null"
      @preflight="runPreflight" @start="startDeployment" />
    <ActiveDeploymentsPanel
      :deployments="deployments" :revisions="revisions" :policies="policies"
      :busy="busy" :realtime="connected"
      @deployment-command="runDeploymentCommand" @agent-command="runAgentCommand"
      @refresh="refreshDeployments" />
  </section>
</template>

<style scoped>
.lifecycle-shell { display: flex; flex-direction: column; gap: 14px; }
header { border: 1px solid color-mix(in srgb, var(--accent, #6ea8fe) 50%, var(--border, #2a2f3a)); border-radius: 8px; padding: 16px; }
header h1 { margin: 0; font-size: 17px; }header p { margin: 5px 0 12px; color: var(--muted, #8b93a7); font-size: 12px; }
.operator-fields { display: grid; grid-template-columns: minmax(150px, .5fr) minmax(230px, 1fr) minmax(180px, .6fr); gap: 10px; }
label { display: flex; flex-direction: column; gap: 5px; color: var(--muted, #8b93a7); font-size: 12px; }
input { border: 1px solid var(--border, #2a2f3a); border-radius: 5px; padding: 7px; background: transparent; color: inherit; }
.notice { color: #55c57a; }.error { color: var(--coral, #f26c69); }.notice, .error { margin: 0; font-size: 13px; }
@media (max-width: 760px) { .operator-fields { grid-template-columns: 1fr; } }
</style>
