import { onUnmounted, ref, watch, type Ref } from 'vue'
import * as signalR from '@microsoft/signalr'
import type { SimulationJobSnapshot } from '../types'

export function useSimulationRealtime(simulationId: Ref<string | null>) {
  const snapshot = ref<SimulationJobSnapshot | null>(null)
  const connected = ref(false)
  const usingPolling = ref(false)
  const error = ref<string | null>(null)

  let connection: signalR.HubConnection | null = null
  let pollTimer: number | undefined
  let lastRevision = -1
  let disposed = false

  async function connect(id: string) {
    await disposeConnection()
    lastRevision = -1
    error.value = null
    usingPolling.value = false

    const hubUrl = `${window.location.origin}${import.meta.env.BASE_URL}hubs/simulations`.replace(/([^:]\/)\/+/g, '$1')
    connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .build()

    const apply = (payload: SimulationJobSnapshot) => {
      if (disposed) return
      if (typeof payload.revision === 'number' && payload.revision < lastRevision) return
      lastRevision = payload.revision ?? lastRevision
      snapshot.value = payload
    }

    connection.on('SimulationStatusChanged', apply)
    connection.on('SimulationProgressChanged', apply)
    connection.on('SimulationCompleted', apply)
    connection.on('SimulationFailed', apply)

    connection.onreconnected(async () => {
      connected.value = true
      if (simulationId.value) {
        await connection?.invoke('Subscribe', simulationId.value)
        await refreshOnce(simulationId.value)
      }
    })

    try {
      await connection.start()
      connected.value = true
      await connection.invoke('Subscribe', id)
      await refreshOnce(id)
    } catch (err) {
      connected.value = false
      usingPolling.value = true
      error.value = err instanceof Error ? err.message : String(err)
      startPolling(id)
    }
  }

  async function refreshOnce(id: string) {
    const response = await fetch(`${import.meta.env.BASE_URL}api/simulations/${id}`)
    if (!response.ok) return
    const payload = await response.json() as SimulationJobSnapshot
    if (typeof payload.revision === 'number' && payload.revision < lastRevision) return
    lastRevision = payload.revision ?? lastRevision
    snapshot.value = payload
  }

  function startPolling(id: string) {
    stopPolling()
    usingPolling.value = true
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
    if (connection) {
      try {
        if (simulationId.value) {
          await connection.invoke('Unsubscribe', simulationId.value)
        }
      } catch {
        // ignore
      }
      try {
        await connection.stop()
      } catch {
        // ignore
      }
      connection = null
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
    refreshOnce,
  }
}
