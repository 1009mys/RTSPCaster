<script setup>
import { reactive, ref, watch } from 'vue'

const props = defineProps({ settings: { type: Object, required: true }, mediaMtx: Object, disabled: Boolean })
const emit = defineEmits(['save', 'check', 'template', 'help'])
const draft = reactive({ fileLoggingEnabled: true, ...props.settings })
const dirty = ref(false)

function reset(value = props.settings) {
  Object.assign(draft, { fileLoggingEnabled: true, ...value })
  dirty.value = false
}
watch(() => props.settings, (value) => { if (!dirty.value) reset(value) })
defineExpose({ saved: reset })
</script>

<template>
  <details class="settings-disclosure">
    <summary>
      <span class="disclosure-heading">연결 및 송출 설정</span>
      <span class="settings-target">MediaMTX <code>{{ settings.mediaMtxHost }}:{{ settings.mediaMtxPort }}</code></span>
      <span class="connection-state" :class="{ good: mediaMtx?.reachable === true, bad: mediaMtx?.reachable === false }"><span class="dot" aria-hidden="true"></span>{{ mediaMtx?.reachable === true ? '연결됨' : mediaMtx?.reachable === false ? '연결할 수 없음' : '확인 전' }}</span>
      <span v-if="dirty" class="unsaved">저장하지 않은 변경</span>
      <span class="hint">.log 기록 {{ settings.fileLoggingEnabled === false ? '꺼짐' : '켜짐' }}</span>
      <span class="disclosure-action"><span class="when-closed">설정 펼치기</span><span class="when-open">설정 접기</span><span class="chevron" aria-hidden="true"></span></span>
    </summary>
    <form class="settings-panel" @submit.prevent="emit('save', { ...draft })" @input="dirty = true">
      <fieldset :disabled="disabled">
        <legend>MediaMTX 연결</legend>
        <div class="fields">
          <label class="grow">호스트<input v-model.trim="draft.mediaMtxHost" required maxlength="253" placeholder="127.0.0.1" /></label>
          <label class="number-field">포트<input v-model.number="draft.mediaMtxPort" required type="number" min="1" max="65535" /></label>
          <button type="button" @click="emit('check')">연결 확인</button>
        </div>
        <p class="connection-detail" :class="{ good: mediaMtx?.reachable === true, bad: mediaMtx?.reachable === false }">
          <span class="dot" aria-hidden="true"></span>
          {{ mediaMtx?.reachable === true ? '연결됨' : mediaMtx?.reachable === false ? '연결할 수 없음' : '확인 전' }}
          <span class="muted">{{ mediaMtx?.host }}:{{ mediaMtx?.port }}</span>
        </p>
        <p class="hint">연결 확인은 저장된 주소 기준 · 주소 변경은 신규 채널에 적용</p>
      </fieldset>

      <fieldset :disabled="disabled">
        <legend>RTSP URL 템플릿</legend>
        <label>템플릿<input v-model.trim="draft.bulkRtspTemplate" required maxlength="1024" class="mono" /></label>
        <div class="fields template-actions">
          <span class="hint grow">{host} · {port} · {index} · {index:D3} · {name}</span>
          <button type="button" class="text-button" @click="emit('help')">도움말</button>
          <button type="button" :disabled="!draft.bulkRtspTemplate" @click="emit('template', draft.bulkRtspTemplate)">전체 적용</button>
        </div>
        <p class="hint">작업 중인 채널은 제외 · 경로만 입력하면 기존 호스트 유지</p>
      </fieldset>

      <fieldset :disabled="disabled">
        <legend>자동 재시작</legend>
        <label class="check"><input v-model="draft.autoRestartEnabled" type="checkbox" /> 활성화</label>
        <div class="fields restart-fields">
          <label>최대 재시도<input v-model.number="draft.maxAutoRestartAttempts" type="number" required min="0" max="20" /></label>
          <label>기본 지연(s)<input v-model.number="draft.autoRestartBaseDelaySeconds" type="number" required min="1" max="120" /></label>
          <label>리셋 기준(s)<input v-model.number="draft.autoRestartResetThresholdSeconds" type="number" required min="5" max="3600" /></label>
        </div>
      </fieldset>

      <div class="settings-footer">
        <div class="logging-setting">
          <label class="check"><input v-model="draft.fileLoggingEnabled" type="checkbox" :disabled="disabled" /> .log 파일 기록</label>
          <p class="hint">설정 저장 시 즉시 적용 · 다음 실행에도 유지 · 꺼도 기존 파일과 화면 로그는 유지</p>
          <p class="hint" :class="{ unsaved: dirty }">{{ dirty ? '저장하지 않은 설정이 있습니다. 실시간 갱신 시에도 입력값을 유지합니다.' : '서버 설정과 동기화됨' }}</p>
        </div>
        <div class="fields">
          <button type="button" :disabled="disabled || !dirty" @click="reset()">변경 취소</button>
          <button type="submit" class="primary" :disabled="disabled || !dirty">설정 저장</button>
        </div>
      </div>
    </form>
  </details>
</template>
