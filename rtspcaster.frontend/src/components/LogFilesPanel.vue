<script setup>
import { computed, onUnmounted, ref } from 'vue'
import { api } from '../services/api'
import { formatBytes } from '../services/presentation'

const emit = defineEmits(['copy'])
const files = ref([])
const listOffset = ref(0)
const hasMoreFiles = ref(false)
const selected = ref('')
const page = ref(null)
const query = ref('')
const loading = ref(false)
const loaded = ref(false)
const error = ref('')
let controller
const displayed = computed(() => {
  const text = page.value?.content ?? ''
  if (!query.value) return text
  return text.split('\n').filter(line => line.toLocaleLowerCase().includes(query.value.toLocaleLowerCase())).join('\n')
})
const time = value => new Date(value).toLocaleString('ko-KR', { hour12: false })

async function request(fetchData, apply) {
  controller?.abort()
  const current = new AbortController()
  controller = current
  loading.value = true
  error.value = ''
  try {
    const result = await fetchData(current.signal)
    current.signal.throwIfAborted()
    apply(result)
  } catch (exception) {
    if (!current.signal.aborted) error.value = exception.message
  } finally {
    if (controller === current) loading.value = false
  }
}

function loadFiles(offset = listOffset.value) {
  return request(signal => api.logFiles(offset, signal), result => {
    files.value = result.files
    listOffset.value = offset
    hasMoreFiles.value = result.hasMore
    loaded.value = true
    if (!files.value.some(file => file.name === selected.value)) {
      selected.value = ''
      page.value = null
    }
  })
}

function read(offset = null) {
  if (!selected.value) { page.value = null; return }
  const name = selected.value
  page.value = null
  return request(signal => api.logFileContent(name, offset, signal), result => { page.value = result })
}

function toggle(event) {
  if (event.target.open) loadFiles()
  else {
    controller?.abort()
    loading.value = false
  }
}

onUnmounted(() => controller?.abort())
</script>

<template>
  <details class="log-files-panel" @toggle="toggle">
    <summary><strong>저장된 .log 파일</strong><span class="hint">웹 백엔드 파일만 조회</span></summary>
    <div class="log-files-body" :aria-busy="loading">
      <div class="file-controls">
        <label class="grow">로그 파일
          <select v-model="selected" :disabled="loading || !files.length" aria-label="저장된 로그 파일 선택" @change="query = ''; read()">
            <option value="">파일을 선택하세요</option>
            <option v-for="file in files" :key="file.name" :value="file.name">{{ file.name }} · {{ formatBytes(file.size) }} · {{ time(file.lastWriteTimeUtc) }}</option>
          </select>
        </label>
        <button :disabled="loading" @click="loadFiles()">파일 목록 새로고침</button>
        <button :disabled="loading || listOffset === 0" @click="loadFiles(Math.max(0, listOffset - 100))">이전 파일 목록</button>
        <button :disabled="loading || !hasMoreFiles" @click="loadFiles(listOffset + 100)">다음 파일 목록</button>
      </div>
      <p v-if="loading" class="hint" role="status">로그 파일을 읽고 있습니다…</p>
      <p v-if="error" class="bad" role="alert">{{ error }}</p>
      <p v-if="loaded && !files.length && !loading" class="hint">저장된 .log 파일이 없습니다. 파일 기록을 켜고 송출하면 생성됩니다.</p>
      <template v-if="selected">
        <div class="file-controls">
          <button :disabled="loading" @click="read(0)">처음부터</button>
          <button :disabled="loading || !page?.hasMore" @click="read(page.nextOffset)">다음 구간</button>
          <button :disabled="loading" @click="read()">최신 내용</button>
          <input v-model="query" type="search" aria-label="현재 파일 구간 검색" placeholder="현재 구간에서 검색" />
          <button :disabled="loading || !displayed" @click="emit('copy', displayed)">파일 표시 내용 복사</button>
        </div>
        <template v-if="page">
          <p class="hint file-metadata">{{ page.name }} · {{ formatBytes(page.size) }} · 수정 {{ time(page.lastWriteTimeUtc) }} · {{ page.offset.toLocaleString() }}–{{ page.nextOffset.toLocaleString() }} 바이트</p>
          <pre class="log-output file-output" tabindex="0" role="region" aria-label="저장된 로그 파일 내용">{{ displayed || (query ? '현재 구간에 검색 결과가 없습니다.' : '이 구간에 기록된 내용이 없습니다.') }}</pre>
        </template>
      </template>
      <p class="hint">파일 기록을 꺼도 기존 파일은 조회할 수 있습니다. 한 번에 최대 64KiB씩 표시하며 구간 경계에서 로그 한 줄이 나뉠 수 있습니다. 최신 내용은 수동으로 갱신합니다.</p>
    </div>
  </details>
</template>
