import { onUnmounted, ref } from 'vue'
import * as signalR from '@microsoft/signalr'

export interface LiveConnectionsHealth {
  quoteStreamConnected: boolean
  lastQuoteStreamFaultAt: string | null
  restReachable: boolean
  leaseState: string
}

export interface LiveMarketStatus {
  instrument: string
  bid: number | null
  ask: number | null
  spread: number | null
  lastM1CloseAt: string | null
  ready: boolean
  regime: string | null
  state: string
}

export interface LiveAgentStatus {
  deploymentId: string
  strategyId: string
  instrument: string
  policyBundleId: string
  policyRevision: number
  analysisProfileHash: string
  mode: string
  lastStatus: string | null
  health: string
  lastEvaluatedEpoch: number
  lastSnapshotVersion: number
  lastEvaluatedAt: string | null
  evaluations: number
  timeouts: number
  averageEvaluationDurationMilliseconds: number
  p95EvaluationDurationMilliseconds: number
  mailboxDepth: number
  candidatesObserved: number
  candidatesBuy: number
  candidatesSell: number
  rejectedBySetupCalibration: number
  rejectedByMetaLabel: number
  rejectedByTradingCondition: number
  rejectedByDataQuality: number
  lastDecisionId: string | null
  lastCandidateId: string | null
  lastAction: string | null
  lastReferencePrice: number | null
  lastStopLossPrice: number | null
  lastTakeProfitPrice: number | null
  lastMetaLabelProbability: number | null
  lastMetaLabelRiskMultiplier: number | null
  lastError: string | null
  lastRejection: string | null
}

export interface LiveAnalysisProfileStatus {
  profileHash: string
  instrument: string
  requiredIntervals: string[]
  lastSnapshotVersion: number
  lastAvailableAt: string | null
  lastBuildDurationMilliseconds: number
  cacheHits: number
  cacheMisses: number
  failureCount: number
  dependentAgentCount: number
}

export interface LiveDecisionEpochStatus {
  epoch: number
  expectedAgents: number
  completedAgents: number
  failedAgents: number
  timedOutAgents: number
  missingAgents: number
  candidateCount: number
  durationMilliseconds: number
}

export interface LiveRuntimeStatus {
  brokerWritesEnabled: boolean
  automaticExecutionEnabled: boolean
  partialCloseEnabled: boolean
  dynamicStopReplacementEnabled: boolean
  canOpenNewEntries: boolean
  safetyState: string
  safetyMessage: string | null
  equity: number | null
  marginUsed: number | null
  pendingManualCandidates: number
  activeReservations: number
  orders: number
  positions: number
  reconciliationDifferences: number
  lastReconciledAt: string | null
}

export interface LiveEngineStatus {
  revision: number
  engineState: string
  connections: LiveConnectionsHealth
  markets: LiveMarketStatus[]
  agents: LiveAgentStatus[]
  analysisProfiles: LiveAnalysisProfileStatus[]
  lastDecisionEpoch: LiveDecisionEpochStatus | null
  runtime: LiveRuntimeStatus | null
  asOf: string
  message: string | null
}

export interface LiveManualCandidate {
  candidateId: string
  fingerprint: string
  state: string
  decisionTime: string
  expiresAt: string
  strategyId: string
  instrument: string
  action: string
  referencePrice: number | null
  stopLossPrice: number | null
  takeProfitPrice: number | null
  quantity: number
  estimatedStopRisk: number
  estimatedMargin: number
  portfolioScore: number
  metaProbability: number
  combinedRiskMultiplier: number
  reviewedBy: string | null
  reviewReason: string | null
}

export interface LiveOrder {
  clientOrderId: string
  brokerOrderId: string | null
  brokerTradeId: string | null
  strategyId: string
  instrument: string
  side: string
  requestedQuantity: number
  filledQuantity: number
  averageFillPrice: number | null
  protectiveStopPrice: number | null
  takeProfitPrice: number | null
  state: string
  updatedAt: string
  lastReason: string | null
}

export interface LivePosition {
  positionId: string
  brokerTradeId: string | null
  strategyId: string
  instrument: string
  side: string
  initialQuantity: number
  quantity: number
  averagePrice: number | null
  initialStopPrice: number | null
  protectiveStopPrice: number | null
  takeProfitPrice: number | null
  maximumFavourableExcursionR: number
  maximumAdverseExcursionR: number
  lastManagementAction: string | null
  lastManagementReason: string | null
  updatedAt: string
}

export interface LiveCapabilities {
  atomicStopOnFillImplemented: boolean
  explicitFullCloseImplemented: boolean
  partialCloseImplemented: boolean
  partialClosePracticeCertified: boolean
  partialCloseEnabled: boolean
  dynamicStopReplacementImplemented: boolean
  dynamicStopReplacementPracticeCertified: boolean
  dynamicStopReplacementEnabled: boolean
}

const API_BASE = `${import.meta.env.BASE_URL}api/live-host/`
const HUB_BASE = `${import.meta.env.BASE_URL}hubs/live`

export function useLiveEngineStatus() {
  const status = ref<LiveEngineStatus | null>(null)
  const candidates = ref<LiveManualCandidate[]>([])
  const orders = ref<LiveOrder[]>([])
  const positions = ref<LivePosition[]>([])
  const capabilities = ref<LiveCapabilities | null>(null)
  const connected = ref(false)
  const usingPolling = ref(false)
  const busy = ref(false)
  const error = ref<string | null>(null)

  let connection: signalR.HubConnection | null = null
  let pollTimer: number | undefined
  let lastRevision = -1
  let disposed = false

  function apply(payload: LiveEngineStatus) {
    if (disposed) return
    if (typeof payload.revision === 'number' && payload.revision < lastRevision) return
    lastRevision = payload.revision ?? lastRevision
    status.value = payload
  }

  async function readJson<T>(path: string): Promise<T> {
    const response = await fetch(`${API_BASE}${path}`)
    if (!response.ok) throw new Error(await readError(response))
    return await response.json() as T
  }

  async function sendJson<T>(
    path: string,
    body: unknown,
    controlToken: string,
  ): Promise<T> {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' }
    if (controlToken.trim()) headers['X-Live-Control-Token'] = controlToken.trim()
    const response = await fetch(`${API_BASE}${path}`, {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
    })
    if (!response.ok) throw new Error(await readError(response))
    return await response.json() as T
  }

  async function readError(response: Response): Promise<string> {
    const text = await response.text()
    if (!text) return `${response.status} ${response.statusText}`
    try {
      const parsed = JSON.parse(text) as { error?: string }
      return parsed.error ?? text
    } catch {
      return text
    }
  }

  async function refreshOnce() {
    const payload = await readJson<LiveEngineStatus>('status')
    apply(payload)
  }

  async function refreshDetails() {
    const [candidatePayload, orderPayload, positionPayload, capabilityPayload] = await Promise.all([
      readJson<LiveManualCandidate[]>('candidates'),
      readJson<LiveOrder[]>('orders'),
      readJson<LivePosition[]>('positions'),
      readJson<LiveCapabilities>('capabilities'),
    ])
    if (disposed) return
    candidates.value = candidatePayload
    orders.value = orderPayload
    positions.value = positionPayload
    capabilities.value = capabilityPayload
  }

  async function refreshAll() {
    try {
      await Promise.all([refreshOnce(), refreshDetails()])
      error.value = null
    } catch (err) {
      error.value = err instanceof Error ? err.message : String(err)
    }
  }

  async function command<T>(operation: () => Promise<T>): Promise<T> {
    busy.value = true
    error.value = null
    try {
      const result = await operation()
      await refreshAll()
      return result
    } catch (err) {
      error.value = err instanceof Error ? err.message : String(err)
      throw err
    } finally {
      busy.value = false
    }
  }

  const pause = (reason: string, token: string) => command(() =>
    sendJson<unknown>('control/pause', { reason }, token))
  const resume = (token: string) => command(() =>
    sendJson<unknown>('control/resume', {}, token))
  const reconcile = (token: string) => command(() =>
    sendJson<unknown>('control/reconcile', {}, token))
  const cancelPending = (token: string) => command(() =>
    sendJson<unknown>('control/cancel-pending', {}, token))
  const flatten = (reason: string, token: string) => command(() =>
    sendJson<unknown>('control/flatten', { reason }, token))
  const approveCandidate = (candidate: LiveManualCandidate, reviewer: string, token: string) => command(() =>
    sendJson<unknown>(`candidates/${encodeURIComponent(candidate.candidateId)}/approve`, {
      candidateFingerprint: candidate.fingerprint,
      approvedBy: reviewer,
    }, token))
  const rejectCandidate = (candidate: LiveManualCandidate, reviewer: string, reason: string, token: string) => command(() =>
    sendJson<unknown>(`candidates/${encodeURIComponent(candidate.candidateId)}/reject`, {
      reviewedBy: reviewer,
      reason,
    }, token))
  const closePosition = (position: LivePosition, reason: string, token: string) => command(() =>
    sendJson<unknown>(`positions/${encodeURIComponent(position.positionId)}/close`, {
      reason,
      expectedPositionUpdatedAt: position.updatedAt,
    }, token))
  const reducePosition = (position: LivePosition, quantity: number, reason: string, token: string) => command(() =>
    sendJson<unknown>(`positions/${encodeURIComponent(position.positionId)}/reduce`, {
      quantity,
      reason,
      expectedPositionUpdatedAt: position.updatedAt,
    }, token))
  const amendStop = (position: LivePosition, stopPrice: number, reason: string, token: string) => command(() =>
    sendJson<unknown>(`positions/${encodeURIComponent(position.positionId)}/stop`, {
      stopPrice,
      reason,
      expectedPositionUpdatedAt: position.updatedAt,
    }, token))

  function startPolling() {
    stopPolling()
    usingPolling.value = true
    pollTimer = window.setInterval(() => {
      void refreshAll()
    }, 2000)
  }

  function stopPolling() {
    if (pollTimer !== undefined) {
      window.clearInterval(pollTimer)
      pollTimer = undefined
    }
  }

  async function connect() {
    error.value = null
    usingPolling.value = false

    const hubUrl = `${window.location.origin}${HUB_BASE}`.replace(/([^:]\/)\/+/g, '$1')
    connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .build()

    connection.on('LiveStatusChanged', (payload: LiveEngineStatus) => {
      apply(payload)
      void refreshDetails()
    })
    connection.onreconnected(async () => {
      connected.value = true
      await refreshAll()
    })
    connection.onclose(() => {
      connected.value = false
      startPolling()
    })

    try {
      await connection.start()
      connected.value = true
      await refreshAll()
    } catch (err) {
      connected.value = false
      usingPolling.value = true
      error.value = err instanceof Error ? err.message : String(err)
      startPolling()
    }
  }

  async function disposeConnection() {
    stopPolling()
    if (connection) {
      try {
        await connection.stop()
      } catch {
        // Shutdown is best-effort; the independent live host keeps running.
      }
      connection = null
    }
    connected.value = false
  }

  void connect()

  onUnmounted(() => {
    disposed = true
    void disposeConnection()
  })

  return {
    status,
    candidates,
    orders,
    positions,
    capabilities,
    connected,
    usingPolling,
    busy,
    error,
    refreshAll,
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
  }
}
