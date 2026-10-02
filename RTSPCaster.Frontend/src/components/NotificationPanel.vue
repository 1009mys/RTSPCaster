<script setup>
import { computed, ref, watch } from 'vue'

/** @typedef {{ id: number, name: string, text: string, error: boolean, kind: string, timestamp: string }} NotificationEntry */
const props = defineProps({ entries: { type: /** @type {import('vue').PropType<NotificationEntry[]>} */ (Array), required: true } })
const emit = defineEmits(['clear'])
const expanded = ref(false)
const readThrough = ref(0)
const latestId = computed(() => props.entries[0]?.id ?? 0)
const unread = computed(() => props.entries.filter(entry => entry.id > readThrough.value).length)
/** @param {ToggleEvent} event */
function toggle(event) {
  expanded.value = event.newState === 'open'
  if (expanded.value) readThrough.value = latestId.value
}
watch(latestId, value => { if (expanded.value) readThrough.value = value })
</script>

<template>
  <div class="notifications">
    <button class="notification-button" popovertarget="task-notifications" :aria-expanded="expanded" aria-controls="task-notifications"
      :aria-label="`작업 알림${unread ? ` · 미확인 ${unread}개` : ''}`" title="작업 결과 보기">
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9Z" /><path d="M9 21h6" /></svg>
      <span v-if="unread" class="notification-count" aria-hidden="true">{{ unread > 99 ? '99+' : unread }}</span>
    </button>
    <section id="task-notifications" class="notification-panel" popover="auto" aria-labelledby="notifications-title" @beforetoggle="toggle">
      <div class="notification-heading">
        <h2 id="notifications-title">작업 알림 <span class="count">{{ entries.length }}</span></h2>
        <div class="fields">
          <button :disabled="!entries.length" @click="emit('clear')">모두 지우기</button>
          <button popovertarget="task-notifications" popovertargetaction="hide" aria-label="작업 알림 닫기">닫기</button>
        </div>
      </div>
      <p v-if="!entries.length" class="notification-empty muted">아직 작업 결과가 없습니다.</p>
      <ol v-else class="notification-list">
        <li v-for="entry in entries" :key="entry.id" :class="{ 'notification-error': entry.error }" :data-kind="entry.kind">
          <div class="notification-meta"><strong>{{ entry.name }}</strong><time :datetime="entry.timestamp">{{ new Date(entry.timestamp).toLocaleTimeString('ko-KR', { hour12: false }) }}</time></div>
          <p><span v-if="entry.error" class="bad">오류 · </span>{{ entry.text }}</p>
        </li>
      </ol>
      <p class="notification-footer hint">현재 화면을 연 이후의 최근 200개 작업 결과입니다.</p>
    </section>
  </div>
</template>
