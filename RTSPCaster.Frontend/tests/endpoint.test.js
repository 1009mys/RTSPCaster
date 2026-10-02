import test from 'node:test'
import assert from 'node:assert/strict'
import { parseEndpoint, toHlsUrl } from '../src/services/endpoint.js'

test('parses RTSP host, explicit port and nested path', () => {
  assert.deepEqual(parseEndpoint(' rtsp://media.example:8554/live/camera_1 '), {
    mediaMtxHost: 'media.example', mediaMtxPort: 8554, rtspPath: 'live/camera_1',
  })

test('creates MediaMTX HLS player URLs on port 8888', () => {
  assert.equal(toHlsUrl('rtsp://media.example:8554/live/camera_1', 'http:'), 'http://media.example:8888/live/camera_1')
  assert.equal(toHlsUrl('rtsp://127.0.0.1/stream', 'https:'), 'https://127.0.0.1:8888/stream')
})
})

test('uses standard RTSP port and trims path slashes', () => {
  assert.deepEqual(parseEndpoint('rtsp://127.0.0.1/live/'), {
    mediaMtxHost: '127.0.0.1', mediaMtxPort: 554, rtspPath: 'live',
  })
})

test('rejects unsupported or invalid endpoints without silently rewriting them', () => {
  for (const value of [
    '', 'http://host/live', 'rtsp://host/', 'rtsp://host:0/live', 'rtsp://host:65536/live',
    'rtsp://user:pass@host/live', 'rtsp://[::1]/live', 'rtsp://host/live?q=1',
    'rtsp://host/live#fragment', 'rtsp://host/live path', 'rtsp://host/a/../b',
    'rtsp://-host/live', `rtsp://host/${'a'.repeat(513)}`,
  ]) assert.throws(() => parseEndpoint(value), Error, value)
})
