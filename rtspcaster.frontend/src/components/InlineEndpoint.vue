<script setup>
import { computed, nextTick, ref } from 'vue'
import { isBusy } from '../services/presentation'
import { parseEndpoint } from '../services/endpoint'

const props = defineProps({ channel: { type: Object, required: true }, disabled: Boolean, save: { type: Function, required: true } })
const emit = defineEmits(['copy', 'play'])
const editing = ref(false)
const draft = ref('')
const error = ref('')
const saving = ref(false)
const input = ref(/** @type {HTMLInputElement | null} */ (null))
const editButton = ref(/** @type {HTMLButtonElement | null} */ (null))
const unavailable = computed(() => props.disabled || saving.value || isBusy(props.channel))

async function edit() {
  draft.value = props.channel.rtspUrl
  error.value = ''
  editing.value = true
  await nextTick()
  input.value?.focus()
  input.value?.select()
}

async function cancel() {
  if (saving.value) return
  editing.value = false
  error.value = ''
  await nextTick()
  editButton.value?.focus()
}

async function apply() {
  if (unavailable.value) return
  error.value = ''
  try {
    const values = parseEndpoint(draft.value)
    saving.value = true
    await props.save(props.channel.id, values)
    saving.value = false
    await cancel()
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : '주소 적용에 실패했습니다.'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <form v-if="editing" class="endpoint-editor" :aria-busy="saving" @submit.prevent="apply" @keydown.esc.stop.prevent="cancel">
    <div class="endpoint-inline">
      <input ref="input" v-model="draft" type="text" class="mono" required maxlength="1280" spellcheck="false" autocomplete="off"
        :aria-label="`${channel.name} RTSP URL 수정`" :aria-invalid="!!error" :aria-describedby="error ? `endpoint-error-${channel.id}` : undefined" :disabled="unavailable" />
      <div class="row-actions">
        <button type="submit" :disabled="unavailable" :aria-label="`${channel.name} RTSP URL 적용`">{{ saving ? '적용 중' : '적용' }}</button>
        <button type="button" :disabled="saving" :aria-label="`${channel.name} RTSP URL 수정 취소`" @click="cancel">취소</button>
      </div>
    </div>
    <p v-if="error" :id="`endpoint-error-${channel.id}`" class="endpoint-error bad" role="alert">{{ error }}</p>
    <p v-else-if="isBusy(channel)" class="endpoint-error hint">작업 중입니다. 중지 후 적용하세요.</p>
  </form>
  <div v-else class="endpoint-inline">
    <code :title="channel.rtspUrl">{{ channel.rtspUrl }}</code>
    <div class="row-actions">
      <button class="start-button" :disabled="channel.status !== 'Streaming'" :aria-label="`${channel.name} 스트림 재생`" @click="emit('play', channel)">재생</button>
      <button :aria-label="`${channel.name} RTSP URL 복사`" @click="emit('copy', channel.rtspUrl)">복사</button>
      <button ref="editButton" :disabled="unavailable" :aria-label="`${channel.name} RTSP 주소 편집`" @click="edit">편집</button>
    </div>
  </div>
</template>
