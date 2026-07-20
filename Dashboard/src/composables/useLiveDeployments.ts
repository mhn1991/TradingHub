import { onUnmounted, ref } from 'vue'
import * as signalR from '@microsoft/signalr'
import type {
  AgentDescriptor,
  BrokerAccountOption,
  DeploymentDetail,
  DeploymentPreflightRequest,
  DeploymentPreflightResult,
  DeploymentSummary,
  InstrumentOption,
  LifecycleMutationContext,
  Page,
  PolicyRevisionSummary,
  PolicySummary,
} from '../types/liveDeployments'

const API_BASE = `${import.meta.env.BASE_URL}api/live-host/`
const HUB_URL = `${import.meta.env.BASE_URL}hubs/live`

export function useLiveDeployments() {
  const agentTypes = ref<AgentDescriptor[]>([])
  const policies = ref<PolicySummary[]>([])
  const revisions = ref<PolicyRevisionSummary[]>([])
  const accounts = ref<BrokerAccountOption[]>([])
  const instruments = ref<InstrumentOption[]>([])
  const deployments = ref<DeploymentDetail[]>([])
  const busy = ref(false)
  const error = ref<string | null>(null)
  const connected = ref(false)
  let disposed = false
  let pollTimer: number | undefined
  let connection: signalR.HubConnection | null = null

  async function request<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await fetch(`${API_BASE}${path}`, init)
    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`
      try {
        const body = await response.json() as { message?: string; code?: string }
        message = body.message ? `${body.code ? `${body.code}: ` : ''}${body.message}` : message
      } catch {
        // The status remains useful when a proxy or host returns a non-JSON response.
      }
      throw new Error(message)
    }
    return await response.json() as T
  }

  async function refreshLibrary() {
    try {
      const [types, policyPage, revisionPage, accountRows] = await Promise.all([
        request<AgentDescriptor[]>('agent-types'),
        request<Page<PolicySummary>>('policies?offset=0&limit=100'),
        request<Page<PolicyRevisionSummary>>('policy-revisions?offset=0&limit=250'),
        request<BrokerAccountOption[]>('broker-accounts'),
      ])
      agentTypes.value = types
      policies.value = policyPage.items
      revisions.value = revisionPage.items
      accounts.value = accountRows
      error.value = null
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
    }
  }

  async function loadInstruments(brokerAccountId: string) {
    instruments.value = brokerAccountId
      ? await request<InstrumentOption[]>(`broker-accounts/${brokerAccountId}/instruments`)
      : []
  }

  async function refreshDeployments() {
    try {
      const page = await request<Page<DeploymentSummary>>('deployments?offset=0&limit=100')
      deployments.value = await Promise.all(page.items.map(item =>
        request<DeploymentDetail>(`deployments/${item.deploymentId}`)))
      error.value = null
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
    }
  }

  async function mutate<T>(path: string, body: object, context: LifecycleMutationContext): Promise<T> {
    const actor = context.actor.trim()
    const reason = context.reason.trim()
    if (!actor || !reason) throw new Error('Operator identity and reason are required.')
    const idempotencyKey = crypto.randomUUID()
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      'Idempotency-Key': idempotencyKey,
    }
    if (context.controlToken) headers['X-Live-Control-Token'] = context.controlToken
    return await request<T>(path, {
      method: 'POST',
      headers,
      body: JSON.stringify({ ...body, actor, reason, idempotencyKey }),
    })
  }

  async function preflight(value: DeploymentPreflightRequest): Promise<DeploymentPreflightResult> {
    busy.value = true
    try {
      const result = await request<DeploymentPreflightResult>('deployments/preflight', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value),
      })
      error.value = null
      return result
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
      throw cause
    } finally {
      busy.value = false
    }
  }

  async function start(preflightRequest: DeploymentPreflightRequest, context: LifecycleMutationContext) {
    busy.value = true
    try {
      const value = await mutate('deployments', { preflight: preflightRequest }, context)
      await refreshDeployments()
      return value
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
      throw cause
    } finally {
      busy.value = false
    }
  }

  async function deploymentCommand(detail: DeploymentDetail, command: string,
      context: LifecycleMutationContext) {
    busy.value = true
    try {
      await mutate(`deployments/${detail.deployment.deploymentId}/${command}`,
        { expectedVersion: detail.deployment.version }, context)
      await refreshDeployments()
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
      throw cause
    } finally {
      busy.value = false
    }
  }

  async function agentCommand(detail: DeploymentDetail, deploymentAgentId: string, command: string,
      context: LifecycleMutationContext, replacementPolicyRevisionId?: string) {
    busy.value = true
    try {
      await mutate(`deployment-agents/${deploymentAgentId}/${command}`, {
        expectedVersion: detail.deployment.version,
        replacementPolicyRevisionId: replacementPolicyRevisionId || undefined,
      }, context)
      await refreshDeployments()
    } catch (cause) {
      error.value = cause instanceof Error ? cause.message : String(cause)
      throw cause
    } finally {
      busy.value = false
    }
  }

  function startPolling() {
    if (pollTimer !== undefined) return
    pollTimer = window.setInterval(() => void refreshDeployments(), 5000)
  }

  async function connect() {
    connection = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect()
      .build()
    connection.on('DeploymentChanged', () => void refreshDeployments())
    connection.onreconnecting(() => { connected.value = false; startPolling() })
    connection.onreconnected(() => { connected.value = true })
    connection.onclose(() => { connected.value = false; if (!disposed) startPolling() })
    try {
      await connection.start()
      connected.value = true
    } catch {
      startPolling()
    }
  }

  void Promise.all([refreshLibrary(), refreshDeployments()])
  void connect()

  onUnmounted(() => {
    disposed = true
    if (pollTimer !== undefined) window.clearInterval(pollTimer)
    if (connection) void connection.stop()
  })

  return {
    agentTypes, policies, revisions, accounts, instruments, deployments, busy, error, connected,
    refreshLibrary, refreshDeployments, loadInstruments, preflight, start,
    deploymentCommand, agentCommand,
  }
}
