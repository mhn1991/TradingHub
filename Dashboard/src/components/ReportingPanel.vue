<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'

type ReportKind = 'simulation' | 'experiment' | 'live-session' | 'deployment' | 'candidate' | 'position' | 'agent'
type ReportValue = Record<string, unknown>

const props = withDefaults(defineProps<{
  initialKind?: ReportKind
  initialIdentifier?: string
  autoLoad?: boolean
}>(), {
  initialKind: 'live-session',
  initialIdentifier: '',
  autoLoad: false,
})

const kinds: Array<{ value: ReportKind; label: string; path: string; ranged: boolean }> = [
  { value: 'simulation', label: 'Simulation run', path: 'simulations', ranged: false },
  { value: 'experiment', label: 'Experiment', path: 'experiments', ranged: false },
  { value: 'live-session', label: 'Live session', path: 'live/sessions', ranged: false },
  { value: 'deployment', label: 'Deployment', path: 'deployments', ranged: true },
  { value: 'candidate', label: 'Candidate explanation', path: 'candidates', ranged: false },
  { value: 'position', label: 'Position explanation', path: 'positions', ranged: false },
  { value: 'agent', label: 'Agent operations', path: 'agents', ranged: true },
]

const sectionKeys: Array<{ title: string; keys: string[] }> = [
  { title: 'Run Summary', keys: ['activity', 'runs', 'comparisons', 'blobs'] },
  { title: 'Agent Funnel', keys: ['funnel', 'topReasons'] },
  { title: 'Setup / Playbook', keys: ['deterministicContextJson', 'setupCalibrationJson'] },
  { title: 'ML Calibration', keys: ['metaLabelJson'] },
  { title: 'Risk', keys: ['sizing', 'portfolio'] },
  { title: 'Execution / Management', keys: ['orders', 'position', 'positionEvents', 'managementActions', 'outcome'] },
  { title: 'Incidents / Data Quality', keys: ['incidents', 'timeline', 'stateChangesAndIncidents', 'completenessWarnings'] },
  { title: 'Explanation', keys: ['candidateExplanation'] },
]

const selectedKind = ref<ReportKind>(props.initialKind)
const identifier = ref(props.initialIdentifier)
const from = ref('')
const to = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const report = ref<ReportValue | null>(null)

const selectedDefinition = computed(() => kinds.find(item => item.value === selectedKind.value)!)
const topLevelScalars = computed(() => report.value
  ? Object.entries(report.value).filter(([, value]) => isScalar(value))
  : [])
const sections = computed(() => {
  if (!report.value) return []
  const claimed = new Set(sectionKeys.flatMap(section => section.keys))
  const result = sectionKeys.map(section => ({
    title: section.title,
    entries: section.keys
      .filter(key => report.value && key in report.value)
      .map(key => ({ key, value: report.value![key] })),
  }))
  const unclaimed = Object.entries(report.value)
    .filter(([key, value]) => !claimed.has(key) && !isScalar(value))
    .map(([key, value]) => ({ key, value }))
  if (unclaimed.length) result[0].entries.push(...unclaimed)
  if (selectedKind.value === 'candidate') {
    const explanation = Object.fromEntries(topLevelScalars.value.filter(([key]) =>
      ['decisionId', 'strategyId', 'setupId', 'direction', 'status', 'decisionTime', 'rawConfidence'].includes(key)))
    result.at(-1)!.entries.push({ key: 'decisionTrace', value: explanation })
  }
  return result
})

async function loadReport() {
  error.value = null
  report.value = null
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(identifier.value.trim())) {
    error.value = 'Enter a valid report UUID.'
    return
  }
  if (from.value && to.value && from.value > to.value) {
    error.value = 'From must be earlier than or equal to To.'
    return
  }
  busy.value = true
  try {
    const definition = selectedDefinition.value
    const query = new URLSearchParams()
    if (definition.ranged && from.value) query.set('from', new Date(from.value).toISOString())
    if (definition.ranged && to.value) query.set('to', new Date(to.value).toISOString())
    const suffix = query.size ? `?${query}` : ''
    const response = await fetch(`${import.meta.env.BASE_URL}api/reports/${definition.path}/${identifier.value.trim()}${suffix}`)
    if (response.status === 404) throw new Error('No report exists for that identifier.')
    if (!response.ok) {
      const body = await response.json().catch(() => ({})) as { error?: string; detail?: string; title?: string }
      throw new Error(body.error ?? body.detail ?? body.title ?? `Report request failed (HTTP ${response.status}).`)
    }
    report.value = await response.json() as ReportValue
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Report request failed.'
  } finally {
    busy.value = false
  }
}

function isScalar(value: unknown): boolean {
  return value == null || ['string', 'number', 'boolean'].includes(typeof value)
}

function label(value: string): string {
  return value.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/[_-]/g, ' ')
}

function display(value: unknown): string {
  if (value == null || value === '') return '—'
  if (typeof value === 'boolean') return value ? 'Yes' : 'No'
  if (typeof value === 'number') return value.toLocaleString()
  if (typeof value === 'string' && /^\d{4}-\d\d-\d\dT/.test(value)) return new Date(value).toLocaleString()
  return String(value)
}

function nestedScalars(value: unknown): Array<[string, unknown]> {
  if (!value || Array.isArray(value) || typeof value !== 'object') return []
  return Object.entries(value as ReportValue).filter(([, item]) => isScalar(item))
}

onMounted(() => {
  if (props.autoLoad && props.initialIdentifier) void loadReport()
})
</script>

<template>
  <main class="reporting-page">
    <header class="reporting-heading">
      <div><small>PostgreSQL read model</small><h1>Structured reporting</h1></div>
      <p>Operational summaries and drill-down explanations use normalized runtime records, not log parsing.</p>
    </header>

    <form class="report-query" @submit.prevent="loadReport">
      <label>Report
        <select v-model="selectedKind"><option v-for="kind in kinds" :key="kind.value" :value="kind.value">{{ kind.label }}</option></select>
      </label>
      <label class="id-field">Identifier
        <input v-model.trim="identifier" autocomplete="off" placeholder="UUID" />
      </label>
      <label v-if="selectedDefinition.ranged">From
        <input v-model="from" type="datetime-local" />
      </label>
      <label v-if="selectedDefinition.ranged">To
        <input v-model="to" type="datetime-local" />
      </label>
      <button class="report-button" type="submit" :disabled="busy">{{ busy ? 'Loading…' : 'Run report' }}</button>
    </form>

    <p v-if="error" class="report-error" role="alert">{{ error }}</p>
    <section v-if="!report && !error" class="report-empty">Choose a report type and enter its database identifier.</section>

    <template v-if="report">
      <section class="summary-grid" aria-label="Report summary">
        <article v-for="([key, value]) in topLevelScalars" :key="key"><small>{{ label(key) }}</small><strong>{{ display(value) }}</strong></article>
      </section>

      <section class="report-sections">
        <article v-for="section in sections" :key="section.title" class="report-section">
          <header><h2>{{ section.title }}</h2><span>{{ section.entries.length }}</span></header>
          <p v-if="section.entries.length === 0" class="muted">No records in this report.</p>
          <div v-for="entry in section.entries" :key="entry.key" class="report-entry">
            <h3>{{ label(entry.key) }}</h3>
            <div v-if="isScalar(entry.value)" class="single-value">{{ display(entry.value) }}</div>
            <dl v-else-if="!Array.isArray(entry.value)" class="detail-grid">
              <template v-for="([key, value]) in nestedScalars(entry.value)" :key="key"><dt>{{ label(key) }}</dt><dd>{{ display(value) }}</dd></template>
            </dl>
            <div v-else-if="entry.value.length === 0" class="muted">None</div>
            <div v-else class="table-scroll"><table><tbody>
              <tr v-for="(row, index) in entry.value" :key="index">
                <template v-if="isScalar(row)"><td>{{ display(row) }}</td></template>
                <template v-else><td v-for="([key, value]) in nestedScalars(row)" :key="key"><small>{{ label(key) }}</small>{{ display(value) }}</td></template>
              </tr>
            </tbody></table></div>
          </div>
        </article>
      </section>
    </template>
  </main>
</template>

<style scoped>
.reporting-page{padding:26px;min-height:calc(100vh - 76px);background:#071016;color:#e9f0f2}.reporting-heading{display:flex;justify-content:space-between;gap:28px;align-items:end;margin-bottom:20px}.reporting-heading h1{margin:3px 0;font-size:26px}.reporting-heading small,.reporting-heading p,.muted{color:#82949b}.reporting-heading p{max-width:570px;margin:0}.report-query{display:flex;align-items:end;gap:12px;padding:16px;background:#0d1a21;border:1px solid #1c3039;border-radius:10px}.report-query label{display:grid;gap:6px;color:#91a5ad;font-size:12px}.report-query input,.report-query select{height:38px;padding:0 10px;color:#e9f0f2;background:#071016;border:1px solid #28414b;border-radius:6px}.id-field{flex:1}.report-button{height:38px;padding:0 18px;border:0;border-radius:6px;background:#49d5aa;color:#04110d;font-weight:700;cursor:pointer}.report-button:disabled{opacity:.55}.report-error,.report-empty{margin-top:18px;padding:16px;border-radius:8px;background:#152128}.report-error{border:1px solid #713c42;color:#ffadb4}.summary-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;margin:18px 0}.summary-grid article{display:grid;gap:7px;padding:13px;background:#0d1a21;border:1px solid #1c3039;border-radius:8px}.summary-grid small{color:#7e9299;text-transform:capitalize}.summary-grid strong{overflow-wrap:anywhere}.report-sections{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.report-section{min-width:0;padding:15px;background:#0d1a21;border:1px solid #1c3039;border-radius:9px}.report-section>header{display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid #1c3039}.report-section h2{font-size:15px}.report-section>header span{color:#49d5aa}.report-entry{padding:11px 0;border-bottom:1px solid #17282f}.report-entry:last-child{border:0}.report-entry h3{margin:0 0 8px;color:#91a5ad;font-size:12px;text-transform:capitalize}.single-value{overflow-wrap:anywhere}.detail-grid{display:grid;grid-template-columns:minmax(130px,auto) 1fr;gap:6px 12px;margin:0}.detail-grid dt,table small{color:#789098;text-transform:capitalize}.detail-grid dd{margin:0;overflow-wrap:anywhere}.table-scroll{overflow:auto}table{width:100%;border-collapse:collapse}td{padding:8px;vertical-align:top;border-bottom:1px solid #1b2c34;white-space:nowrap}td small{display:block;margin-bottom:4px}@media(max-width:900px){.reporting-heading{display:block}.report-query{align-items:stretch;flex-direction:column}.report-sections{grid-template-columns:1fr}}
</style>
