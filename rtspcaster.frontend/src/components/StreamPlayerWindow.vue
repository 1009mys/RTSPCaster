<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

const props = defineProps({
  player: { type: Object, required: true },
})
const emit = defineEmits(['close', 'focus'])
const minimumWidth = 320
const minimumHeight = 220
const position = ref({ x: props.player.x, y: props.player.y })
const size = ref({ width: props.player.width, height: props.player.height })
const interaction = ref(null)

const windowStyle = computed(() => ({
  left: `${position.value.x}px`,
  top: `${position.value.y}px`,
  width: `${size.value.width}px`,
  height: `${size.value.height}px`,
  zIndex: props.player.z,
}))

function viewport() {
  return { width: window.innerWidth, height: window.innerHeight }
}

function constrain() {
  const area = viewport()
  size.value = {
    width: Math.min(Math.max(minimumWidth, size.value.width), area.width),
    height: Math.min(Math.max(minimumHeight, size.value.height), area.height),
  }
  position.value = {
    x: Math.min(Math.max(0, position.value.x), Math.max(0, area.width - size.value.width)),
    y: Math.min(Math.max(0, position.value.y), Math.max(0, area.height - size.value.height)),
  }
}

function begin(event, mode) {
  if (event.button !== 0 || interaction.value || (mode === 'move' && event.target.closest('button'))) return
  event.preventDefault()
  emit('focus')
  event.currentTarget.setPointerCapture(event.pointerId)
  interaction.value = {
    mode,
    pointerId: event.pointerId,
    target: event.currentTarget,
    startX: event.clientX,
    startY: event.clientY,
    x: position.value.x,
    y: position.value.y,
    width: size.value.width,
    height: size.value.height,
  }
}

function move(event) {
  const active = interaction.value
  if (!active || active.pointerId !== event.pointerId) return
  const area = viewport()
  const dx = event.clientX - active.startX
  const dy = event.clientY - active.startY
  if (active.mode === 'move') {
    position.value = {
      x: Math.min(Math.max(0, active.x + dx), Math.max(0, area.width - size.value.width)),
      y: Math.min(Math.max(0, active.y + dy), Math.max(0, area.height - size.value.height)),
    }
  } else {
    size.value = {
      width: Math.min(Math.max(minimumWidth, active.width + dx), area.width - position.value.x),
      height: Math.min(Math.max(minimumHeight, active.height + dy), area.height - position.value.y),
    }
  }
}

function end() {
  const active = interaction.value
  interaction.value = null
  if (active?.target.hasPointerCapture(active.pointerId)) active.target.releasePointerCapture(active.pointerId)
}

onMounted(() => {
  constrain()
  window.addEventListener('resize', constrain)
})
onBeforeUnmount(() => window.removeEventListener('resize', constrain))
</script>

<template>
  <section class="stream-player-window" :class="{ interacting: interaction }" :style="windowStyle" role="dialog" :aria-label="`${player.address} 스트림 플레이어`" @pointerdown="$emit('focus')">
    <header class="stream-player-header" title="드래그하여 이동" @pointerdown="begin($event, 'move')" @pointermove="move" @pointerup="end" @pointercancel="end" @lostpointercapture="end">
      <span class="stream-player-address" :title="player.address">{{ player.address }}</span>
      <button type="button" class="stream-player-close" :aria-label="`${player.address} 플레이어 닫기`" title="닫기" @click="$emit('close')">×</button>
    </header>
    <iframe :src="player.url" :title="`${player.address} HLS 영상`" allow="autoplay; fullscreen" referrerpolicy="no-referrer" />
    <button type="button" class="stream-player-resizer" aria-label="플레이어 창 크기 조절" title="드래그하여 크기 조절" @pointerdown="begin($event, 'resize')" @pointermove="move" @pointerup="end" @pointercancel="end" @lostpointercapture="end"></button>
  </section>
</template>
