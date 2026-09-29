export const statusLabels = {
  Idle: '대기', Probing: '검사 중', Converting: '변환 중', Ready: '송출 준비',
  Streaming: '송출 중', Stopping: '중지 중', Error: '오류',
}

export function isBusy(channel) {
  return channel.operationInProgress || ['Probing', 'Converting', 'Ready', 'Streaming', 'Stopping'].includes(channel.status)
}

export function percent(value) {
  return Math.max(0, Math.min(100, Number(value) || 0))
}

export function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes < 0) return '—'
  if (bytes < 1024) return `${bytes} B`
  const unit = Math.min(3, Math.floor(Math.log(bytes) / Math.log(1024)))
  return `${(bytes / 1024 ** unit).toFixed(1)} ${['B', 'KiB', 'MiB', 'GiB'][unit]}`
}

export function sparkPoints(samples) {
  const values = samples.slice(-30).map((sample) => Math.max(0, Number(sample.speed) || 0))
  const max = Math.max(1.2, ...values)
  return values.map((value, index) => `${2 + index * 106 / Math.max(1, values.length - 1)},${26 - value / max * 24}`).join(' ')
}

export function formatLog(entry) {
  return `${new Date(entry.timestamp).toLocaleTimeString('ko-KR', { hour12: false })} ${entry.channelId == null ? '[서버]' : `[채널 ${entry.channelId}]`} ${entry.message}`
}
