import { computed, onBeforeUnmount, ref } from 'vue'
import type {
  ExperimentDraft,
  SimulationExperimentRequest,
  SimulationExperimentSnapshot,
  SimulationExperimentTimeline,
} from '../types/simulation-experiments'

function utcStart(value: string): Date {
  return new Date(`${value}T00:00:00.000Z`)
}

function iso(value: Date): string {
  return value.toISOString()
}

function subtractUtcMonths(value: Date, months: number): Date {
  const result = new Date(value)
  const day = result.getUTCDate()
  result.setUTCDate(1)
  result.setUTCMonth(result.getUTCMonth() - months)
  const finalDay = new Date(Date.UTC(result.getUTCFullYear(), result.getUTCMonth() + 1, 0)).getUTCDate()
  result.setUTCDate(Math.min(day, finalDay))
  return result
}

export function resolveExperimentTimeline(draft: ExperimentDraft['timeline']): SimulationExperimentTimeline {
  const evaluationFrom = utcStart(draft.evaluationFrom)
  let learningFrom: Date
  let learningTo: Date
  if (draft.mode === 'relative') {
    learningTo = new Date(evaluationFrom)
    learningTo.setUTCDate(learningTo.getUTCDate() - draft.embargoDays)
    learningFrom = subtractUtcMonths(learningTo, draft.learningMonths)
    learningFrom.setUTCDate(learningFrom.getUTCDate() - draft.learningDays)
  } else {
    learningFrom = utcStart(draft.learningFrom)
    learningTo = utcStart(draft.learningTo)
  }
  return {
    learningFrom: iso(learningFrom),
    learningTo: iso(learningTo),
    evaluationFrom: iso(evaluationFrom),
    evaluationTo: iso(utcStart(draft.evaluationTo)),
    trainingWarmupDays: draft.trainingWarmupDays,
    evaluationWarmupDays: draft.evaluationWarmupDays,
    embargoDays: draft.embargoDays,
  }
}

async function readProblem(response: Response): Promise<string> {
  const body = await response.json().catch(() => null) as { error?: string; detail?: string } | null
  return body?.error ?? body?.detail ?? `Request failed with HTTP ${response.status}.`
}

export function useSimulationExperiment() {
  const snapshot = ref<SimulationExperimentSnapshot | null>(null)
  const recent = ref<SimulationExperimentSnapshot[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)
  let pollHandle: number | undefined

  const isActive = computed(() => snapshot.value != null &&
    !['Completed', 'Failed', 'Cancelled'].includes(snapshot.value.state))

  function stopPolling(): void {
    if (pollHandle !== undefined) window.clearTimeout(pollHandle)
    pollHandle = undefined
  }

  async function load(id: string, scheduleNext = true): Promise<void> {
    try {
      const response = await fetch(`${import.meta.env.BASE_URL}api/simulation-experiments/${id}`, {
        cache: 'no-store',
      })
      if (!response.ok) throw new Error(await readProblem(response))
      snapshot.value = await response.json() as SimulationExperimentSnapshot
      error.value = null
      stopPolling()
      if (scheduleNext && isActive.value) {
        pollHandle = window.setTimeout(() => void load(id), 1500)
      }
    } catch (problem) {
      error.value = problem instanceof Error ? problem.message : String(problem)
      stopPolling()
      if (scheduleNext) pollHandle = window.setTimeout(() => void load(id), 4000)
    }
  }

  async function refreshRecent(): Promise<void> {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulation-experiments?take=20`, {
      cache: 'no-store',
    })
    if (!response.ok) throw new Error(await readProblem(response))
    recent.value = await response.json() as SimulationExperimentSnapshot[]
  }

  async function start(draft: ExperimentDraft): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const request: SimulationExperimentRequest = {
        name: draft.name.trim(),
        description: draft.description.trim() || undefined,
        timeline: resolveExperimentTimeline(draft.timeline),
        profiles: draft.profileReferences,
        parallelism: draft.parallelism,
        allowInsufficientWarmup: draft.allowInsufficientWarmup,
        availableDataFrom: draft.availableDataFrom
          ? iso(utcStart(draft.availableDataFrom))
          : undefined,
      }
      const response = await fetch(`${import.meta.env.BASE_URL}api/simulation-experiments`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      })
      if (!response.ok) throw new Error(await readProblem(response))
      const handle = await response.json() as { id: string }
      await load(handle.id)
      await refreshRecent()
    } catch (problem) {
      error.value = problem instanceof Error ? problem.message : String(problem)
      throw problem
    } finally {
      loading.value = false
    }
  }

  async function apply(action: 'pause' | 'resume' | 'cancel'): Promise<void> {
    if (!snapshot.value) return
    const id = snapshot.value.id
    const response = await fetch(
      `${import.meta.env.BASE_URL}api/simulation-experiments/${id}/${action}`,
      { method: 'POST' },
    )
    if (!response.ok) throw new Error(await readProblem(response))
    await load(id)
  }

  function select(item: SimulationExperimentSnapshot): void {
    snapshot.value = item
    void load(item.id)
  }

  onBeforeUnmount(stopPolling)
  return { snapshot, recent, loading, error, isActive, start, load, select, apply, refreshRecent }
}
