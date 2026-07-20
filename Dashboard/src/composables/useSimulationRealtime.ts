import { onUnmounted, ref, watch, type Ref } from 'vue'
import * as signalR from '@microsoft/signalr'
import type { ReplayTrade, SimulationJobSnapshot } from '../types'

export interface CompletedTradeEnvelope {
  simulationId: string
  strategyId: string
  trade: ReplayTrade
}

export function useSimulationRealtime(simulationId: Ref<string | null>) {
  const snapshot = ref<SimulationJobSnapshot | null>(null)
  const connected = ref(false)
  const usingPolling = ref(false)
  const error = ref<string | null>(null)
  const completedTradeRevision = ref(0)
  const completedTrade = ref<CompletedTradeEnvelope | null>(null)

  let connection: signalR.HubConnection | null = null
  let pollTimer: number | undefined
  let subscribedId: string | null = null
  let lastRevision = -1
  let disposed = false

  async function connect(id: string) {
    await disposeConnection()
    lastRevision = -1
    error.value = null
    usingPolling.value = false

    const hubUrl = `${window.location.origin}${import.meta.env.BASE_URL}hubs/simulations`.replace(/([^:]\/)\/+/g, '$1')
    const nextConnection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .build()
    connection = nextConnection

    const apply = (payload: SimulationJobSnapshot) => {
      if (disposed) return
      if (typeof payload.revision === 'number' && payload.revision < lastRevision) return
      lastRevision = payload.revision ?? lastRevision
      snapshot.value = payload
    }

    nextConnection.on('SimulationStatusChanged', apply)
    nextConnection.on('SimulationProgressChanged', apply)
    nextConnection.on('SimulationCompleted', apply)
    nextConnection.on('SimulationFailed', apply)
    nextConnection.on('TradeCompleted', (payload: CompletedTradeEnvelope) => {
      completedTrade.value = payload
      completedTradeRevision.value++
    })

    nextConnection.onreconnecting(() => {
      if (connection !== nextConnection) return
      connected.value = false
      usingPolling.value = true
    })

    nextConnection.onreconnected(async () => {
      if (connection !== nextConnection) return
      connected.value = true
      usingPolling.value = false
      subscribedId = id
      await nextConnection.invoke('Subscribe', id)
      await refreshOnce(id)
    })

    nextConnection.onclose(() => {
      if (connection !== nextConnection || disposed) return
      connected.value = false
      usingPolling.value = true
      startPolling(id, true)
    })

    try {
      await nextConnection.start()
      if (connection !== nextConnection || disposed || simulationId.value !== id) {
        await nextConnection.stop()
        return
      }
      connected.value = true
      subscribedId = id
      await nextConnection.invoke('Subscribe', id)
      await refreshOnce(id)
      // SignalR progress events are fast, while this low-frequency reconciliation
      // prevents a dropped event or proxy timeout from freezing the panel.
      startPolling(id, false)
    } catch (err) {
      if (connection !== nextConnection) return
      connected.value = false
      usingPolling.value = true
      error.value = err instanceof Error ? err.message : String(err)
      startPolling(id, true)
    }
  }

  async function refreshOnce(id: string) {
    if (disposed || simulationId.value !== id) return
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}`, {
      cache: 'no-store',
    })
    if (!response.ok) return
    const payload = await response.json() as SimulationJobSnapshot
    if (disposed || simulationId.value !== id) return
    if (typeof payload.revision === 'number' && payload.revision < lastRevision) return
    lastRevision = payload.revision ?? lastRevision
    snapshot.value = payload
    error.value = null
  }

  function startPolling(id: string, fallback: boolean) {
    stopPolling()
    usingPolling.value = fallback
    pollTimer = window.setInterval(() => {
      void refreshOnce(id)
    }, 1500)
  }

  function stopPolling() {
    if (pollTimer !== undefined) {
      window.clearInterval(pollTimer)
      pollTimer = undefined
    }
  }

  async function disposeConnection() {
    stopPolling()
    const currentConnection = connection
    const currentSubscription = subscribedId
    connection = null
    subscribedId = null
    if (currentConnection) {
      try {
        if (currentSubscription) {
          await currentConnection.invoke('Unsubscribe', currentSubscription)
        }
      } catch {
        // ignore
      }
      try {
        await currentConnection.stop()
      } catch {
        // ignore
      }
    }
    connected.value = false
  }

  watch(simulationId, (id) => {
    if (!id) {
      void disposeConnection()
      snapshot.value = null
      return
    }
    void connect(id)
  }, { immediate: true })

  onUnmounted(() => {
    disposed = true
    void disposeConnection()
  })

  return {
    snapshot,
    connected,
    usingPolling,
    error,
    completedTradeRevision,
    completedTrade,
    refreshOnce,
  }
}
