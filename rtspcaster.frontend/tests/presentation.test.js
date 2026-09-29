import assert from 'node:assert/strict'
import { test } from 'node:test'
import { formatBytes, formatLog, isBusy, percent, sparkPoints, statusLabels } from '../src/services/presentation.js'

test('channel command guards cover preparation, conversion, streaming and stopping', () => {
  for (const status of ['Probing', 'Converting', 'Ready', 'Streaming', 'Stopping']) {
    assert.equal(isBusy({ status, operationInProgress: false }), true, status)
  }
  assert.equal(isBusy({ status: 'Idle', operationInProgress: true }), true)
  assert.equal(isBusy({ status: 'Idle', operationInProgress: false }), false)
  assert.equal(isBusy({ status: 'Error', operationInProgress: false }), false)
  assert.equal(statusLabels.Streaming, '송출 중')
})

test('conversion progress is bounded', () => {
  assert.equal(percent(-5), 0)
  assert.equal(percent(120), 100)
  assert.equal(percent(undefined), 0)
  assert.equal(percent(35.5), 35.5)
})

test('file sizes are readable and handle invalid values', () => {
  assert.equal(formatBytes(0), '0 B')
  assert.equal(formatBytes(1024), '1.0 KiB')
  assert.equal(formatBytes(2 * 1024 ** 3), '2.0 GiB')
  assert.equal(formatBytes(-1), '—')
  assert.equal(formatBytes(undefined), '—')
})

test('speed sparkline is bounded for empty, single and long sample lists', () => {
  assert.equal(sparkPoints([]), '')
  assert.equal(sparkPoints([{ speed: 0 }]), '2,26')
  const points = sparkPoints(Array.from({ length: 40 }, (_, i) => ({ speed: i / 10 }))).split(' ')
  assert.equal(points.length, 30)
  for (const point of points) {
    const [x, y] = point.split(',').map(Number)
    assert.ok(x >= 2 && x <= 108)
    assert.ok(y >= 2 && y <= 26)
  }
})

test('logs include channel identity and preserve Unicode and newlines as plain text', () => {
  assert.match(formatLog({ timestamp: '2026-03-20T12:00:00Z', channelId: 7, message: '영상\n<script>' }), /\[채널 7\] 영상\n<script>$/)
  assert.match(formatLog({ timestamp: '2026-03-20T12:00:00Z', channelId: null, message: '서버 재시작' }), /\[서버\] 서버 재시작$/)
})
