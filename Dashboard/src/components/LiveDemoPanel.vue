<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  type LiveManualCandidate,
  type LivePosition,
  useLiveEngineStatus,
} from '../composables/useLiveEngineStatus'
import LiveDecisionChart from './LiveDecisionChart.vue'

const {
  status,
  candidates,
  orders,
  positions,
  capabilities,
  connected,
  usingPolling,
  busy,
  error,
  pause,
  resume,
  reconcile,
  cancelPending,
  flatten,
  approveCandidate,
  rejectCandidate,
  closePosition,
  reducePosition,
  amendStop,
} = useLiveEngineStatus()

const reviewer = ref('operator')
const controlToken = ref('')
const reductionQuantity = ref<Record<string, number>>({})
const stopPrice = ref<Record<string, number>>({})
const confirmTitle = ref('')
const confirmDescription = ref('')
const confirmAction = ref<(() => Promise<unknown>) | null>(null)
const confirmDialog = ref<HTMLDialogElement | null>(null)

const engineStateClass = computed(() => (status.value?.engineState ?? 'starting').toLowerCase())
const runtime = computed(() => status.value?.runtime ?? null)
const pendingCandidates = computed(() => candidates.value.filter(candidate =>
  candidate.state === 'Pending' || candidate.state === 'Approved'))

function formatTime(value: string | null): string {
  if (!value) return '—'
  return new Date(value).toLocaleString()
}

function formatPrice(value: number | null): string {
  return value === null ? '—' : value.toFixed(5)
}

function formatNumber(value: number | null, digits = 2): string {
  return value === null ? '—' : value.toFixed(digits)
}

function formatRatio(value: number | null): string {
  return value === null ? '—' : value.toFixed(3)
}

function reviewerRequired(): string {
  const value = reviewer.value.trim()
  if (!value) throw new Error('Enter the operator/reviewer name first.')
  return value
}

function askConfirmation(title: string, description: string, action: () => Promise<unknown>) {
  confirmTitle.value = title
  confirmDescription.value = description
  confirmAction.value = action
  confirmDialog.value?.showModal()
}

async function executeConfirmed() {
  const action = confirmAction.value
  confirmDialog.value?.close()
  confirmAction.value = null
  if (!action) return
  try {
    await action()
  } catch {
    // The composable exposes the server error in the panel.
  }
}

function approve(item: LiveManualCandidate) {
  askConfirmation(
    `Approve ${item.action} ${item.instrument}`,
    `Submit ${item.quantity} units with stop ${formatPrice(item.stopLossPrice)}. The server will refresh price, account, reservation and portfolio state before sending.`,
    () => approveCandidate(item, reviewerRequired(), controlToken.value),
  )
}

function reject(item: LiveManualCandidate) {
  askConfirmation(
    `Reject ${item.instrument} candidate`,
    'Reject this candidate and release its reserved risk and margin.',
    () => rejectCandidate(item, reviewerRequired(), 'Operator rejected from Live Demo dashboard', controlToken.value),
  )
}

function close(item: LivePosition) {
  askConfirmation(
    `Close ${item.instrument} position`,
    `Close the full owned quantity (${item.quantity}). The broker stop remains active until OANDA confirms the close.`,
    () => closePosition(item, 'Operator full close from Live Demo dashboard', controlToken.value),
  )
}

function reduce(item: LivePosition) {
  const quantity = reductionQuantity.value[item.positionId] ?? 0
  askConfirmation(
    `Reduce ${item.instrument} position`,
    `Reduce the owned position by ${quantity} units. This command is available only after Practice-account partial-close certification is enabled.`,
    () => reducePosition(item, quantity, 'Operator partial close from Live Demo dashboard', controlToken.value),
  )
}

function moveStop(item: LivePosition) {
  const price = stopPrice.value[item.positionId] ?? 0
  askConfirmation(
    `Replace ${item.instrument} stop`,
    `Request a new protective stop at ${formatPrice(price)}. The existing stop is not cancelled first.`,
    () => amendStop(item, price, 'Operator stop replacement from Live Demo dashboard', controlToken.value),
  )
}
</script>

<template>
  <section class="live-demo-panel" aria-label="Live Demo">
    <p v-if="!status" class="live-demo-loading">Connecting to the independent live engine…</p>
    <template v-else>
      <section class="live-demo-section">
        <div class="section-heading">
          <h2>Engine and account safety</h2>
          <div class="live-demo-row">
            <span :class="['status-dot', engineStateClass]"></span>
            <strong>{{ status.engineState }}</strong>
          </div>
        </div>
        <div class="metric-grid">
          <div class="metric"><span>Transport</span><strong>{{ connected ? 'SignalR' : usingPolling ? 'Polling' : 'Disconnected' }}</strong></div>
          <div class="metric"><span>Broker writes</span><strong>{{ runtime?.brokerWritesEnabled ? 'Enabled' : 'Disabled' }}</strong></div>
          <div class="metric"><span>New entries</span><strong>{{ runtime?.canOpenNewEntries ? 'Allowed' : 'Paused' }}</strong></div>
          <div class="metric"><span>Safety</span><strong>{{ runtime?.safetyState ?? '—' }}</strong></div>
          <div class="metric"><span>Equity</span><strong>{{ formatNumber(runtime?.equity ?? null) }}</strong></div>
          <div class="metric"><span>Margin used</span><strong>{{ formatNumber(runtime?.marginUsed ?? null) }}</strong></div>
          <div class="metric"><span>Reservations</span><strong>{{ runtime?.activeReservations ?? 0 }}</strong></div>
          <div class="metric"><span>Reconciliation differences</span><strong>{{ runtime?.reconciliationDifferences ?? 0 }}</strong></div>
        </div>
        <p v-if="status.message || runtime?.safetyMessage" class="live-demo-warning">
          {{ status.message ?? runtime?.safetyMessage }}
        </p>
        <p v-if="error" class="live-demo-error">{{ error }}</p>
        <div class="control-fields">
          <label>Operator
            <input v-model="reviewer" autocomplete="off" />
          </label>
          <label>Control token
            <input v-model="controlToken" type="password" autocomplete="off" placeholder="Only when configured" />
          </label>
        </div>
        <div class="button-row">
          <button type="button" :disabled="busy" @click="askConfirmation('Pause new entries', 'Pause all new entries while continuing broker event processing and position management.', () => pause('Operator pause from dashboard', controlToken))">Pause</button>
          <button type="button" :disabled="busy" @click="askConfirmation('Resume entries', 'Reconcile authoritative OANDA state first, then resume only when safety permits.', () => resume(controlToken))">Resume</button>
          <button type="button" :disabled="busy" @click="askConfirmation('Reconcile now', 'Refresh account, orders, trades, stops and local reservations from OANDA.', () => reconcile(controlToken))">Reconcile</button>
          <button type="button" :disabled="busy || !runtime?.brokerWritesEnabled" @click="askConfirmation('Cancel pending entries', 'Cancel every owned pending entry order, then reconcile.', () => cancelPending(controlToken))">Cancel pending</button>
          <button class="danger" type="button" :disabled="busy || !runtime?.brokerWritesEnabled || positions.length === 0" @click="askConfirmation('Flatten all positions', `Close ${positions.length} owned position(s), preserve protection until each close is confirmed, and keep entries paused.`, () => flatten('Operator kill switch from dashboard', controlToken))">Flatten all</button>
        </div>
        <div class="live-demo-meta">
          <span>As of {{ formatTime(status.asOf) }}</span>
          <span>Last reconciliation {{ formatTime(runtime?.lastReconciledAt ?? null) }}</span>
        </div>
      </section>

      <section class="live-demo-section">
        <h2>Broker capability gates</h2>
        <div v-if="capabilities" class="capability-grid">
          <div><strong>Atomic stop on fill</strong><span>{{ capabilities.atomicStopOnFillImplemented ? 'Implemented' : 'Unavailable' }}</span></div>
          <div><strong>Full close</strong><span>{{ capabilities.explicitFullCloseImplemented ? 'Implemented' : 'Unavailable' }}</span></div>
          <div><strong>Partial close</strong><span>{{ capabilities.partialCloseEnabled ? 'Enabled' : capabilities.partialCloseImplemented ? 'Implemented, disabled pending certification' : 'Unavailable' }}</span></div>
          <div><strong>Dynamic stop replacement</strong><span>{{ capabilities.dynamicStopReplacementEnabled ? 'Enabled' : capabilities.dynamicStopReplacementImplemented ? 'Implemented, disabled pending certification' : 'Unavailable' }}</span></div>
        </div>
      </section>

      <section class="live-demo-section">
        <h2>Connections</h2>
        <div class="live-demo-row"><span :class="['status-dot', status.connections.quoteStreamConnected ? '' : 'faulted']"></span><span>Quote stream: {{ status.connections.quoteStreamConnected ? 'connected' : 'disconnected' }}</span></div>
        <div class="live-demo-row"><span :class="['status-dot', status.connections.restReachable ? '' : 'faulted']"></span><span>REST: {{ status.connections.restReachable ? 'reachable' : 'unreachable' }}</span></div>
        <div class="live-demo-row"><span>Account lease: {{ status.connections.leaseState }}</span></div>
        <div v-if="status.connections.lastQuoteStreamFaultAt" class="live-demo-meta">Last quote fault: {{ formatTime(status.connections.lastQuoteStreamFaultAt) }}</div>
      </section>

      <section class="live-demo-section">
        <h2>Manual candidates</h2>
        <p v-if="pendingCandidates.length === 0" class="live-demo-loading">No candidate is awaiting manual review.</p>
        <div v-else class="table-wrap">
          <table class="live-demo-table">
            <thead><tr><th>Expires</th><th>Strategy</th><th>Instrument</th><th>Action</th><th>Qty</th><th>Entry</th><th>Stop</th><th>Target</th><th>Stop risk</th><th>Meta p</th><th>Risk×</th><th>Actions</th></tr></thead>
            <tbody>
              <tr v-for="item in pendingCandidates" :key="item.candidateId">
                <td>{{ formatTime(item.expiresAt) }}</td><td>{{ item.strategyId }}</td><td>{{ item.instrument }}</td><td>{{ item.action }}</td>
                <td>{{ item.quantity }}</td><td>{{ formatPrice(item.referencePrice) }}</td><td>{{ formatPrice(item.stopLossPrice) }}</td><td>{{ formatPrice(item.takeProfitPrice) }}</td>
                <td>{{ formatNumber(item.estimatedStopRisk) }}</td><td>{{ formatRatio(item.metaProbability) }}</td><td>{{ formatRatio(item.combinedRiskMultiplier) }}</td>
                <td class="actions"><button type="button" :disabled="busy || !runtime?.brokerWritesEnabled" @click="approve(item)">Approve</button><button type="button" :disabled="busy" @click="reject(item)">Reject</button></td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section class="live-demo-section">
        <h2>Owned positions</h2>
        <p v-if="positions.length === 0" class="live-demo-loading">No owned OANDA trade is open.</p>
        <div v-else class="table-wrap">
          <table class="live-demo-table">
            <thead><tr><th>Strategy</th><th>Instrument</th><th>Side</th><th>Qty</th><th>Entry</th><th>Initial stop</th><th>Broker stop</th><th>Target</th><th>MFE R</th><th>MAE R</th><th>Management</th><th>Actions</th></tr></thead>
            <tbody>
              <tr v-for="item in positions" :key="item.positionId">
                <td>{{ item.strategyId }}</td><td>{{ item.instrument }}</td><td>{{ item.side }}</td><td>{{ item.quantity }}</td>
                <td>{{ formatPrice(item.averagePrice) }}</td><td>{{ formatPrice(item.initialStopPrice) }}</td><td>{{ formatPrice(item.protectiveStopPrice) }}</td><td>{{ formatPrice(item.takeProfitPrice) }}</td>
                <td>{{ formatRatio(item.maximumFavourableExcursionR) }}</td><td>{{ formatRatio(item.maximumAdverseExcursionR) }}</td><td :title="item.lastManagementReason ?? ''">{{ item.lastManagementAction ?? '—' }}</td>
                <td class="position-actions">
                  <button class="danger" type="button" :disabled="busy || !runtime?.brokerWritesEnabled" @click="close(item)">Close</button>
                  <div><input v-model.number="reductionQuantity[item.positionId]" type="number" min="0" :max="item.quantity" step="1" placeholder="Reduce qty" /><button type="button" :disabled="busy || !capabilities?.partialCloseEnabled" @click="reduce(item)">Reduce</button></div>
                  <div><input v-model.number="stopPrice[item.positionId]" type="number" min="0" step="0.00001" placeholder="New stop" /><button type="button" :disabled="busy || !capabilities?.dynamicStopReplacementEnabled" @click="moveStop(item)">Move stop</button></div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section class="live-demo-section">
        <h2>Orders</h2>
        <p v-if="orders.length === 0" class="live-demo-loading">No locally owned orders.</p>
        <div v-else class="table-wrap">
          <table class="live-demo-table"><thead><tr><th>Updated</th><th>Strategy</th><th>Instrument</th><th>Side</th><th>Requested</th><th>Filled</th><th>Average</th><th>Stop</th><th>Target</th><th>State</th><th>Reason</th></tr></thead><tbody><tr v-for="item in orders" :key="item.clientOrderId"><td>{{ formatTime(item.updatedAt) }}</td><td>{{ item.strategyId }}</td><td>{{ item.instrument }}</td><td>{{ item.side }}</td><td>{{ item.requestedQuantity }}</td><td>{{ item.filledQuantity }}</td><td>{{ formatPrice(item.averageFillPrice) }}</td><td>{{ formatPrice(item.protectiveStopPrice) }}</td><td>{{ formatPrice(item.takeProfitPrice) }}</td><td>{{ item.state }}</td><td>{{ item.lastReason ?? '—' }}</td></tr></tbody></table>
        </div>
      </section>

      <section class="live-demo-section">
        <h2>Markets</h2>
        <p v-if="status.markets.length === 0" class="live-demo-loading">No markets configured.</p>
        <div v-else class="table-wrap"><table class="live-demo-table"><thead><tr><th>Instrument</th><th>Bid</th><th>Ask</th><th>Spread</th><th>M1 close</th><th>Ready</th><th>Regime</th><th>State</th></tr></thead><tbody><tr v-for="market in status.markets" :key="market.instrument"><td>{{ market.instrument }}</td><td>{{ formatPrice(market.bid) }}</td><td>{{ formatPrice(market.ask) }}</td><td>{{ formatPrice(market.spread) }}</td><td>{{ formatTime(market.lastM1CloseAt) }}</td><td>{{ market.ready ? 'Yes' : 'No' }}</td><td>{{ market.regime ?? '—' }}</td><td>{{ market.state }}</td></tr></tbody></table></div>
      </section>

      <section class="live-demo-section">
        <h2>Shared market and Agent overlays</h2>
        <LiveDecisionChart :markets="status.markets" :agents="status.agents" />
      </section>

      <section class="live-demo-section">
        <h2>Analysis profiles</h2>
        <p v-if="status.analysisProfiles.length === 0" class="live-demo-loading">No analysis profile has published yet.</p>
        <div v-else class="table-wrap"><table class="live-demo-table"><thead><tr><th>Instrument</th><th>Profile</th><th>Intervals</th><th>Version</th><th>Available</th><th>Build ms</th><th>Hits</th><th>Misses</th><th>Failures</th><th>Agents</th></tr></thead><tbody><tr v-for="profile in status.analysisProfiles" :key="`${profile.instrument}:${profile.profileHash}`"><td>{{ profile.instrument }}</td><td :title="profile.profileHash">{{ profile.profileHash.slice(0, 10) }}</td><td>{{ profile.requiredIntervals.join(', ') }}</td><td>{{ profile.lastSnapshotVersion }}</td><td>{{ formatTime(profile.lastAvailableAt) }}</td><td>{{ profile.lastBuildDurationMilliseconds.toFixed(2) }}</td><td>{{ profile.cacheHits }}</td><td>{{ profile.cacheMisses }}</td><td>{{ profile.failureCount }}</td><td>{{ profile.dependentAgentCount }}</td></tr></tbody></table></div>
        <div v-if="status.lastDecisionEpoch" class="live-demo-meta">Last epoch {{ status.lastDecisionEpoch.epoch }} · {{ status.lastDecisionEpoch.completedAgents }}/{{ status.lastDecisionEpoch.expectedAgents }} completed · {{ status.lastDecisionEpoch.candidateCount }} candidates · {{ status.lastDecisionEpoch.durationMilliseconds.toFixed(2) }} ms · {{ status.lastDecisionEpoch.failedAgents }} failed · {{ status.lastDecisionEpoch.timedOutAgents }} timed out · {{ status.lastDecisionEpoch.missingAgents }} missing</div>
      </section>

      <section class="live-demo-section">
        <h2>Agents</h2>
        <p v-if="status.agents.length === 0" class="live-demo-loading">No agents configured.</p>
        <div v-else class="table-wrap"><table class="live-demo-table"><thead><tr><th>Strategy</th><th>Instrument</th><th>Policy rev</th><th>Profile</th><th>Mode</th><th>Health</th><th>Status</th><th>Epoch</th><th>Snapshot</th><th>Last evaluated</th><th>Evaluations</th><th>Avg/p95 ms</th><th>Mailbox</th><th>Timeouts</th><th>Action</th><th>Reference</th><th>Stop</th><th>Target</th><th>Decision</th><th>Candidate</th><th>Observed</th><th>Buy</th><th>Sell</th><th>Data rejects</th><th>Setup rejects</th><th>Meta rejects</th><th>Condition rejects</th><th>Last rejection</th><th>Meta p</th><th>Risk×</th></tr></thead><tbody><tr v-for="agent in status.agents" :key="`${agent.deploymentId}:${agent.instrument}:${agent.strategyId}:${agent.policyBundleId}:${agent.policyRevision}`"><td>{{ agent.strategyId }}</td><td>{{ agent.instrument }}</td><td>{{ agent.policyRevision }}</td><td :title="agent.analysisProfileHash">{{ agent.analysisProfileHash.slice(0, 10) }}</td><td>{{ agent.mode }}</td><td>{{ agent.health }}</td><td>{{ agent.lastStatus ?? '—' }}</td><td>{{ agent.lastEvaluatedEpoch || '—' }}</td><td>{{ agent.lastSnapshotVersion || '—' }}</td><td>{{ formatTime(agent.lastEvaluatedAt) }}</td><td>{{ agent.evaluations }}</td><td>{{ agent.averageEvaluationDurationMilliseconds.toFixed(2) }} / {{ agent.p95EvaluationDurationMilliseconds.toFixed(2) }}</td><td>{{ agent.mailboxDepth }}</td><td>{{ agent.timeouts }}</td><td>{{ agent.lastAction ?? '—' }}</td><td>{{ formatPrice(agent.lastReferencePrice) }}</td><td>{{ formatPrice(agent.lastStopLossPrice) }}</td><td>{{ formatPrice(agent.lastTakeProfitPrice) }}</td><td :title="agent.lastDecisionId ?? ''">{{ agent.lastDecisionId?.slice(0, 10) ?? '—' }}</td><td :title="agent.lastCandidateId ?? ''">{{ agent.lastCandidateId?.slice(0, 10) ?? '—' }}</td><td>{{ agent.candidatesObserved }}</td><td>{{ agent.candidatesBuy }}</td><td>{{ agent.candidatesSell }}</td><td>{{ agent.rejectedByDataQuality }}</td><td>{{ agent.rejectedBySetupCalibration }}</td><td>{{ agent.rejectedByMetaLabel }}</td><td>{{ agent.rejectedByTradingCondition }}</td><td :title="agent.lastRejection ?? ''">{{ agent.lastRejection ?? '—' }}</td><td>{{ formatRatio(agent.lastMetaLabelProbability) }}</td><td>{{ formatRatio(agent.lastMetaLabelRiskMultiplier) }}</td></tr></tbody></table></div>
      </section>
    </template>

    <dialog ref="confirmDialog" class="confirm-dialog">
      <h3>{{ confirmTitle }}</h3>
      <p>{{ confirmDescription }}</p>
      <div class="button-row"><button type="button" @click="confirmDialog?.close()">Cancel</button><button class="danger" type="button" :disabled="busy" @click="executeConfirmed">Confirm</button></div>
    </dialog>
  </section>
</template>

<style scoped>
.live-demo-panel { display: flex; flex-direction: column; gap: 20px; padding: 20px; }
.live-demo-section { border: 1px solid var(--border, #2a2f3a); border-radius: 8px; padding: 16px; }
.live-demo-section h2 { margin: 0 0 12px; font-size: 14px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted, #8b93a7); }
.section-heading, .live-demo-row, .button-row, .live-demo-meta { display: flex; align-items: center; gap: 10px; }
.section-heading { justify-content: space-between; }
.metric-grid, .capability-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(145px, 1fr)); gap: 10px; margin: 12px 0; }
.metric, .capability-grid > div { border: 1px solid var(--border, #2a2f3a); border-radius: 6px; padding: 10px; display: flex; flex-direction: column; gap: 4px; }
.metric span, .capability-grid span, .live-demo-meta { color: var(--muted, #8b93a7); font-size: 12px; }
.control-fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 10px; margin: 14px 0; }
label { display: flex; flex-direction: column; gap: 5px; font-size: 12px; color: var(--muted, #8b93a7); }
input { border: 1px solid var(--border, #2a2f3a); border-radius: 5px; padding: 7px; background: transparent; color: inherit; }
button { border: 1px solid var(--border, #2a2f3a); border-radius: 5px; padding: 7px 10px; background: transparent; color: inherit; cursor: pointer; }
button:hover:not(:disabled) { border-color: var(--accent, #6ea8fe); }
button:disabled { cursor: not-allowed; opacity: .45; }
button.danger { color: var(--coral, #f26c69); }
.button-row { flex-wrap: wrap; margin: 10px 0; }
.live-demo-warning, .live-demo-error { color: var(--coral, #f26c69); }
.live-demo-loading { color: var(--muted, #8b93a7); }
.table-wrap { overflow-x: auto; }
.live-demo-table { width: 100%; border-collapse: collapse; font-size: 13px; }
.live-demo-table th, .live-demo-table td { text-align: left; padding: 7px 9px; border-bottom: 1px solid var(--border, #2a2f3a); white-space: nowrap; vertical-align: top; }
.actions { display: flex; gap: 6px; }
.position-actions { display: flex; flex-direction: column; gap: 6px; }
.position-actions > div { display: flex; gap: 5px; }
.position-actions input { width: 100px; }
.confirm-dialog { max-width: 520px; border: 1px solid var(--border, #2a2f3a); border-radius: 8px; padding: 20px; background: var(--panel, #141820); color: inherit; }
.confirm-dialog::backdrop { background: rgba(0, 0, 0, .65); }
@media (max-width: 640px) { .live-demo-panel { padding: 10px; } .section-heading { align-items: flex-start; flex-direction: column; } }
</style>
