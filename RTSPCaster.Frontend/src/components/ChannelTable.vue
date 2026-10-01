<script setup>
import { formatBytes, isBusy, percent, sparkPoints, statusLabels } from '../services/presentation'

defineProps({ channels: { type: Array, required: true }, disabled: Boolean, loaded: Boolean })
defineEmits(['start', 'stop', 'remove', 'edit', 'copy', 'add'])
const number = (value, digits = 1) => Number.isFinite(value) ? value.toFixed(digits) : '—'
const latest = (channel) => channel.healthSamples.at(-1)
</script>

<template>
  <div class="table-scroll" :tabindex="channels.length ? 0 : undefined" :role="channels.length ? 'region' : undefined" aria-label="채널 테이블 · 가로 스크롤로 모든 정보 확인">
    <table v-if="channels.length">
      <caption class="sr-only">채널별 미디어 정보, RTSP URL, 송출 상태 및 제어</caption>
      <thead>
        <tr>
          <th scope="col">채널</th><th scope="col">송출 상태</th><th scope="col">소스 파일</th>
          <th scope="col">미디어 / 호환성</th><th scope="col">RTSP URL</th>
          <th scope="col">송출 품질</th><th scope="col">제어</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="channel in channels" :key="channel.id" :class="{ 'row-error': channel.status === 'Error' }">
          <th scope="row"><span class="channel-id">#{{ String(channel.id).padStart(2, '0') }}</span><strong class="cell-name" :title="channel.name">{{ channel.name }}</strong></th>
          <td class="state-cell">
            <span class="channel-state" :class="`status-${channel.status.toLowerCase()}`"><span class="dot" aria-hidden="true"></span>{{ statusLabels[channel.status] || channel.status }}</span>
            <small class="cell-reason" :title="channel.statusMessage">{{ channel.statusMessage }}</small>
            <div v-if="!channel.video.streamCopyCompatible || channel.conversionProgress > 0 || channel.status === 'Converting'" class="conversion">
              <div><span>변환</span><span>{{ percent(channel.conversionProgress).toFixed(0) }}%</span></div>
              <progress :value="percent(channel.conversionProgress)" max="100" :aria-label="`${channel.name} 변환 진행률`"></progress>
            </div>
          </td>
          <td><span class="cell-name" :title="channel.video.fileName">{{ channel.video.fileName }}</span><small>{{ formatBytes(channel.video.fileSize) }} · {{ number(channel.video.durationSeconds, 0) }}초</small></td>
          <td class="media-cell"><span class="numeric">{{ channel.video.videoWidth }} × {{ channel.video.videoHeight }}</span><small>{{ channel.video.videoCodec || '—' }} / {{ channel.video.audioCodec || '음성 없음' }}</small><small :class="channel.video.streamCopyCompatible ? 'compatible' : 'convert'">{{ channel.video.streamCopyCompatible ? '원본 송출 · 복사 호환' : '변환 필요' }}</small><small v-if="!channel.video.streamCopyCompatible" class="cell-reason" :title="channel.video.incompatibleReason">{{ channel.video.incompatibleReason }}</small></td>
          <td class="url-cell">
            <code :title="channel.rtspUrl">{{ channel.rtspUrl }}</code>
            <div class="row-actions">
              <button class="text-button" :aria-label="`${channel.name} RTSP URL 복사`" @click="$emit('copy', channel.rtspUrl)">복사</button>
              <button class="text-button" :disabled="disabled || isBusy(channel)" :aria-label="`${channel.name} RTSP 주소 편집`" @click="$emit('edit', channel)">편집</button>
            </div>
          </td>
          <td class="health-cell">
            <template v-if="latest(channel)">
              <span class="health-reading">{{ number(latest(channel).fps) }} <span>fps</span> · {{ number(latest(channel).speed, 2) }}<span>x</span></span>
              <small>{{ number(latest(channel).bitrateKbps, 0) }} kbps · 지연 추정 {{ number(latest(channel).latencyMs, 0) }} ms</small>
              <svg viewBox="0 0 110 28" role="img" :aria-label="`${channel.name} 최근 송출 속도 추이`"><polyline :points="sparkPoints(channel.healthSamples)" fill="none" stroke="currentColor" stroke-width="1.6" /></svg>
            </template>
            <span v-else class="muted">—<small>수집 대기</small></span>
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
