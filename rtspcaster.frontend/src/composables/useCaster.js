import { onMounted, onUnmounted, ref, shallowRef } from 'vue'
import { api } from '../services/api'

export function useCaster() {
  const snapshot = shallowRef(null)
  const connected = ref(false)
  const connectionError = ref('Backend에 연결하는 중입니다.')
  const lastUpdated = ref(null)
  let events
  let watchdog
  let disposed = false
  const initialRequest = new AbortController()

  function accept(data) {
    if (disposed) return
    if (!data || !Array.isArray(data.channels) || !data.settings || !data.logs) {
      throw new Error('Backend 상태 응답 형식이 올바르지 않습니다.')
    }
    snapshot.value = data
    connected.value = true
    connectionError.value = ''
    lastUpdated.value = Date.now()
  }

  async function refresh() {
    // An SSE frame received during this request is newer than its HTTP snapshot.
    const previousUpdate = lastUpdated.value
    const data = await api.status(initialRequest.signal)
    if (lastUpdated.value === previousUpdate) accept(data)
  }

  function connectEvents() {
    events?.close()
    if (disposed) return
    events = new EventSource('/api/events')
    events.addEventListener('snapshot', (event) => {
      try { accept(JSON.parse(event.data)) } catch {
        connected.value = false
        connectionError.value = '상태 데이터를 읽을 수 없습니다. Backend 버전을 확인하세요.'
      }
    })
    events.onerror = () => {
      connected.value = false
      connectionError.value = '실시간 연결이 끊겼습니다. 자동 재연결 중입니다. 표시된 상태는 마지막 수신 값입니다.'
    }
  }

  onMounted(async () => {
    watchdog = setInterval(() => {
      if (connected.value && Date.now() - lastUpdated.value > 15000) {
        connected.value = false
        connectionError.value = '상태 수신이 지연되고 있습니다. 다시 연결하는 중입니다.'
        connectEvents()
      }
    }, 5000)
    try { await refresh() } catch (error) {
      if (!disposed) connectionError.value = error.message
    } finally {
      connectEvents()
    }
  })

  onUnmounted(() => {
    disposed = true
    initialRequest.abort()
    events?.close()
    clearInterval(watchdog)
  })

  return { snapshot, connected, connectionError, lastUpdated, refresh }
}
