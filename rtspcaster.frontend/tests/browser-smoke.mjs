// Optional real-browser smoke test. Uses a temporary profile and an isolated mock API, never user data.
import assert from 'node:assert/strict'
import { Buffer } from 'node:buffer'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { createServer as createHttpServer } from 'node:http'
import { tmpdir } from 'node:os'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { createServer as createViteServer } from 'vite'
import vue from '@vitejs/plugin-vue'

const browserPath = process.env.BROWSER_PATH
if (!browserPath) throw new Error('Set BROWSER_PATH to a Chromium/Edge executable to run browser smoke tests.')
const root = fileURLToPath(new URL('../', import.meta.url))
const profile = await mkdtemp(path.join(tmpdir(), 'rtspcaster-browser-'))
let browser, vite, socket
let sequence = 0
const pending = new Map()
const exceptions = []
const requests = []
const streams = new Set()
let offline = false
const snapshot = {
  settings: { mediaMtxHost: '127.0.0.1', mediaMtxPort: 8554, bulkRtspTemplate: 'stream_{index}', autoRestartEnabled: true, maxAutoRestartAttempts: 3, autoRestartBaseDelaySeconds: 2, autoRestartResetThresholdSeconds: 30 },
  mediaMtx: { host: '127.0.0.1', port: 8554, reachable: true },
  channels: [1, 2].map((id) => ({ id, name: `테스트 ${id}`, rtspPath: `stream_${id}`, mediaMtxHost: '127.0.0.1', mediaMtxPort: 8554, rtspUrl: `rtsp://127.0.0.1:8554/stream_${id}`, status: 'Idle', statusMessage: '대기', conversionProgress: 0, operationInProgress: false, healthSamples: [], video: { fileName: `영상 ${id}.mp4`, fileSize: 1048576, videoWidth: 1920, videoHeight: 1080, videoCodec: 'h264', audioCodec: 'aac', durationSeconds: 60, streamCopyCompatible: true } })),
  logs: { lastId: 1, entries: [{ id: 1, timestamp: new Date().toISOString(), channelId: null, message: '연결 완료 <script>window.injected = true</script>' }] },
}
const backend = createHttpServer(async (req, res) => {
  if (req.url === '/api/events') {
    if (offline) { res.writeHead(503).end(); return }
    res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store' })
    const write = () => res.write(`event: snapshot\ndata: ${JSON.stringify(snapshot)}\n\n`)
    write()
    const interval = setInterval(write, 100)
    streams.add(res)
    req.on('close', () => { clearInterval(interval); streams.delete(res) })
    return
  }
  const chunks = []
  for await (const chunk of req) chunks.push(chunk)
  requests.push({ url: req.url, method: req.method, headers: req.headers })
  res.setHeader('Content-Type', 'application/json')
  if (req.method !== 'GET' && req.headers['x-rtspcaster-client'] !== 'web') {
    res.writeHead(403).end(JSON.stringify({ detail: 'Missing client header' })); return
  }
  if (req.url === '/api/status') { res.end(JSON.stringify(snapshot)); return }
  if (req.url === '/api/uploads/limits') { res.end(JSON.stringify({ maxUploadBytes: 4 })); return }
  if (req.url === '/api/settings') {
    snapshot.settings = JSON.parse(Buffer.concat(chunks).toString())
    res.end(JSON.stringify(snapshot.settings)); return
  }
  if (req.url === '/api/channels/upload') {
    const body = Buffer.concat(chunks).toString()
    assert.equal((body.match(/name="files"/g) ?? []).length, 1, 'one multipart file per request')
    assert.ok(!body.includes('oversized.mp4'), 'oversized file must not reach server')
    const fileName = body.match(/filename="([^"]+)"/)[1]
    const invalid = fileName === 'bad.mp4'
    res.end(JSON.stringify([{ fileName, channel: invalid ? null : snapshot.channels[0], error: invalid ? '잘못된 영상' : null }]))
    return
  }
  const match = req.url.match(/^\/api\/channels\/(\d+)(?:\/(start|stop|endpoint))?$/)
  if (match) {
    const channel = snapshot.channels.find((c) => c.id === Number(match[1]))
    if (req.method === 'DELETE') {
      snapshot.channels = snapshot.channels.filter((c) => c !== channel)
      res.writeHead(204).end(); return
    }
    if (match[2] === 'endpoint') {
      Object.assign(channel, JSON.parse(Buffer.concat(chunks).toString()))
      channel.rtspUrl = `rtsp://${channel.mediaMtxHost}:${channel.mediaMtxPort}/${channel.rtspPath}`
    } else {
      channel.status = match[2] === 'start' ? 'Streaming' : 'Idle'
      channel.healthSamples = match[2] === 'start' ? [{ fps: 30, bitrateKbps: 2000, speed: 1, latencyMs: 0 }] : []
    }
    res.end(JSON.stringify(channel)); return
  }
  res.writeHead(409).end(JSON.stringify({ detail: '테스트 충돌 오류' }))
})

async function waitFor(action, message, timeout = 15000) {
  const end = Date.now() + timeout
  while (Date.now() < end) {
    try { const result = await action(); if (result) return result } catch { /* startup/reconnect can be transient */ }
    await new Promise((resolve) => setTimeout(resolve, 100))
  }
  throw new Error(`Timed out: ${message}`)
}
function command(method, params = {}) {
  const id = ++sequence
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)) }, 10000)
    pending.set(id, { resolve, reject, timer })
    socket.send(JSON.stringify({ id, method, params }))
  })
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails))
  return result.result.value
}
async function click(text) {
  const find = `Array.from(document.querySelectorAll('button')).find(b => b.textContent.trim() === ${JSON.stringify(text)} && !b.disabled)`
  await waitFor(() => evaluate(`!!(${find})`), `enabled button ${text}`)
  await evaluate(`${find}.click()`)
}
async function clickSelector(selector) {
  const find = `document.querySelector(${JSON.stringify(selector)})`
  await waitFor(() => evaluate(`!!(${find}) && !${find}.disabled`), `enabled ${selector}`)
  await evaluate(`${find}.click()`)
}
const fill = (selector, value) => evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); el.value = ${JSON.stringify(value)}; el.dispatchEvent(new Event('input', { bubbles: true })); })()`)
const check = async (expression, message) => { await waitFor(() => evaluate(expression), message); console.log(`PASS: ${message}`) }

try {
  backend.listen(0, '127.0.0.1')
  await once(backend, 'listening')
  vite = await createViteServer({ configFile: false, root, plugins: [vue()], server: { host: '127.0.0.1', port: 0, proxy: { '/api': { target: `http://127.0.0.1:${backend.address().port}`, changeOrigin: false } } } })
  await vite.listen()
  browser = spawn(browserPath, ['--headless', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' })
  browser.on('error', (error) => exceptions.push(error.message))
  const port = await waitFor(async () => (await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0], 'browser startup')
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()
  socket = new WebSocket(pages.find((page) => page.type === 'page').webSocketDebuggerUrl)
  socket.addEventListener('message', (event) => {
    const data = JSON.parse(event.data)
    if (data.method === 'Runtime.exceptionThrown') exceptions.push(data.params.exceptionDetails.text)
    const task = pending.get(data.id)
    if (task) {
      clearTimeout(task.timer); pending.delete(data.id)
      if (data.error) task.reject(new Error(data.error.message)); else task.resolve(data.result)
    }
  })
  await once(socket, 'open')
  await command('Runtime.enable')
  await command('Page.enable')
  await command('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false })
  await command('Page.navigate', { url: `http://127.0.0.1:${vite.httpServer.address().port}` })
  await check("document.querySelectorAll('tbody tr').length === 2", 'SSE renders two channels')
  await check("document.body.textContent.includes('Backend 연결됨') && document.body.textContent.includes('영상 1.mp4')", 'connected status and Korean filename')
  await check("document.querySelectorAll('h1').length === 1 && document.querySelectorAll('thead th').length === 7 && document.querySelectorAll('tbody th[scope=row]').length === 2", 'operational hierarchy and semantic channel rows')
  await check("document.querySelector('.channels-panel').getBoundingClientRect().top < document.querySelector('.settings-disclosure').getBoundingClientRect().top && !document.querySelector('.settings-disclosure').open", 'channels precede collapsed settings')
  await check("document.querySelector('.table-scroll').scrollWidth <= document.querySelector('.table-scroll').clientWidth", 'desktop table fits the working area')
  await check("!document.querySelector('progress')", 'copy-compatible channels do not show irrelevant conversion progress')
  await evaluate("(() => { const data = new DataTransfer(); data.items.add(new File(['video'], 'drag.mp4', { type: 'video/mp4' })); document.querySelector('.channels-panel').dispatchEvent(new DragEvent('dragenter', { bubbles: true, dataTransfer: data })); })()")
  await check("!!document.querySelector('.channels-panel.drop-active') && document.querySelector('.table-footer').textContent.includes('파일을 놓으면')", 'file drag exposes drop target feedback')
  await evaluate("document.querySelector('.channels-panel').dispatchEvent(new DragEvent('drop', { bubbles: true, dataTransfer: new DataTransfer() }))")
  await check("!document.querySelector('.drop-active')", 'drop clears drag feedback')
  assert.equal(await evaluate('window.injected'), undefined)
  await check("document.querySelector('.log-message').textContent.includes('<script>') && !!document.querySelector('.log-line time[datetime]')", 'structured logs retain escaped messages and timestamps')
  const logTimesFit = "Array.from(document.querySelectorAll('.log-line time')).every(time => { const range = document.createRange(); range.selectNodeContents(time); const rects = range.getClientRects(); return rects.length === 1 && time.scrollWidth <= time.clientWidth && rects[0].right <= time.nextElementSibling.getBoundingClientRect().left; })"
  await check(logTimesFit, 'desktop log timestamps stay on one line without overlapping the source')
  await fill('input[aria-label="로그 검색"]', 'no-match')
  await check("document.querySelector('.log-empty').textContent.includes('검색 조건')", 'log filter empty state')
  await fill('input[aria-label="로그 검색"]', '')
  await clickSelector('.log-heading input[type=checkbox]')
  await check("document.querySelector('.log-footer').textContent.includes('자동 스크롤 꺼짐')", 'paused log follow feedback')
  await clickSelector('.log-heading input[type=checkbox]')
  await fill('input[aria-label="채널 검색"]', '영상 2')
  await check("document.querySelectorAll('tbody tr').length === 1", 'channel search')
  await fill('input[aria-label="채널 검색"]', 'no-match')
  await check("document.querySelector('.channels-panel .empty-state').textContent.includes('검색 조건')", 'channel filter empty state')
  await click('필터 초기화')
  await check("document.querySelectorAll('tbody tr').length === 2 && document.querySelector('input[aria-label=\"채널 검색\"]').value === ''", 'channel filters reset')
  snapshot.channels[1].status = 'Converting'
  snapshot.channels[1].conversionProgress = 42
  snapshot.channels[1].video.streamCopyCompatible = false
  snapshot.channels[1].video.incompatibleReason = '영상 코덱 변환 필요'
  await check("document.querySelector('progress')?.value === 42 && !!document.querySelector('.status-converting') && document.querySelector('button[aria-label=\"테스트 2 시작\"]').disabled", 'conversion state and disabled controls')
  snapshot.channels[1].status = 'Error'
  snapshot.channels[1].statusMessage = '송출 연결 실패'
  await check("document.querySelector('.row-error .status-error').textContent.includes('오류') && document.querySelector('.summary .bad dd').textContent === '1'", 'error status has text and summary emphasis')
  snapshot.channels[1].status = 'Idle'
  snapshot.channels[1].statusMessage = '대기'
  snapshot.channels[1].conversionProgress = 0
  snapshot.channels[1].video.streamCopyCompatible = true
  await clickSelector('.settings-disclosure > summary')
  await check("document.querySelector('.settings-disclosure').open", 'settings disclosure opens')
  await fill('.settings-panel fieldset input', 'changed.example')
  await new Promise((resolve) => setTimeout(resolve, 400))
  await check("document.querySelector('.settings-panel fieldset input').value === 'changed.example'", 'SSE preserves settings draft')
  await evaluate("document.querySelector('.settings-disclosure > summary').focus()")
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' })
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 })
  await check("!document.querySelector('.settings-disclosure').open && document.querySelector('.settings-disclosure > summary .unsaved').textContent.includes('저장하지 않은')", 'keyboard collapse retains visible unsaved warning')
  await clickSelector('.settings-disclosure > summary')
  await check("document.querySelector('.settings-panel fieldset input').value === 'changed.example'", 'reopening settings preserves draft')
  await click('설정 저장')
  await waitFor(() => snapshot.settings.mediaMtxHost === 'changed.example', 'settings persisted')
  await check("Array.from(document.querySelectorAll('button')).find(b => b.textContent === '설정 저장').disabled", 'saved settings clean')
  await clickSelector('button[aria-label="테스트 1 시작"]')
  await check("!!document.querySelector('.status-streaming') && !!document.querySelector('.health-cell svg')", 'start and live health graph')
  await clickSelector('button[aria-label="테스트 1 중지"]')
  await check("document.querySelector('button[aria-label=\"테스트 1 시작\"]').disabled === false", 'stop returns channel to idle')
  await clickSelector('button[aria-label="테스트 1 RTSP 주소 편집"]')
  await check("document.querySelector('dialog[open]') !== null", 'native modal opens')
  await fill('dialog input[maxlength="512"]', 'edited')
  await click('저장')
  await check("document.querySelector('.url-cell code').textContent.endsWith('/edited') && !document.querySelector('dialog')", 'endpoint edit')
  await clickSelector('button[aria-label="테스트 1 RTSP 주소 편집"]')
  await check("!!document.querySelector('dialog[open]') && document.querySelector('dialog').contains(document.activeElement)", 'dialog receives keyboard focus')
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await check("!document.querySelector('dialog')", 'Escape dismisses endpoint dialog without saving')
  await clickSelector('button[aria-label="테스트 2 삭제"]')
  await click('확인')
  await check("document.querySelectorAll('tbody tr').length === 1 && !document.querySelector('dialog')", 'confirmed deletion')
  await waitFor(() => evaluate("!document.querySelector('button.primary').disabled"), 'upload enabled after deletion')
  await evaluate(`(() => { const data = new DataTransfer(); for (const [name, content] of [['good.mp4', 'good'], ['bad.mp4', 'bad'], ['oversized.mp4', 'oversized'], ['last.mp4', 'last']]) data.items.add(new File([content], name, { type: 'video/mp4' })); const input = document.querySelector('input[type=file]'); input.files = data.files; input.dispatchEvent(new Event('change', { bubbles: true })); })()`)
  await check("document.querySelector('.results')?.textContent.includes('잘못된 영상')", 'per-file upload errors')
  await check("document.querySelectorAll('.results li').length === 4 && !document.querySelector('.upload-status')", 'all per-file results retained after sequential upload')
  await check("document.querySelector('.results').textContent.includes('초과하여 전송하지 않았습니다')", 'oversized file rejected before transmission')
  assert.equal(requests.filter((r) => r.url === '/api/channels/upload').length, 3)
  await click('연결 확인')
  await check("document.querySelector('[role=alert]')?.textContent.includes('테스트 충돌 오류')", 'ProblemDetails visible')
  offline = true
  for (const stream of streams) stream.end()
  await check("document.body.textContent.includes('실시간 연결이 끊겼습니다') && document.querySelector('button.primary').disabled", 'disconnect disables commands and retains last data')
  offline = false
  await check("document.body.textContent.includes('Backend 연결됨') && !document.querySelector('button.primary').disabled", 'automatic SSE reconnect')
  snapshot.channels[0].name = '긴 채널 이름 '.repeat(20)
  snapshot.channels[0].video.fileName = `${'long-video-name-'.repeat(20)}.mp4`
  snapshot.channels[0].rtspUrl = `rtsp://127.0.0.1:8554/${'long-path-'.repeat(30)}`
  snapshot.logs.entries.push({ id: 2, timestamp: new Date().toISOString(), channelId: 1, message: `오류 ${'long-message-'.repeat(50)}` })
  await check("document.querySelector('.cell-name').textContent.includes('긴 채널') && !!document.querySelector('.log-error')", 'long content and error log rendered')
  for (const width of [1280, 768, 390, 320]) {
    await command('Emulation.setDeviceMetricsOverride', { width, height: 844, deviceScaleFactor: 1, mobile: width < 720 })
    await command('Emulation.setTouchEmulationEnabled', { enabled: width < 720 })
    await check('document.documentElement.scrollWidth <= window.innerWidth', `${width}px layout with expanded settings and long content has no page overflow`)
    await check("document.querySelector('.table-scroll').scrollWidth > document.querySelector('.table-scroll').clientWidth", `${width}px table scrolls within its own region`)
    await check(logTimesFit, `${width}px log timestamps fit on one line`)
    await evaluate("document.querySelector('.log-output').style.fontSize = '18px'")
    await check(logTimesFit, `${width}px enlarged log timestamps do not wrap or overlap`)
    await evaluate("document.querySelector('.log-output').style.removeProperty('font-size')")
  }
  await check("Array.from(document.querySelectorAll('.row-actions button')).every(button => button.getBoundingClientRect().height >= 44)", 'touch channel controls have 44px targets')
  await evaluate("document.querySelector('.table-scroll').focus()")
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 })
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 })
  await check("document.querySelector('.table-scroll').scrollLeft > 0", 'table supports keyboard horizontal scrolling')
  await command('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] })
  await check("getComputedStyle(document.querySelector('button')).transitionDuration === '0s'", 'reduced-motion preference disables control transitions')
  snapshot.channels = []
  await check("document.querySelector('.channels-panel .empty-state h3').textContent.includes('등록된 채널이 없습니다') && !document.querySelector('table')", 'empty channel onboarding preserves add action')
  assert.ok(requests.some((r) => r.method === 'PUT' && r.headers.origin?.includes(String(vite.httpServer.address().port))))
  assert.deepEqual(exceptions, [])
  console.log('PASS: browser smoke completed without runtime exceptions')
} finally {
  if (socket?.readyState === WebSocket.OPEN) {
    try { await command('Browser.close') } catch { /* browser may close before acknowledging */ }
    socket.close()
  }
  if (browser && browser.exitCode === null) {
    await Promise.race([once(browser, 'exit'), new Promise((resolve) => setTimeout(resolve, 3000))])
    if (browser.exitCode === null) browser.kill()
  }
  for (const task of pending.values()) clearTimeout(task.timer)
  await vite?.close()
  for (const stream of streams) stream.end()
  backend.closeAllConnections()
  await new Promise((resolve) => backend.close(resolve))
  await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 })
}
