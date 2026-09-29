import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { api, problemMessage, request } from '../src/services/api.js'

const originalFetch = globalThis.fetch
afterEach(() => { globalThis.fetch = originalFetch })

function respond(data, status = 200) {
  let captured
  globalThis.fetch = async (url, options) => {
    captured = { url, ...options }
    return new Response(status === 204 ? null : JSON.stringify(data), { status })
  }
  return () => captured
}

test('status is a same-origin GET without a body', async () => {
  const captured = respond({ channels: [] })
  assert.deepEqual(await api.status(), { channels: [] })
  assert.equal(captured().url, '/api/status')
  assert.equal(captured().method, 'GET')
  assert.equal(Object.hasOwn(captured(), 'body'), false)
})

test('settings sends full JSON with required browser policy header', async () => {
  const settings = { mediaMtxHost: 'localhost', mediaMtxPort: 8554, autoRestartEnabled: true }
  const captured = respond(settings)
  assert.deepEqual(await api.settings(settings), settings)
  assert.equal(captured().method, 'PUT')
  assert.equal(captured().headers['X-RTSPCaster-Client'], 'web')
  assert.equal(captured().headers['Content-Type'], 'application/json')
  assert.deepEqual(JSON.parse(captured().body), settings)
})

test('multipart upload sends one file per request and leaves boundary to browser', async () => {
  const captured = []
  globalThis.fetch = async (url, options) => {
    if (url === '/api/uploads/limits') return Response.json({ maxUploadBytes: 4 })
    captured.push({ url, ...options })
    const file = options.body.get('files')
    return Response.json([{ fileName: file.name, channel: null, error: 'invalid media' }])
  }
  const files = [new File(['one'], '영상.mp4'), new File(['two'], 'two.mkv')]
  const result = await api.upload(files)
  assert.equal(result.length, 2)
  assert.equal(result[0].error, 'invalid media')
  assert.equal(captured.length, 2)
  for (const [index, sent] of captured.entries()) {
    assert.equal(sent.url, '/api/channels/upload')
    assert.equal(sent.headers['Content-Type'], undefined)
    assert.equal(sent.headers['X-RTSPCaster-Client'], 'web')
    assert.deepEqual(sent.body.getAll('files').map((file) => file.name), [files[index].name])
  }
})

test('delete and stop-all accept empty 204 responses', async () => {
  const captured = respond(null, 204)
  assert.equal(await api.remove(42), null)
  assert.equal(captured().url, '/api/channels/42')
  assert.equal(captured().method, 'DELETE')
  assert.equal(await api.stopAll(), null)
})

test('start preserves accepted responses, not an assertion of streaming success', async () => {
  const captured = respond({ id: 7, status: 'Probing' }, 202)
  assert.equal((await api.start(7)).status, 'Probing')
  assert.equal(captured().url, '/api/channels/7/start')
  assert.equal(captured().method, 'POST')
})

test('endpoint and template send expected contracts', async () => {
  const captured = respond({})
  const endpoint = { rtspPath: 'cam_1', mediaMtxHost: 'host', mediaMtxPort: 8554 }
  await api.endpoint(7, endpoint)
  assert.equal(captured().url, '/api/channels/7/endpoint')
  assert.deepEqual(JSON.parse(captured().body), endpoint)
  await api.template('stream_{index:D3}')
  assert.deepEqual(JSON.parse(captured().body), { template: 'stream_{index:D3}' })
})

test('ProblemDetails errors are surfaced, including 409 conflict', async () => {
  respond({ title: 'Conflict', detail: '이미 송출 중인 채널입니다.' }, 409)
  await assert.rejects(api.start(7), /이미 송출 중/)
  assert.equal(problemMessage({ errors: { host: ['호스트 오류'], port: ['포트 오류'] } }, 400), '호스트 오류\n포트 오류')
  assert.equal(problemMessage({}, 503), '요청에 실패했습니다. (HTTP 503)')
})

test('non-JSON proxy response gives a useful error', async () => {
  globalThis.fetch = async () => new Response('<html>Bad Gateway</html>', { status: 502 })
  await assert.rejects(api.status(), /API 프록시.*502/)
})

test('network and timeout errors retain their cause', async () => {
  const network = new TypeError('Failed to fetch')
  globalThis.fetch = async () => { throw network }
  await assert.rejects(api.status(), (error) => error.cause === network && error.message.includes('Backend'))
  const timeout = new DOMException('timeout', 'TimeoutError')
  globalThis.fetch = async () => { throw timeout }
  await assert.rejects(api.status(), (error) => error.cause === timeout && error.message.includes('초과'))
})

test('caller cancellation is propagated', async () => {
  const controller = new AbortController()
  controller.abort()
  globalThis.fetch = async (_url, options) => {
    options.signal.throwIfAborted()
    return new Response('{}')
  }
  await assert.rejects(request('/status', { signal: controller.signal }), { name: 'AbortError' })
})
