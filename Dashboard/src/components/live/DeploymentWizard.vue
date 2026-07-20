<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type {
  BrokerAccountOption,
  DeploymentPreflightRequest,
  DeploymentPreflightResult,
  InstrumentOption,
  PolicyRevisionSummary,
  PolicySummary,
} from '../../types/liveDeployments'
import { policyStatusNames } from '../../types/liveDeployments'

const props = defineProps<{
  accounts: BrokerAccountOption[]
  instruments: InstrumentOption[]
  policies: PolicySummary[]
  revisions: PolicyRevisionSummary[]
  busy: boolean
  result: DeploymentPreflightResult | null
}>()

const emit = defineEmits<{
  accountChanged: [brokerAccountId: string]
  changed: []
  preflight: [request: DeploymentPreflightRequest]
  start: [request: DeploymentPreflightRequest]
}>()

const form = reactive({ accountId: '', instrumentId: 0, revisionId: '', mode: 1 })
const parityRequired = ref(true)

const account = computed(() => props.accounts.find(item => item.brokerAccountId === form.accountId))
const revision = computed(() => props.revisions.find(item => item.policyRevisionId === form.revisionId))
const selectedPolicy = computed(() => props.policies.find(item => item.policyId === revision.value?.policyId))
const complete = computed(() => Boolean(account.value && form.instrumentId && revision.value))
const request = computed<DeploymentPreflightRequest>(() => ({
  brokerAccountId: form.accountId,
  brokerEnvironment: account.value?.environment ?? '',
  instrumentId: form.instrumentId,
  policyRevisionId: form.revisionId,
  mode: form.mode,
  expectedConfigurationHash: revision.value?.configurationHash,
  requireParityCertification: parityRequired.value,
}))

watch(() => form.accountId, value => {
  form.instrumentId = 0
  emit('accountChanged', value)
})
watch(() => [form.accountId, form.instrumentId, form.revisionId, form.mode, parityRequired.value], () => {
  // The parent clears stale gates whenever a deployment input changes.
  emit('changed')
})

function policyLabel(item: PolicyRevisionSummary): string {
  const policy = props.policies.find(value => value.policyId === item.policyId)
  return `${policy?.strategyId ?? 'Unknown'} ${policy?.strategyVersion ?? ''} · r${item.revision} · ${policyStatusNames[item.status] ?? item.status}`
}
</script>

<template>
  <section class="lifecycle-card">
    <div class="heading"><div><h2>Guided deployment</h2><p>Select exact database-backed inputs, pass every gate, then start.</p></div><span class="step">Preflight first</span></div>
    <div class="form-grid">
      <label>1. Account / environment
        <select v-model="form.accountId">
          <option value="">Select an enabled account</option>
          <option v-for="item in accounts" :key="item.brokerAccountId" :value="item.brokerAccountId">
            {{ item.broker }} · {{ item.displayName }} · {{ item.environment }}{{ item.isLive ? ' LIVE' : '' }}
          </option>
        </select>
      </label>
      <label>2. Exact configured revision
        <select v-model="form.revisionId">
          <option value="">Select an immutable revision</option>
          <option v-for="item in revisions" :key="item.policyRevisionId" :value="item.policyRevisionId">{{ policyLabel(item) }}</option>
        </select>
      </label>
      <label>3. Compatible instrument
        <select v-model.number="form.instrumentId" :disabled="!form.accountId">
          <option :value="0">Select a current tradeable mapping</option>
          <option v-for="item in instruments" :key="item.instrumentId" :value="item.instrumentId">{{ item.displayName }} · {{ item.brokerSymbol }}</option>
        </select>
      </label>
      <label>4. Activation mode
        <select v-model.number="form.mode">
          <option :value="0">Record only</option>
          <option :value="1">Shadow</option>
          <option :value="2">Manual approval</option>
          <option :value="3">Automatic</option>
        </select>
      </label>
    </div>
    <div v-if="revision" class="selection">
      <span>{{ selectedPolicy?.strategyId }} r{{ revision.revision }}</span>
      <code :title="revision.configurationHash">config {{ revision.configurationHash.slice(0, 12) }}</code>
      <span>{{ account?.environment }}</span>
      <label class="check"><input v-model="parityRequired" type="checkbox" /> Require current parity certification</label>
    </div>
    <div class="actions">
      <button type="button" :disabled="busy || !complete" @click="emit('preflight', request)">Run preflight</button>
      <button class="primary" type="button" :disabled="busy || !result?.passed" @click="emit('start', request)">Start deployment</button>
    </div>
    <div v-if="result" class="gates" :class="{ failed: !result.passed }">
      <strong>{{ result.passed ? 'All mandatory gates passed' : 'Start blocked by preflight' }}</strong>
      <div v-for="gate in result.gates" :key="gate.code" class="gate">
        <span :class="gate.passed ? 'pass' : 'fail'">{{ gate.passed ? '✓' : '×' }}</span>
        <div><code>{{ gate.code }}</code><p>{{ gate.message }}</p></div>
      </div>
      <p v-if="result.packageHash" class="hash">Pinned package: <code>{{ result.packageHash }}</code></p>
    </div>
  </section>
</template>

<style scoped>
.lifecycle-card { border: 1px solid var(--border, #2a2f3a); border-radius: 8px; padding: 16px; }
.heading, .selection, .actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.heading { justify-content: space-between; align-items: start; }
h2 { margin: 0; font-size: 14px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted, #8b93a7); }
p, label, .step { color: var(--muted, #8b93a7); font-size: 12px; }
.heading p { margin: 5px 0 12px; }
.form-grid { display: grid; grid-template-columns: repeat(2, minmax(220px, 1fr)); gap: 10px; }
label { display: flex; flex-direction: column; gap: 5px; }
select { border: 1px solid var(--border, #2a2f3a); border-radius: 5px; padding: 7px; background: var(--panel, #141820); color: inherit; }
.selection { margin: 12px 0; font-size: 12px; }
.check { flex-direction: row; align-items: center; }
button { border: 1px solid var(--border, #2a2f3a); border-radius: 5px; padding: 7px 10px; background: transparent; color: inherit; cursor: pointer; }
button.primary { border-color: var(--accent, #6ea8fe); }
button:disabled { cursor: not-allowed; opacity: .45; }
.gates { margin-top: 14px; border-left: 3px solid #55c57a; padding: 8px 12px; }
.gates.failed { border-color: var(--coral, #f26c69); }
.gate { display: flex; gap: 9px; margin-top: 8px; }
.gate p { margin: 2px 0; }
.pass { color: #55c57a; }.fail { color: var(--coral, #f26c69); }
.hash { overflow-wrap: anywhere; }
@media (max-width: 700px) { .form-grid { grid-template-columns: 1fr; } }
</style>
