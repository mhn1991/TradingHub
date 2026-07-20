import { ref } from 'vue'
import type {
  ExperimentCalibrationMode,
  SimulationStrategyProfile,
  StructuralConfluenceOptions,
} from '../types/simulation-experiments'

interface StructuralProfileDraft {
  name: string
  instrument: string
  calibrationMode: ExperimentCalibrationMode
}

interface StructuralVariantPatch {
  name: string
  playbook: 'sweep' | 'pullback' | 'break-retest'
  cciMode: 'Disabled' | 'Soft' | 'Required'
  supplyDemandConfluence: 'Disabled' | 'Preferred' | 'Required'
}

function newId(): string {
  return crypto.randomUUID()
}

function cloneJson<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

function enableAnalysisModule(
  profile: SimulationStrategyProfile,
  module: 'supplyDemand' | 'liquidity',
): void {
  const current = profile.analysis[module]
  profile.analysis[module] = {
    ...(typeof current === 'object' && current !== null ? current : {}),
    enabled: true,
  }
}

async function readProblem(response: Response): Promise<string> {
  const body = await response.json().catch(() => null) as { error?: string; detail?: string } | null
  return body?.error ?? body?.detail ?? `Request failed with HTTP ${response.status}.`
}

export function useSimulationProfiles() {
  const profiles = ref<SimulationStrategyProfile[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)

  async function refresh(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const response = await fetch(`${import.meta.env.BASE_URL}api/simulation-profiles`)
      if (!response.ok) throw new Error(await readProblem(response))
      profiles.value = await response.json() as SimulationStrategyProfile[]
    } catch (problem) {
      error.value = problem instanceof Error ? problem.message : String(problem)
    } finally {
      loading.value = false
    }
  }

  async function createStructural(draft: StructuralProfileDraft): Promise<SimulationStrategyProfile> {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulation-profiles`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        profileId: newId(),
        revision: 1,
        name: draft.name.trim(),
        agent: { kind: 'StructuralConfluence', structuralConfluence: {} },
        instrument: { value: draft.instrument.trim() },
        analysis: {
          supplyDemand: { enabled: true },
          liquidity: { enabled: true },
        },
        runtime: { options: {} },
        management: {},
        calibration: {
          mode: draft.calibrationMode,
          internalFolds: 5,
          internalEmbargoHours: 24,
          artifactIds: [],
        },
        tags: ['structural-confluence'],
        contentHash: '',
      }),
    })
    if (!response.ok) throw new Error(await readProblem(response))
    const created = await response.json() as SimulationStrategyProfile
    await refresh()
    return created
  }

  async function createVariant(
    source: SimulationStrategyProfile,
    patch: StructuralVariantPatch,
  ): Promise<SimulationStrategyProfile> {
    if (!source.agent.structuralConfluence) throw new Error('Variants require a structural-confluence profile.')

    const options = cloneJson(source.agent.structuralConfluence) as StructuralConfluenceOptions
    const playbooks = {
      sweep: 'liquiditySweepReversal',
      pullback: 'supplyDemandPullback',
      'break-retest': 'liquidityBreakRetest',
    } as const
    const selected = playbooks[patch.playbook]
    for (const key of Object.values(playbooks)) {
      options[key] = { ...options[key], enabled: key === selected }
    }
    options[selected] = {
      ...options[selected],
      enabled: true,
      cciMode: patch.cciMode,
      ...(selected === 'liquiditySweepReversal'
        ? { supplyDemandConfluence: patch.supplyDemandConfluence }
        : {}),
    }

    const variant = cloneJson(source)
    variant.profileId = newId()
    variant.revision = 1
    variant.name = patch.name.trim()
    variant.contentHash = ''
    variant.agent.structuralConfluence = options
    variant.tags = [...new Set([...variant.tags, 'variant', patch.playbook, `cci-${patch.cciMode.toLowerCase()}`])]
    if (patch.playbook === 'pullback') enableAnalysisModule(variant, 'supplyDemand')
    if (patch.playbook === 'sweep' || patch.playbook === 'break-retest') {
      enableAnalysisModule(variant, 'liquidity')
    }
    if (patch.playbook === 'sweep' && patch.supplyDemandConfluence !== 'Disabled') {
      enableAnalysisModule(variant, 'supplyDemand')
    }

    const response = await fetch(`${import.meta.env.BASE_URL}api/simulation-profiles`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(variant),
    })
    if (!response.ok) throw new Error(await readProblem(response))
    const created = await response.json() as SimulationStrategyProfile
    await refresh()
    return created
  }

  async function clone(source: SimulationStrategyProfile, name: string): Promise<SimulationStrategyProfile> {
    const response = await fetch(
      `${import.meta.env.BASE_URL}api/simulation-profiles/${source.profileId}/clone`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sourceRevision: source.revision, name: name.trim() }),
      },
    )
    if (!response.ok) throw new Error(await readProblem(response))
    const created = await response.json() as SimulationStrategyProfile
    await refresh()
    return created
  }

  async function reviseCalibration(
    source: SimulationStrategyProfile,
    policy: SimulationStrategyProfile['calibration'],
  ): Promise<SimulationStrategyProfile> {
    const revision = cloneJson(source)
    revision.calibration = policy
    revision.contentHash = ''
    const response = await fetch(
      `${import.meta.env.BASE_URL}api/simulation-profiles/${source.profileId}/revisions`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(revision),
      },
    )
    if (!response.ok) throw new Error(await readProblem(response))
    const created = await response.json() as SimulationStrategyProfile
    await refresh()
    return created
  }

  async function archive(profileId: string): Promise<void> {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulation-profiles/${profileId}`, {
      method: 'DELETE',
    })
    if (!response.ok) throw new Error(await readProblem(response))
    await refresh()
  }

  return { profiles, loading, error, refresh, createStructural, createVariant, clone, reviseCalibration, archive }
}
