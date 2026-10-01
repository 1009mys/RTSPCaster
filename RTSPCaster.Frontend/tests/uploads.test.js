import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { api } from '../src/services/api.js'

const originalFetch = globalThis.fetch
afterEach(() => { globalThis.fetch = originalFetch })
const makeFile = (name, size = 4) => new File([new Uint8Array(size)], name)
const success = (file) => Response.json([{ fileName: file.name, channel: { id: 1 }, error: null }])

function mockUploads(handler, maxUploadBytes = 4) {
  const sent = []
  globalThis.fetch = async (url, options) => {
    options.signal.throwIfAborted()
    if (url === '/api/uploads/limits') return Response.json({ maxUploadBytes })
    const files = options.body.getAll('files')
    assert.equal(files.length, 1)
    sent.push(files[0].name)
    return handler(files[0], options)
  }
  return sent
}

test('large selections run strictly sequentially, with results before next request', async () => {
  let active = 0
  let maximum = 0
  const reported = []
  const progress = []
  const sent = mockUploads(async (file) => {
    active++
    maximum = Math.max(maximum, active)
    assert.equal(reported.length, Number(file.name))
    await new Promise((resolve) => setTimeout(resolve, 1))
    active--
    return success(file)
  })
  const files = Array.from({ length: 60 }, (_, index) => makeFile(String(index)))
  const results = await api.upload(files, undefined, { onResult: (r) => reported.push(r), onProgress: (p) => progress.push(p) })
  assert.equal(maximum, 1)
  assert.equal(sent.length, 60)
  assert.deepEqual(reported, results)
  assert.deepEqual(progress.at(-1), { index: 60, total: 60, fileName: '59' })
})

test('oversized and empty files are not transmitted; later valid files still upload', async () => {
  const sent = mockUploads(success)
  const results = await api.upload([makeFile('large', 5), makeFile('empty', 0), makeFile('valid', 4)])
  assert.deepEqual(sent, ['valid'])
  assert.match(results[0].error, /초과/)
  assert.match(results[1].error, /빈 파일/)
  assert.equal(results[2].channel.id, 1)
})

test('media validation error does not prevent later uploads', async () => {
  const sent = mockUploads((file) => file.name === 'bad'
    ? Response.json([{ fileName: file.name, channel: null, error: 'invalid media' }]) : success(file))
  const results = await api.upload([makeFile('bad'), makeFile('good')])
  assert.deepEqual(sent, ['bad', 'good'])
  assert.equal(results[0].error, 'invalid media')
  assert.equal(results[1].channel.id, 1)
})

test('connection reset preserves completed results and stops queue without retry', async () => {
  const reported = []
  const sent = mockUploads((file) => {
    if (file.name === 'reset') throw new TypeError('write ECONNRESET')
    return success(file)
  })
  await assert.rejects(api.upload([makeFile('done'), makeFile('reset'), makeFile('not-sent')], undefined,
    { onResult: (r) => reported.push(r) }), /남은 파일은 전송하지 않았습니다/)
  assert.deepEqual(sent, ['done', 'reset'])
  assert.equal(reported[0].channel.id, 1)
  assert.match(reported[1].error, /등록 여부 확인 필요/)
})

test('cancellation after completed file prevents next request', async () => {
  const controller = new AbortController()
  const reported = []
  const sent = mockUploads(success)
  await assert.rejects(api.upload([makeFile('done'), makeFile('not-sent')], controller.signal, {
    onResult: (r) => { reported.push(r); controller.abort() },
  }), { name: 'AbortError' })
  assert.deepEqual(sent, ['done'])
  assert.equal(reported.length, 1)
})

test('cancellation during upload cancels active request and retains earlier results', async () => {
  const controller = new AbortController()
  const reported = []
  const sent = mockUploads((file, options) => {
    if (file.name !== 'slow') return success(file)
    controller.abort()
    options.signal.throwIfAborted()
  })
  await assert.rejects(api.upload([makeFile('done'), makeFile('slow'), makeFile('not-sent')], controller.signal,
    { onResult: (r) => reported.push(r) }), { name: 'AbortError' })
  assert.deepEqual(sent, ['done', 'slow'])
  assert.equal(reported.length, 1)
})

test('server or proxy rejection stops queue instead of blindly retrying', async () => {
  const sent = mockUploads(() => Response.json({ detail: 'Upload too large' }, { status: 413 }))
  await assert.rejects(api.upload([makeFile('rejected'), makeFile('not-sent')]), /Upload too large/)
  assert.deepEqual(sent, ['rejected'])
})

test('invalid or unavailable limits never start sending files', async () => {
  for (const maxUploadBytes of [0, -1, null, '4', 1.5]) {
    const sent = mockUploads(success, maxUploadBytes)
    await assert.rejects(api.upload([makeFile('not-sent')]), /업로드 제한/)
    assert.equal(sent.length, 0)
  }
  let count = 0
  globalThis.fetch = async () => { count++; return Response.json({ detail: 'Unavailable' }, { status: 503 }) }
  await assert.rejects(api.upload([makeFile('not-sent')]), /Unavailable/)
  assert.equal(count, 1)
})

test('malformed upload response stops queue with result-check guidance', async () => {
  const sent = mockUploads(() => Response.json([]))
  await assert.rejects(api.upload([makeFile('unknown'), makeFile('not-sent')]), /응답 형식/)
  assert.deepEqual(sent, ['unknown'])
})

test('empty selection performs no requests', async () => {
  globalThis.fetch = async () => { throw new Error('Unexpected request') }
  assert.deepEqual(await api.upload([]), [])
})
