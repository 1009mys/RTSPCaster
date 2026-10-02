<script setup>
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import InlineEndpoint from './InlineEndpoint.vue'
import { formatBytes, isBusy, percent, sparkPoints, statusLabels } from '../services/presentation'

const props = defineProps({ channels: { type: Array, required: true }, disabled: Boolean, loaded: Boolean, saveEndpoint: { type: Function, required: true } })
defineEmits(['start', 'stop', 'remove', 'copy', 'play', 'add'])
const number = (value, digits = 1) => Number.isFinite(value) ? value.toFixed(digits) : '—'
const latest = (channel) => channel.healthSamples.at(-1)
const columns = [
  { label: '채널', percent: 8, min: 80 },
  { label: '송출 상태', percent: 7, min: 88 },
  { label: '소스 파일', percent: 16, min: 150 },
  { label: '미디어 / 호환성', percent: 18, min: 180 },
  { label: 'RTSP URL', percent: 17, min: 150 },
  { label: '송출 품질', percent: 22, min: 120 },
  { label: '제어', percent: 12, min: 160 },
]
const scroll = ref(null)
const table = ref(null)
const columnWidths = ref([])
const resizing = shallowRef(null)
const tableStyle = computed(() => {
  if (!columnWidths.value.length) return undefined
  const width = `${columnWidths.value.reduce((sum, value) => sum + value, 0)}px`
  return { width, minWidth: width }
})

function measureWidths() {
  if (!columnWidths.value.length && table.value) {
    columnWidths.value = Array.from(table.value.querySelectorAll('thead th'), (cell) => cell.getBoundingClientRect().width)
  }
}

function setWidth(index, width) {
  columnWidths.value[index] = Math.max(columns[index].min, Math.min(1200, width))
}

function startResize(event, index) {
  if (event.button !== 0 || resizing.value) return
  event.preventDefault()
  const previous = [...columnWidths.value]
  measureWidths()
  event.currentTarget.focus({ preventScroll: true })
  event.currentTarget.setPointerCapture(event.pointerId)
  resizing.value = { index, startX: event.clientX, width: columnWidths.value[index], previous, handle: event.currentTarget, pointerId: event.pointerId }
}

function moveResize(event) {
  const drag = resizing.value
  if (drag && event.pointerId === drag.pointerId) setWidth(drag.index, drag.width + event.clientX - drag.startX)
}

function endResize() {
  const drag = resizing.value
  resizing.value = null
  if (drag?.handle.hasPointerCapture(drag.pointerId)) drag.handle.releasePointerCapture(drag.pointerId)
}

function cancelResize() {
  if (resizing.value) columnWidths.value = resizing.value.previous
  endResize()
}

function resizeBy(index, delta) {
  if (resizing.value) return
  measureWidths()
  setWidth(index, columnWidths.value[index] + delta)
}

function resetWidth(index) {
  cancelResize()
  measureWidths()
  const defaults = columns.map((column) => Math.max(column.min, Math.max(scroll.value.clientWidth, 1340) * column.percent / 100))
  columnWidths.value[index] = defaults[index]
  if (columnWidths.value.every((width, i) => Math.abs(width - defaults[i]) < 1)) columnWidths.value = []
}

watch(() => props.channels.length, (length) => { if (!length) cancelResize() })
onBeforeUnmount(endResize)
</script>

<template>
  <div ref="scroll" class="table-scroll" :class="{ 'column-resizing': resizing }" :tabindex="channels.length ? 0 : undefined" :role="channels.length ? 'region' : undefined" aria-label="채널 테이블 · 가로 스크롤로 모든 정보 확인">
    <table v-if="channels.length" ref="table" :style="tableStyle">
      <caption class="sr-only">채널별 미디어 정보, RTSP URL, 송출 상태 및 제어. 열 경계를 드래그하거나 조절 버튼에서 좌우 화살표로 너비를 변경합니다. 더블클릭 또는 Home 키로 기본 너비를 복원합니다.</caption>
      <colgroup><col v-for="(column, index) in columns" :key="column.label" :style="{ width: columnWidths.length ? `${columnWidths[index]}px` : `${column.percent}%` }" /></colgroup>
      <thead>
        <tr>
          <th v-for="(column, index) in columns" :key="column.label" scope="col">
            {{ column.label }}
            <button type="button" class="column-resizer" :class="{ active: resizing?.index === index }" :aria-label="`${column.label} 열 너비 조절`" title="드래그 또는 ← →: 너비 조절 · Shift: 크게 조절 · 더블클릭 / Home: 초기화 · Esc: 취소"
              @pointerdown="startResize($event, index)" @pointermove="moveResize" @pointerup="endResize" @pointercancel="cancelResize" @lostpointercapture="endResize"
              @keydown.left.prevent="resizeBy(index, $event.shiftKey ? -32 : -8)" @keydown.right.prevent="resizeBy(index, $event.shiftKey ? 32 : 8)"
              @keydown.home.prevent="resetWidth(index)" @keydown.esc.prevent="cancelResize" @dblclick.prevent="resetWidth(index)" @click="$event.detail === 0 && resetWidth(index)"></button>
          </th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="channel in channels" :key="channel.id" :class="{ 'row-error': channel.status === 'Error' }">
          <th scope="row"><div class="channel-identity"><span class="channel-id">#{{ String(channel.id).padStart(2, '0') }}</span><strong class="cell-name" :title="channel.name">{{ channel.name }}</strong></div></th>
          <td class="state-cell">
            <span class="channel-state" :class="`status-${channel.status.toLowerCase()}`"><span class="dot" aria-hidden="true"></span>{{ statusLabels[channel.status] || channel.status }}</span>
          </td>
          <td><div class="source-summary"><span class="cell-name" :title="channel.video.fileName">{{ channel.video.fileName }}</span><small>{{ formatBytes(channel.video.fileSize) }} · {{ number(channel.video.durationSeconds, 0) }}초</small></div></td>
          <td class="media-cell">
            <div class="media-summary">
              <span class="numeric" :title="`${channel.video.videoWidth}×${channel.video.videoHeight}`">{{ channel.video.videoWidth }}×{{ channel.video.videoHeight }}</span>
              <small class="cell-name" :title="`${channel.video.videoCodec || '—'} / ${channel.video.audioCodec || '음성 없음'}`">{{ channel.video.videoCodec || '—' }} / {{ channel.video.audioCodec || '음성 없음' }}</small>
              <small v-if="!channel.video.streamCopyCompatible || channel.conversionProgress > 0 || channel.status === 'Converting'" class="conversion" role="progressbar" :aria-valuenow="percent(channel.conversionProgress)" aria-valuemin="0" aria-valuemax="100" :aria-label="`${channel.name} 변환 진행률`">{{ percent(channel.conversionProgress).toFixed(0) }}%</small>
              <small v-else class="compatible" title="원본 송출 · 복사 호환">복사 호환</small>
            </div>
          </td>
          <td class="url-cell">
            <InlineEndpoint :channel="channel" :disabled="disabled" :save="saveEndpoint" @copy="$emit('copy', $event)" @play="$emit('play', $event)" />
          </td>
          <td class="health-cell">
            <template v-if="latest(channel)">
              <div class="health-summary">
                <span class="health-reading" :title="`${number(latest(channel).fps)} fps · 송출 속도 ${number(latest(channel).speed, 2)}x · ${number(latest(channel).bitrateKbps, 0)} kbps · 지연 추정 ${number(latest(channel).latencyMs, 0)} ms`">{{ number(latest(channel).fps) }}<span>fps</span> · {{ number(latest(channel).speed, 2) }}<span>x</span> · {{ number(latest(channel).bitrateKbps, 0) }}<span>kbps</span> · {{ number(latest(channel).latencyMs, 0) }}<span>ms</span></span>
                <svg viewBox="0 0 110 28" role="img" :aria-label="`${channel.name} 최근 송출 속도 추이`"><polyline :points="sparkPoints(channel.healthSamples)" fill="none" stroke="currentColor" stroke-width="1.6" /></svg>
              </div>
            </template>
            <span v-else class="muted">— 수집 대기</span>
          </td>
          <td><div class="row-actions nowrap">
            <button class="start-button" :disabled="disabled || isBusy(channel)" :aria-label="`${channel.name} 시작`" @click="$emit('start', channel)">시작</button>
            <button :disabled="disabled || !isBusy(channel) || channel.status === 'Stopping'" :aria-label="`${channel.name} 중지`" @click="$emit('stop', channel)">중지</button>
            <button class="text-button danger-text" :disabled="disabled || channel.status === 'Stopping'" :aria-label="`${channel.name} 삭제`" @click="$emit('remove', channel)">삭제</button>
          </div></td>
        </tr>
      </tbody>
    </table>
    <div v-else class="empty-state" role="status">
      <span class="eyebrow">{{ loaded ? '첫 채널 등록' : '서버 연결' }}</span>
      <h3>{{ loaded ? '등록된 채널이 없습니다' : 'Backend 연결을 기다리는 중입니다' }}</h3>
      <p>{{ loaded ? '동영상 파일을 추가하면 검사 후 채널이 등록됩니다.' : '서버가 실행 중인지, API 프록시 주소가 올바른지 확인하세요.' }}</p>
      <button v-if="loaded" class="primary" :disabled="disabled" @click="$emit('add')">파일 추가</button>
    </div>
  </div>
</template>
