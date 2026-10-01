<script setup>
import { computed, nextTick, ref, watch } from 'vue'
import { formatLog } from '../services/presentation'

const props = defineProps({ logs: { type: Array, default: () => [] }, channels: { type: Array, default: () => [] } })
const emit = defineEmits(['copy'])
const channelFilter = ref('')
const query = ref('')
const follow = ref(true)
const output = ref(null)
const filtered = computed(() => props.logs.filter((entry) =>
  (channelFilter.value === '' || String(entry.channelId) === channelFilter.value)
  && entry.message.toLocaleLowerCase().includes(query.value.toLocaleLowerCase())))

async function scrollToEnd() {
  await nextTick()
  if (follow.value && output.value) output.value.scrollTop = output.value.scrollHeight
}
watch(() => [props.logs.at(-1)?.id, props.logs.at(-1)?.timestamp, channelFilter.value, query.value], scrollToEnd)
watch(follow, scrollToEnd)
</script>

<template>
  <section class="log-panel" aria-labelledby="logs-title">
    <div class="section-heading log-heading">
      <h2 id="logs-title">운영 로그 <span class="count">{{ filtered.length }}</span></h2>
      <div class="fields">
        <select v-model="channelFilter" aria-label="로그 채널 필터">
          <option value="">모든 채널</option><option value="null">서버</option>
          <option v-for="channel in channels" :key="channel.id" :value="String(channel.id)">#{{ channel.id }} {{ channel.name }}</option>
        </select>
        <input v-model="query" type="search" aria-label="로그 검색" placeholder="로그 검색" />
        <label class="check"><input v-model="follow" type="checkbox" /> 자동 스크롤</label>
        <button :disabled="!filtered.length" @click="emit('copy', filtered.map(formatLog).join('\n'))">표시 로그 복사</button>
      </div>
    </div>
    <div ref="output" class="log-output" tabindex="0" role="region" aria-label="최근 서버 로그">
      <p v-if="!filtered.length" class="log-empty">{{ logs.length ? '검색 조건에 맞는 로그가 없습니다.' : '아직 수신된 로그가 없습니다. 서버의 작업 내역이 이곳에 표시됩니다.' }}</p>
      <div v-for="entry in filtered" :key="`${entry.timestamp}-${entry.id}`" class="log-line" :class="{ 'log-error': /error|failed|실패|오류/i.test(entry.message) }">
        <time :datetime="entry.timestamp" :title="new Date(entry.timestamp).toLocaleString('ko-KR')">{{ new Date(entry.timestamp).toLocaleTimeString('ko-KR', { hour12: false }) }}</time>
        <span class="log-source">{{ entry.channelId == null ? '서버' : `채널 ${entry.channelId}` }}</span>
        <span class="log-message">{{ entry.message }}</span>
      </div>
    </div>
    <div class="log-footer"><span>최근 최대 500개 · 약 1초 간격으로 갱신</span><span>{{ follow ? '새 로그를 자동으로 따라갑니다' : '자동 스크롤 꺼짐 · 이전 로그 확인 중' }}</span></div>
  </section>
</template>
