<script setup>
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import ChannelTable from './components/ChannelTable.vue'
import NotificationPanel from './components/NotificationPanel.vue'
import LogPanel from './components/LogPanel.vue'
import SettingsPanel from './components/SettingsPanel.vue'
import StreamPlayerWindow from './components/StreamPlayerWindow.vue'
import { useCaster } from './composables/useCaster'
import { api } from './services/api'
import { toHlsUrl } from './services/endpoint'
import { isBusy } from './services/presentation'

const { snapshot, connected, connectionError, lastUpdated, refresh } = useCaster()
const channels = computed(() => snapshot.value?.channels ?? [])
const pending = ref(false)
const disabled = computed(() => pending.value || !connected.value)
const notice = ref(null)
const results = ref([])
let resultId = 0
function addResult(item) {
  results.value.unshift({ ...item, id: ++resultId, timestamp: new Date().toISOString() })
  results.value = results.value.slice(0, 200)
}
watch(notice, (value) => {
  if (value) addResult({ name: '작업 결과', text: value.text, error: !!value.error, kind: 'notice' })
}, { flush: 'sync' })
const fileInput = ref(null)
const settingsPanel = ref(null)
const uploading = ref(false)
const uploadCount = ref(0)
const uploadCompleted = ref(0)
const uploadCurrent = ref('')
let uploadController
const search = ref('')
const statusFilter = ref('all')
const dragDepth = ref(0)
const modal = ref(null)
const dialog = ref(null)
const players = ref([])
let playerId = 0
let playerZ = 100
const streamingCount = computed(() => channels.value.filter((c) => c.status === 'Streaming').length)
const errorCount = computed(() => channels.value.filter((c) => c.status === 'Error').length)
const visibleChannels = computed(() => channels.value.filter((c) =>
  `${c.name} ${c.video.fileName} ${c.rtspUrl}`.toLocaleLowerCase().includes(search.value.toLocaleLowerCase())
  && (statusFilter.value === 'all' || c.status === statusFilter.value)))

async function perform(action, rethrow = false) {
  if (pending.value) return
  pending.value = true
  notice.value = null
  try {
    await action()
    try { await refresh() } catch {
      notice.value = { error: true, text: '요청 처리는 끝났지만 상태 갱신에 실패했습니다. 재연결 후 결과를 확인하세요.' }
    }
  } catch (error) {
    notice.value = { error: true, text: error.name === 'AbortError'
      ? '업로드를 취소했습니다. 이미 등록된 채널은 유지됩니다.' : error.message }
    if (rethrow) throw error
  } finally {
    pending.value = false
  }
}

async function openModal(kind, data = {}) {
  modal.value = { kind, ...data }
  await nextTick()
  dialog.value?.showModal()
}

function closeModal() {
  dialog.value?.close()
  modal.value = null
}

async function copy(text) {
  try {
    if (!navigator.clipboard) throw new Error('Clipboard unavailable')
    await navigator.clipboard.writeText(text)
    notice.value = { text: '클립보드에 복사했습니다.' }
  } catch {
    await openModal('copy', { text })
  }
}

async function saveEndpoint(id, values) {
  const channel = channels.value.find((c) => c.id === id)
  if (disabled.value || !channel || isBusy(channel)) {
    throw new Error('연결 상태를 확인하고 채널을 중지한 후 다시 적용하세요.')
  }
  await perform(async () => {
    await api.endpoint(channel.id, values)
    notice.value = { text: `채널 #${id}: RTSP 주소를 변경했습니다.` }
  }, true)
}

function saveSettings(values) {
  perform(async () => {
    const saved = await api.settings(values)
    settingsPanel.value?.saved(saved)
    notice.value = { text: '설정을 저장했습니다. 기존 채널의 대상 주소는 채널 편집 또는 템플릿 적용으로 변경하세요.' }
  })
}

function startAll() {
  perform(async () => {
    const response = await api.startAll()
    response.forEach((item) => addResult({ name: `채널 #${item.channelId}`, error: !item.accepted || !!item.error, text: item.error || (item.accepted ? '시작 접수' : '시작 거부'), kind: 'batch' }))
    notice.value = { text: '전체 시작을 요청했습니다. 접수 이후 실제 송출 상태를 확인하세요.' }
  })
}

function channelAction(channel, action) {
  perform(async () => {
    await api[action](channel.id)
    notice.value = { text: `채널 #${channel.id}: ${action === 'start' ? '시작을 접수했습니다. 송출 상태를 확인하세요.' : '중지했습니다.'}` }
  })
}

function openPlayer(channel) {
  const offset = players.value.length * 28
  const width = Math.min(640, Math.max(320, window.innerWidth - 32))
  const height = Math.min(420, Math.max(220, window.innerHeight - 32))
  players.value.push({
    id: ++playerId,
    address: channel.rtspUrl,
    url: toHlsUrl(channel.rtspUrl),
    x: Math.min(40 + offset, Math.max(0, window.innerWidth - width)),
    y: Math.min(40 + offset, Math.max(0, window.innerHeight - height)),
    width,
    height,
    z: ++playerZ,
  })
}

function focusPlayer(player) {
  player.z = ++playerZ
}

function closePlayer(id) {
  players.value = players.value.filter((player) => player.id !== id)
}

function confirmAction() {
  const selected = modal.value
  perform(async () => {
    if (selected.kind === 'remove') {
      await api.remove(selected.channel.id)
      notice.value = { text: `채널 #${selected.channel.id}을 삭제했습니다. 원본 파일은 서버에 유지됩니다.` }
    } else if (selected.kind === 'template') {
      const result = await api.template(selected.template)
      notice.value = { text: `템플릿 적용 ${result.updated.length}개 · 작업 중인 채널 제외 ${result.skipped.length}개` }
    } else {
      await api.stopAll()
      notice.value = { text: '모든 채널을 중지했습니다.' }
    }
    closeModal()
  })
}

function upload(files) {
  if (disabled.value) return
  const selected = Array.from(files ?? [])
  if (!selected.length) return
  perform(async () => {
    uploading.value = true
    uploadCount.value = selected.length
    uploadCurrent.value = '서버 업로드 제한 확인 중'
    uploadCompleted.value = 0
    uploadController = new AbortController()
    try {
      const response = await api.upload(selected, uploadController.signal, {
        onProgress: ({ index, total, fileName }) => { uploadCurrent.value = `${index}/${total} · ${fileName}` },
        onResult: (item) => {
          uploadCompleted.value++
          addResult({ name: item.fileName, error: !item.channel || !!item.error, text: item.error || (item.channel ? `채널 #${item.channel.id} 등록 완료` : '등록 실패'), kind: 'upload' })
        },
      })
      notice.value = { text: `파일 처리 완료: 성공 ${response.filter((r) => r.channel).length}개 / 전체 ${response.length}개` }
    } finally {
      uploading.value = false
      uploadController = null
      // Cancellation/reset can leave completed registrations on the server.
      try { await refresh() } catch { /* Keep partial results; SSE will reconnect separately. */ }
    }
  })
}

function chooseFiles(event) {
  upload(event.target.files)
  event.target.value = ''
}

/** @param {DragEvent} event */
function dragEnter(event) {
  if (!disabled.value && Array.from(event.dataTransfer?.types ?? []).includes('Files')) dragDepth.value++
}

/** @param {DragEvent} event */
function dropFiles(event) {
  dragDepth.value = 0
  upload(event.dataTransfer?.files)
}

function help() {
  perform(async () => openModal('help', { help: await api.templateHelp() }))
}

onUnmounted(() => uploadController?.abort())
</script>

<template>
  <a class="skip-link" href="#channels-title">채널 목록으로 이동</a>
  <header class="app-header">
    <div class="header-top">
    <div class="brand"><strong>RTSP Caster</strong><span>미디어 송출 관리</span></div>
    <div class="server-status">
      <span class="connection-state" role="status"><span class="dot" :class="connected ? 'online' : 'offline'" aria-hidden="true"></span>{{ connected ? 'Backend 연결됨' : 'Backend 연결 대기' }}</span>
      <small v-if="lastUpdated">최근 수신 <time>{{ new Date(lastUpdated).toLocaleTimeString('ko-KR', { hour12: false }) }}</time></small>
      <NotificationPanel :entries="results" @clear="results = []" />
    </div>
    </div>
    <div class="workspace-heading">
      <div><h1>송출 제어</h1><p>파일을 채널로 등록하고 RTSP 송출 상태를 확인합니다.</p></div>
      <dl class="summary" aria-label="채널 현황">
        <div><dt>전체 채널</dt><dd>{{ channels.length }}</dd></div>
        <div :class="{ good: streamingCount > 0 }"><dt>송출 중</dt><dd>{{ streamingCount }}</dd></div>
        <div :class="{ bad: errorCount > 0 }"><dt>오류</dt><dd>{{ errorCount }}</dd></div>
      </dl>
    </div>

    <section class="command-bar" aria-label="송출 제어">
      <div class="fields">
        <input ref="fileInput" class="sr-only" type="file" tabindex="-1" multiple accept="video/*,.mkv,.avi,.ts,.mts,.m2ts,.mov,.webm,.flv" aria-label="업로드할 동영상 파일" @change="chooseFiles" />
        <button class="primary" :disabled="disabled" @click="fileInput.click()">파일 추가</button>
        <div class="bulk-actions">
          <button class="start-button" :disabled="disabled || !channels.some((c) => !isBusy(c))" @click="startAll">전체 시작</button>
          <button :disabled="disabled || !channels.some(isBusy)" @click="openModal('stopAll')">전체 중지</button>
        </div>
      </div>
      <span class="hint">{{ connected ? '실시간 상태 수신 중' : snapshot ? '연결 끊김 · 마지막 수신 상태 표시' : '서버 연결 후 채널을 추가할 수 있습니다' }}</span>
    </section>
  </header>

  <main>
    <div v-if="connectionError" class="banner warning" role="status">{{ connectionError }}</div>
    <p class="sr-only" role="status" aria-live="polite">{{ notice?.text }}</p>
    <div v-if="uploading" class="upload-status" role="status"><span class="spinner" aria-hidden="true"></span><span>처리 완료 {{ uploadCompleted }}/{{ uploadCount }}개 · {{ uploadCurrent }}<br />파일을 한 개씩 업로드·검사합니다.</span><button @click="uploadController?.abort()">업로드 취소</button></div>
    <p v-else-if="pending" class="hint" role="status">요청을 처리하고 있습니다…</p>

    <section class="channels-panel" :class="{ 'drop-active': dragDepth > 0 && !disabled }" :aria-busy="pending" aria-labelledby="channels-title" @dragenter.prevent="dragEnter" @dragleave.prevent="dragDepth = Math.max(0, dragDepth - 1)" @dragover.prevent @drop.prevent="dropFiles">
      <div class="section-heading"><h2 id="channels-title" tabindex="-1">채널 목록 <span class="count">{{ visibleChannels.length }} / {{ channels.length }}</span></h2><div class="fields channel-filters"><input v-model="search" type="search" aria-label="채널 검색" placeholder="채널 · 파일 · URL 검색" /><select v-model="statusFilter" aria-label="채널 상태 필터"><option value="all">모든 상태</option><option value="Streaming">송출 중</option><option value="Idle">대기</option><option value="Probing">검사 중</option><option value="Converting">변환 중</option><option value="Ready">송출 준비</option><option value="Stopping">중지 중</option><option value="Error">오류</option></select></div></div>
      <div v-if="channels.length && !visibleChannels.length" class="empty-state" role="status"><h3>검색 조건에 맞는 채널이 없습니다.</h3><p>다른 검색어를 입력하거나 상태 필터를 변경하세요.</p><button @click="search = ''; statusFilter = 'all'">필터 초기화</button></div>
      <ChannelTable v-else :channels="visibleChannels" :disabled="disabled" :loaded="!!snapshot" :save-endpoint="saveEndpoint" @start="channelAction($event, 'start')" @stop="channelAction($event, 'stop')" @remove="openModal('remove', { channel: $event })" @copy="copy" @play="openPlayer" @add="fileInput.click()" />
      <div class="table-footer"><span>{{ dragDepth > 0 && !disabled ? '파일을 놓으면 업로드와 검사를 시작합니다.' : '동영상 파일을 이 영역에 놓아 채널 추가' }}</span><span>송출 중인 채널은 재생 버튼으로 HLS 영상을 확인할 수 있습니다.</span></div>
    </section>

    <SettingsPanel v-if="snapshot" ref="settingsPanel" :settings="snapshot.settings" :media-mtx="snapshot.mediaMtx" :disabled="disabled" @save="saveSettings" @check="perform(async () => { await api.check(); notice = { text: '저장된 MediaMTX 대상의 연결을 확인했습니다.' } })" @template="openModal('template', { template: $event })" @help="help" />

    <LogPanel :logs="snapshot?.logs.entries ?? []" :channels="channels" @copy="copy" />
    <footer class="page-footer">RTSP Caster · 웹 제어판 <span>인증 없는 관리 API입니다. 신뢰할 수 있는 네트워크에서만 사용하세요.</span></footer>
  </main>

  <StreamPlayerWindow v-for="player in players" :key="player.id" :player="player" @focus="focusPlayer(player)" @close="closePlayer(player.id)" />

  <dialog v-if="modal" ref="dialog" aria-labelledby="dialog-title" @close="modal = null" @cancel="pending && $event.preventDefault()">
    <template v-if="modal.kind === 'copy'">
      <h2 id="dialog-title">직접 복사</h2><p>클립보드 접근이 제한되어 있습니다. 아래 텍스트를 선택해 복사하세요.</p>
      <textarea :value="modal.text" readonly rows="7" aria-label="복사할 텍스트" @focus="$event.target.select()"></textarea>
      <div class="dialog-actions"><button @click="closeModal">닫기</button></div>
    </template>
    <template v-else-if="modal.kind === 'help'">
      <h2 id="dialog-title">RTSP 템플릿 도움말</h2><p class="mono">{{ modal.help.placeholders.join(' · ') }}</p>
      <ul><li v-for="note in modal.help.notes" :key="note">{{ note }}</li></ul><pre>{{ modal.help.examples.join('\n') }}</pre>
      <div class="dialog-actions"><button @click="closeModal">닫기</button></div>
    </template>
    <template v-else>
      <h2 id="dialog-title">{{ modal.kind === 'remove' ? '채널 삭제' : modal.kind === 'template' ? '템플릿 전체 적용' : '전체 송출 중지' }}</h2>
      <p v-if="modal.kind === 'remove'">#{{ modal.channel.id }} {{ modal.channel.name }} 채널을 삭제하시겠습니까? 진행 중인 작업을 중지하고 등록을 삭제합니다. 원본·변환 파일은 유지됩니다.</p>
      <template v-else-if="modal.kind === 'template'"><p>작업 중이 아닌 모든 채널의 주소를 변경합니다.</p><pre>{{ modal.template }}</pre></template>
      <p v-else>모든 채널의 준비·변환·송출 작업을 중지하시겠습니까?</p>
      <p v-if="notice?.error" class="bad" role="alert">{{ notice.text }}</p>
      <div class="dialog-actions"><button :disabled="pending" @click="closeModal">취소</button><button class="primary" :class="{ 'danger-button': modal.kind === 'remove' || modal.kind === 'stopAll' }" :disabled="disabled" @click="confirmAction">확인</button></div>
    </template>
  </dialog>
</template>
