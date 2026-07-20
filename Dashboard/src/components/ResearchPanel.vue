<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import type {
  CalibrationArtifactMetadata,
  CalibrationBundleCandidate,
  ResearchArtifactSelection,
  ResearchJobSnapshot,
  TradingPolicyProfile,
} from '../types'
import { RESEARCH_ARTIFACT_SELECTION_KEY } from '../types'

function evaluationWindowMonths(months: number): { from: string; to: string } {
  const now = new Date()
  const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1))
  const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - months, 1))
  return {
    from: from.toISOString().slice(0, 10),
    to: to.toISOString().slice(0, 10),
  }
}

const defaultWindow = evaluationWindowMonths(1)

const form = reactive({
  kind: 'setup' as 'setup' | 'management' | 'metamodel',
  instrument: 'FX:EUR/USD',
  strategy: 'improved',
  from: defaultWindow.from,
  to: defaultWindow.to,
  startingBalance: 100_000,
  quantity: 1_000,
  sourceKind: 'OandaCandles',
  warmupDays: 21,
  description: '',
})

const selection = reactive<ResearchArtifactSelection>({
  setupCalibrationArtifactId: '',
  managementCalibrationArtifactId: '',
  metaModelArtifactId: '',
})

const artifacts = ref<CalibrationArtifactMetadata[]>([])
const jobs = ref<ResearchJobSnapshot[]>([])
const filterType = ref<'all' | 'Setup' | 'Management' | 'MetaModel'>('all')
const busy = ref(false)
const loading = ref(false)
const error = ref<string | null>(null)
const notice = ref<string | null>(null)
const activeJobId = ref<string | null>(null)

const policyProfiles = ref<TradingPolicyProfile[]>([])
const candidates = ref<CalibrationBundleCandidate[]>([])
const candidateStatusFilter = ref<'PendingReview' | 'all'>('PendingReview')
const candidateBusyId = ref<string | null>(null)

let pollTimer: number | undefined

const filteredArtifacts = computed(() => {
  if (filterType.value === 'all') return artifacts.value
  return artifacts.value.filter(item => item.type === filterType.value)
})

const activeJob = computed(() =>
  jobs.value.find(job => job.jobId === activeJobId.value) ?? jobs.value[0] ?? null)

const selectionSummary = computed(() => {
  const parts = [
    selection.setupCalibrationArtifactId ? 'setup' : null,
    selection.managementCalibrationArtifactId ? 'management' : null,
    selection.metaModelArtifactId ? 'meta-model' : null,
  ].filter(Boolean)
  return parts.length ? parts.join(' · ') : 'none selected'
})

onMounted(async () => {
  loadSelection()
  await refreshAll()
  pollTimer = window.setInterval(() => {
    void refreshJobs()
    void refreshCandidates()
    if (activeJob.value && (activeJob.value.status === 'Queued' || activeJob.value.status === 'Running')) {
      void refreshArtifacts()
    }
  }, 2500)
})

onBeforeUnmount(() => {
  if (pollTimer != null) window.clearInterval(pollTimer)
})

function apiBase(): string {
  return import.meta.env.BASE_URL
}

async function readApiError(response: Response, fallback: string): Promise<string> {
  try {
    const body = await response.json() as { error?: string; title?: string; detail?: string }
    return body.error ?? body.detail ?? body.title ?? fallback
  } catch {
    return `${fallback} (HTTP ${response.status})`
  }
}

function loadSelection() {
  try {
    const raw = localStorage.getItem(RESEARCH_ARTIFACT_SELECTION_KEY)
    if (!raw) return
    const parsed = JSON.parse(raw) as Partial<ResearchArtifactSelection>
    selection.setupCalibrationArtifactId = parsed.setupCalibrationArtifactId ?? ''
    selection.managementCalibrationArtifactId = parsed.managementCalibrationArtifactId ?? ''
    selection.metaModelArtifactId = parsed.metaModelArtifactId ?? ''
  } catch {
    // ignore corrupt storage
  }
}

function persistSelection() {
  const payload: ResearchArtifactSelection = {
    setupCalibrationArtifactId: selection.setupCalibrationArtifactId.trim(),
    managementCalibrationArtifactId: selection.managementCalibrationArtifactId.trim(),
    metaModelArtifactId: selection.metaModelArtifactId.trim(),
  }
  localStorage.setItem(RESEARCH_ARTIFACT_SELECTION_KEY, JSON.stringify(payload))
  notice.value = `Simulator will use: ${selectionSummary.value}. Open the Simulator tab and start a run.`
}

function clearSelection() {
  selection.setupCalibrationArtifactId = ''
  selection.managementCalibrationArtifactId = ''
  selection.metaModelArtifactId = ''
  persistSelection()
  notice.value = 'Cleared research artifacts for the next simulation.'
}

function useArtifact(artifact: CalibrationArtifactMetadata) {
  const id = artifact.id
  if (artifact.type === 'Setup') selection.setupCalibrationArtifactId = id
  else if (artifact.type === 'Management') selection.managementCalibrationArtifactId = id
  else if (artifact.type === 'MetaModel') selection.metaModelArtifactId = id
  else {
    error.value = `Unknown artifact type '${artifact.type}'.`
    return
  }
  persistSelection()
}

function useJobArtifact(job: ResearchJobSnapshot) {
  if (!job.artifactId || !job.artifactType) return
  useArtifact({
    id: job.artifactId,
    type: job.artifactType,
    schemaVersion: 1,
    calibrationId: job.calibrationId ?? '',
    createdAt: job.completedAt ?? job.createdAt,
    contentHash: '',
    description: job.message,
  })
}

async function refreshAll() {
  loading.value = true
  error.value = null
  try {
    await Promise.all([refreshArtifacts(), refreshJobs(), refreshPolicyProfiles(), refreshCandidates()])
  } finally {
    loading.value = false
  }
}

async function refreshPolicyProfiles() {
  const response = await fetch(`${apiBase()}api/trading-policy-profiles`)
  if (!response.ok) return
  policyProfiles.value = await response.json() as TradingPolicyProfile[]
}

async function refreshCandidates() {
  const query = candidateStatusFilter.value === 'all' ? '' : `?status=${candidateStatusFilter.value}`
  const response = await fetch(`${apiBase()}api/calibration-candidates${query}`)
  if (!response.ok) return
  candidates.value = await response.json() as CalibrationBundleCandidate[]
}

async function approveCandidate(candidate: CalibrationBundleCandidate) {
  const approvedBy = window.prompt('Approve as (your name)?')
  if (!approvedBy?.trim()) return
  candidateBusyId.value = candidate.id
  error.value = null
  try {
    const response = await fetch(`${apiBase()}api/calibration-candidates/${candidate.id}/approve`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ approvedBy: approvedBy.trim() }),
    })
    if (!response.ok) throw new Error(await readApiError(response, 'Could not approve candidate'))
    notice.value = `Approved candidate ${shortId(candidate.id)} - now live in the policy store.`
    await Promise.all([refreshCandidates(), refreshPolicyProfiles()])
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    candidateBusyId.value = null
  }
}

async function rejectCandidate(candidate: CalibrationBundleCandidate) {
  const rejectedBy = window.prompt('Reject as (your name)?')
  if (!rejectedBy?.trim()) return
  const reason = window.prompt('Reason for rejection?')
  if (!reason?.trim()) return
  candidateBusyId.value = candidate.id
  error.value = null
  try {
    const response = await fetch(`${apiBase()}api/calibration-candidates/${candidate.id}/reject`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rejectedBy: rejectedBy.trim(), reason: reason.trim() }),
    })
    if (!response.ok) throw new Error(await readApiError(response, 'Could not reject candidate'))
    notice.value = `Rejected candidate ${shortId(candidate.id)}.`
    await refreshCandidates()
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    candidateBusyId.value = null
  }
}

function candidateStatusClass(status: string): string {
  const value = status.toLowerCase()
  if (value === 'approved') return 'positive-text'
  if (value === 'rejected') return 'negative-text'
  if (value === 'pendingreview') return 'amber-text'
  return ''
}

async function refreshArtifacts() {
  const query = filterType.value === 'all' ? '' : `?type=${encodeURIComponent(filterType.value)}`
  const response = await fetch(`${apiBase()}api/calibrations${query}`)
  if (!response.ok) {
    error.value = await readApiError(response, 'Could not load calibration artifacts')
    return
  }
  artifacts.value = await response.json() as CalibrationArtifactMetadata[]
}

async function refreshJobs() {
  const response = await fetch(`${apiBase()}api/research/jobs?take=20`)
  if (!response.ok) return
  jobs.value = await response.json() as ResearchJobSnapshot[]
}

async function startCalibration() {
  busy.value = true
  error.value = null
  notice.value = null
  try {
    if (!form.from || !form.to) throw new Error('From and To dates are required.')
    const body = {
      kind: form.kind,
      instrument: form.instrument.trim(),
      strategy: form.strategy.trim(),
      from: new Date(`${form.from}T00:00:00Z`).toISOString(),
      to: new Date(`${form.to}T00:00:00Z`).toISOString(),
      startingBalance: form.startingBalance,
      quantity: form.quantity,
      sourceKind: form.sourceKind,
      warmupDays: form.warmupDays,
      description: form.description.trim() || null,
    }
    const response = await fetch(`${apiBase()}api/research/calibrations`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (!response.ok) {
      throw new Error(await readApiError(response, 'Calibration job could not be started'))
    }
    const job = await response.json() as ResearchJobSnapshot
    activeJobId.value = job.jobId
    notice.value = `Started ${job.kind} calibration job ${job.jobId}.`
    await refreshJobs()
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

async function deleteArtifact(artifact: CalibrationArtifactMetadata) {
  if (!confirm(`Delete calibration artifact ${artifact.id}?`)) return
  error.value = null
  const response = await fetch(`${apiBase()}api/calibrations/${artifact.id}`, { method: 'DELETE' })
  if (!response.ok && response.status !== 204) {
    error.value = await readApiError(response, 'Could not delete artifact')
    return
  }
  if (selection.setupCalibrationArtifactId === artifact.id) selection.setupCalibrationArtifactId = ''
  if (selection.managementCalibrationArtifactId === artifact.id) selection.managementCalibrationArtifactId = ''
  if (selection.metaModelArtifactId === artifact.id) selection.metaModelArtifactId = ''
  persistSelection()
  await refreshArtifacts()
}

async function copyId(id: string) {
  try {
    await navigator.clipboard.writeText(id)
    notice.value = `Copied ${id}`
  } catch {
    notice.value = id
  }
}

function formatTime(value: string | null | undefined): string {
  if (!value) return '—'
  return new Date(value).toLocaleString()
}

function shortId(id: string): string {
  return id.length > 12 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id
}

function statusClass(status: string): string {
  const value = status.toLowerCase()
  if (value === 'completed') return 'positive-text'
  if (value === 'failed') return 'negative-text'
  if (value === 'running' || value === 'queued') return 'amber-text'
  return ''
}
</script>

<template>
  <div class="research-panel">
    <header class="research-header">
      <div>
        <h2>Research &amp; calibration</h2>
        <p>
          Run setup, management, and meta-model calibrations through the real simulator,
          then attach artifact IDs to the next Simulator run. Same store as live promotion
          (<code>.cache/calibration-artifacts</code>).
        </p>
      </div>
      <div class="research-header-actions">
        <button type="button" class="button button-secondary" :disabled="loading" @click="refreshAll">
          Refresh
        </button>
      </div>
    </header>

    <p v-if="error" class="research-banner error">{{ error }}</p>
    <p v-if="notice" class="research-banner ok">{{ notice }}</p>

    <div class="research-grid">
      <section class="research-card" aria-label="Run calibration">
        <h3>Run calibration</h3>
        <p class="muted">
          Starts a full backtest on the selected window, then builds a versioned artifact.
          Management runs enable detailed excursion tracking automatically.
        </p>
        <div class="research-form">
          <label>
            Kind
            <select v-model="form.kind">
              <option value="setup">Setup confidence</option>
              <option value="metamodel">Meta-model</option>
              <option value="management">Trade management</option>
            </select>
          </label>
          <label>
            Instrument
            <input v-model.trim="form.instrument" type="text" spellcheck="false" />
          </label>
          <label>
            Strategy
            <select v-model="form.strategy">
              <option value="improved">improved</option>
              <option value="legacy">legacy</option>
            </select>
          </label>
          <label>
            From (UTC date)
            <input v-model="form.from" type="date" />
          </label>
          <label>
            To (UTC date)
            <input v-model="form.to" type="date" />
          </label>
          <label>
            Source
            <select v-model="form.sourceKind">
              <option value="OandaCandles">OandaCandles</option>
              <option value="BinanceCandles">BinanceCandles</option>
            </select>
          </label>
          <label>
            Warm-up days
            <input v-model.number="form.warmupDays" type="number" min="0" step="1" />
          </label>
          <label>
            Starting balance
            <input v-model.number="form.startingBalance" type="number" min="1" step="1000" />
          </label>
          <label>
            Quantity
            <input v-model.number="form.quantity" type="number" min="1" step="1" />
          </label>
          <label class="full">
            Description (optional)
            <input v-model.trim="form.description" type="text" placeholder="e.g. EURUSD improved Mar 2026" />
          </label>
        </div>
        <div class="research-actions">
          <button type="button" class="button" :disabled="busy" @click="startCalibration">
            {{ busy ? 'Starting…' : 'Start calibration' }}
          </button>
        </div>

        <div v-if="activeJob" class="research-job-status">
          <h4>Latest job</h4>
          <dl>
            <div><dt>Status</dt><dd :class="statusClass(activeJob.status)">{{ activeJob.status }}</dd></div>
            <div><dt>Kind</dt><dd>{{ activeJob.kind }}</dd></div>
            <div><dt>Window</dt><dd>{{ formatTime(activeJob.from) }} → {{ formatTime(activeJob.to) }}</dd></div>
            <div><dt>Trades</dt><dd>{{ activeJob.tradeCount }}</dd></div>
            <div><dt>Buckets/cohorts</dt><dd>{{ activeJob.bucketOrCohortCount }}</dd></div>
            <div><dt>Message</dt><dd>{{ activeJob.message ?? '—' }}</dd></div>
            <div v-if="activeJob.error"><dt>Error</dt><dd class="negative-text">{{ activeJob.error }}</dd></div>
            <div v-if="activeJob.artifactId">
              <dt>Artifact</dt>
              <dd>
                <code>{{ activeJob.artifactId }}</code>
                <button type="button" class="linkish" @click="copyId(activeJob.artifactId!)">Copy</button>
                <button type="button" class="linkish" @click="useJobArtifact(activeJob)">Use in simulator</button>
              </dd>
            </div>
          </dl>
        </div>
      </section>

      <section class="research-card" aria-label="Simulator attachment">
        <h3>Attach to simulator</h3>
        <p class="muted">
          Selection is stored in this browser and sent with the next Simulator run.
          Leave a field blank to skip that gate.
        </p>
        <div class="research-form stack">
          <label>
            Setup calibration artifact ID
            <input v-model.trim="selection.setupCalibrationArtifactId" type="text" spellcheck="false" placeholder="guid" />
          </label>
          <label>
            Management calibration artifact ID
            <input v-model.trim="selection.managementCalibrationArtifactId" type="text" spellcheck="false" placeholder="guid" />
          </label>
          <label>
            Meta-model artifact ID
            <input v-model.trim="selection.metaModelArtifactId" type="text" spellcheck="false" placeholder="guid" />
          </label>
        </div>
        <div class="research-actions">
          <button type="button" class="button" @click="persistSelection">Save for simulator</button>
          <button type="button" class="button button-secondary" @click="clearSelection">Clear</button>
        </div>
        <p class="muted selection-summary">Active selection: <strong>{{ selectionSummary }}</strong></p>
      </section>
    </div>

    <section class="research-card" aria-label="Stored artifacts">
      <div class="research-card-head">
        <h3>Stored artifacts</h3>
        <label class="inline-filter">
          Type
          <select v-model="filterType" @change="refreshArtifacts">
            <option value="all">All</option>
            <option value="Setup">Setup</option>
            <option value="Management">Management</option>
            <option value="MetaModel">Meta-model</option>
          </select>
        </label>
      </div>
      <div class="table-wrap">
        <table class="research-table">
          <thead>
            <tr>
              <th>Type</th>
              <th>ID</th>
              <th>Calibration</th>
              <th>Created</th>
              <th>Description</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="filteredArtifacts.length === 0">
              <td colspan="6" class="muted">No artifacts yet. Run a calibration above.</td>
            </tr>
            <tr v-for="artifact in filteredArtifacts" :key="artifact.id">
              <td><span class="type-chip">{{ artifact.type }}</span></td>
              <td>
                <code :title="artifact.id">{{ shortId(artifact.id) }}</code>
                <button type="button" class="linkish" @click="copyId(artifact.id)">Copy</button>
              </td>
              <td>{{ artifact.calibrationId }}</td>
              <td>{{ formatTime(artifact.createdAt) }}</td>
              <td>{{ artifact.description || '—' }}</td>
              <td class="row-actions">
                <button type="button" class="button button-secondary" @click="useArtifact(artifact)">Use</button>
                <button type="button" class="button button-secondary danger" @click="deleteArtifact(artifact)">Delete</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="research-card" aria-label="Calibration candidate review">
      <div class="research-card-head">
        <h3>Calibration candidate review</h3>
        <label class="inline-filter">
          Status
          <select v-model="candidateStatusFilter" @change="refreshCandidates">
            <option value="PendingReview">Pending review</option>
            <option value="all">All</option>
          </select>
        </label>
      </div>
      <p class="muted">
        Automated calibration pipeline proposals, awaiting a human approve/reject before they
        become a live-eligible policy profile. Nothing here is ever auto-approved.
      </p>
      <div class="table-wrap">
        <table class="research-table">
          <thead>
            <tr>
              <th>Status</th>
              <th>Strategy</th>
              <th>Proposed profile</th>
              <th>Artifacts (setup / meta / mgmt)</th>
              <th>Created</th>
              <th>Reviewed</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="candidates.length === 0">
              <td colspan="7" class="muted">No candidates for this filter.</td>
            </tr>
            <tr v-for="candidate in candidates" :key="candidate.id">
              <td :class="candidateStatusClass(candidate.status)">{{ candidate.status }}</td>
              <td>{{ candidate.proposedProfile.strategyId }}</td>
              <td><code :title="candidate.proposedProfile.profileId">{{ shortId(candidate.proposedProfile.profileId) }}</code> rev {{ candidate.proposedProfile.revision }}</td>
              <td class="mono">
                {{ shortId(candidate.setupArtifactId) }} / {{ shortId(candidate.metaModelArtifactId) }} / {{ shortId(candidate.managementArtifactId) }}
              </td>
              <td>{{ formatTime(candidate.createdAt) }}</td>
              <td>
                <template v-if="candidate.reviewedBy">
                  {{ candidate.reviewedBy }} · {{ formatTime(candidate.reviewedAt) }}
                  <template v-if="candidate.rejectionReason"><br /><span class="negative-text">{{ candidate.rejectionReason }}</span></template>
                </template>
                <span v-else class="muted">—</span>
              </td>
              <td class="row-actions">
                <template v-if="candidate.status === 'PendingReview'">
                  <button
                    type="button" class="button button-secondary"
                    :disabled="candidateBusyId === candidate.id"
                    @click="approveCandidate(candidate)"
                  >Approve</button>
                  <button
                    type="button" class="button button-secondary danger"
                    :disabled="candidateBusyId === candidate.id"
                    @click="rejectCandidate(candidate)"
                  >Reject</button>
                </template>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="research-card" aria-label="Live trading policy profiles">
      <h3>Live trading policy profiles</h3>
      <p class="muted">
        Every profile ever persisted via <code>ITradingPolicyProfileStore</code> - approved
        candidates above, and simulations promoted from the Simulator tab. Newest revision per
        strategy with status <strong>ApprovedForDemo</strong> is what live/shadow runtimes pick up.
      </p>
      <div class="table-wrap">
        <table class="research-table">
          <thead>
            <tr>
              <th>Strategy</th>
              <th>Revision</th>
              <th>Status</th>
              <th>Profile ID</th>
              <th>Calibration artifacts</th>
              <th>Created</th>
              <th>Description</th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="policyProfiles.length === 0">
              <td colspan="7" class="muted">No trading policy profiles persisted yet.</td>
            </tr>
            <tr v-for="profile in policyProfiles" :key="`${profile.profileId}:${profile.revision}`">
              <td>{{ profile.strategyId }}<small class="mono">{{ profile.strategyVersion }}</small></td>
              <td>{{ profile.revision }}</td>
              <td :class="{ 'positive-text': profile.status === 'ApprovedForDemo', 'amber-text': profile.status === 'Reviewed' }">{{ profile.status }}</td>
              <td><code :title="profile.profileId">{{ shortId(profile.profileId) }}</code></td>
              <td class="mono">
                {{ profile.setupCalibrationArtifactId ? shortId(profile.setupCalibrationArtifactId) : '—' }} /
                {{ profile.metaModelArtifactId ? shortId(profile.metaModelArtifactId) : '—' }} /
                {{ profile.managementCalibrationArtifactId ? shortId(profile.managementCalibrationArtifactId) : '—' }}
              </td>
              <td>{{ formatTime(profile.createdAt) }}</td>
              <td>{{ profile.description || '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="research-card" aria-label="Recent research jobs">
      <h3>Recent jobs</h3>
      <div class="table-wrap">
        <table class="research-table">
          <thead>
            <tr>
              <th>Status</th>
              <th>Kind</th>
              <th>Instrument</th>
              <th>Strategy</th>
              <th>Trades</th>
              <th>Artifact</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="jobs.length === 0">
              <td colspan="7" class="muted">No research jobs in this server session yet.</td>
            </tr>
            <tr v-for="job in jobs" :key="job.jobId">
              <td :class="statusClass(job.status)">{{ job.status }}</td>
              <td>{{ job.kind }}</td>
              <td>{{ job.instrument }}</td>
              <td>{{ job.strategy }}</td>
              <td>{{ job.tradeCount }}</td>
              <td>
                <template v-if="job.artifactId">
                  <code :title="job.artifactId">{{ shortId(job.artifactId) }}</code>
                  <button type="button" class="linkish" @click="useJobArtifact(job)">Use</button>
                </template>
                <span v-else class="muted">—</span>
              </td>
              <td>{{ formatTime(job.createdAt) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</template>
