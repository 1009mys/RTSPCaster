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
let logFiles = Array.from({ length: 101 }, (_, index) => ({ name: `ch${index + 1}_20260320.log`, size: 20, lastWriteTimeUtc: '2026-03-20T10:00:00Z' }))
let logReadError = false
const snapshot = {
  settings: { mediaMtxHost: '127.0.0.1', mediaMtxPort: 8554, bulkRtspTemplate: 'stream_{index}', autoRestartEnabled: true, fileLoggingEnabled: true, maxAutoRestartAttempts: 3, autoRestartBaseDelaySeconds: 2, autoRestartResetThresholdSeconds: 30 },
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
  const requestUrl = new URL(req.url, 'http://localhost')
  if (requestUrl.pathname === '/api/log-files') {
    const skip = Number(requestUrl.searchParams.get('skip') || 0)
    res.end(JSON.stringify({ files: logFiles.slice(skip, skip + 100), hasMore: skip + 100 < logFiles.length })); return
  }
  if (requestUrl.pathname === '/api/log-files/content') {
    const file = logFiles.find(file => file.name === requestUrl.searchParams.get('name'))
    if (logReadError || !file) { res.writeHead(404).end(JSON.stringify({ detail: '로그 파일을 찾을 수 없습니다.' })); return }
    const offset = Number(requestUrl.searchParams.get('offset') ?? 10)
    res.end(JSON.stringify({ ...file, offset, nextOffset: offset + 10, hasMore: offset === 0,
      content: offset === 0 ? '처음 구간\n한글 기록' : '마지막 구간\n<script>window.fileInjected = true</script>' })); return
  }
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
  await check("!document.querySelector('.conversion')", 'copy-compatible channels do not show irrelevant conversion progress')
  const compactRows = "Array.from(document.querySelectorAll('tbody tr')).every(row => row.getBoundingClientRect().height <= 36)"
  await check(compactRows, 'idle channel rows fit within 36px')
  await check("Array.from(document.querySelectorAll('.channel-identity, .endpoint-inline, .source-summary, .media-summary')).every(group => { const first = group.firstElementChild.getBoundingClientRect(); const last = group.lastElementChild.getBoundingClientRect(); return first.right <= last.left && first.top < last.bottom && last.top < first.bottom; })", 'channel identity, endpoint actions, source and media metadata align horizontally')
  await check("Array.from(document.querySelectorAll('.source-summary, .media-summary')).every(group => group.getBoundingClientRect().height <= 20)", 'all source and media information stays on one line')
  await check("document.querySelector('thead th:nth-child(2)').getBoundingClientRect().width <= document.querySelector('table').getBoundingClientRect().width * 0.071", 'status column uses only seven percent of table width')
  await check("Array.from(document.querySelectorAll('.url-cell button')).every(button => { const style = getComputedStyle(button); return style.borderTopStyle === 'solid' && style.borderTopWidth === '1px' && style.borderTopColor !== 'rgba(0, 0, 0, 0)'; })", 'copy and edit buttons have visible outlines')
  await check("!document.querySelector('.state-cell .cell-reason')", 'duplicate idle status is not repeated')
  await check("document.querySelectorAll('button.column-resizer').length === 7", 'every column has a keyboard-focusable resize handle')
  const originalWidths = await evaluate("Array.from(document.querySelectorAll('thead th'), cell => cell.getBoundingClientRect().width)")
  const sourceHandle = 'thead th:nth-child(3) .column-resizer'
  const point = await evaluate(`(() => { const rect = document.querySelector(${JSON.stringify(sourceHandle)}).getBoundingClientRect(); return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 }; })()`)
  await command('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', buttons: 1, clickCount: 1 })
  await command('Input.dispatchMouseEvent', { type: 'mouseMoved', x: point.x + 80, y: point.y, buttons: 1 })
  await command('Input.dispatchMouseEvent', { type: 'mouseReleased', x: point.x + 80, y: point.y, button: 'left', buttons: 0, clickCount: 1 })
  await check(`Math.abs(document.querySelector('thead th:nth-child(3)').getBoundingClientRect().width - ${originalWidths[2] + 80}) < 1`, 'drag expands only the source column by 80px')
  await check(`Array.from(document.querySelectorAll('thead th')).every((cell, index) => index === 2 || Math.abs(cell.getBoundingClientRect().width - ${JSON.stringify(originalWidths)}[index]) < 1)`, 'resizing preserves all neighboring column widths')
  await new Promise((resolve) => setTimeout(resolve, 400))
  await check(`Math.abs(document.querySelector('thead th:nth-child(3)').getBoundingClientRect().width - ${originalWidths[2] + 80}) < 1`, 'live snapshots preserve the resized width')
  await evaluate(`document.querySelector(${JSON.stringify(sourceHandle)}).focus()`)
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'ArrowLeft', code: 'ArrowLeft', windowsVirtualKeyCode: 37 })
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'ArrowLeft', code: 'ArrowLeft', windowsVirtualKeyCode: 37 })
  await check(`Math.abs(document.querySelector('thead th:nth-child(3)').getBoundingClientRect().width - ${originalWidths[2] + 72}) < 1`, 'left arrow reduces column width by 8px')
  await evaluate(`(() => { const handle = document.querySelector(${JSON.stringify(sourceHandle)}); for (let i = 0; i < 50; i++) handle.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', shiftKey: true, bubbles: true, cancelable: true })); })()`)
  await check("Math.abs(document.querySelector('thead th:nth-child(3)').getBoundingClientRect().width - 150) < 1", 'column minimum width prevents excessive shrinking')
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Home', code: 'Home', windowsVirtualKeyCode: 36 })
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Home', code: 'Home', windowsVirtualKeyCode: 36 })
  await check("!document.querySelector('table').style.width", 'Home restores default responsive column widths')
  await command('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', buttons: 1, clickCount: 1 })
  await command('Input.dispatchMouseEvent', { type: 'mouseMoved', x: point.x + 40, y: point.y, buttons: 1 })
  await check("!!document.querySelector('.column-resizing')", 'active resize exposes drag feedback')
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await command('Input.dispatchMouseEvent', { type: 'mouseReleased', x: point.x + 40, y: point.y, button: 'left', buttons: 0, clickCount: 1 })
  await check("!document.querySelector('.column-resizing') && !document.querySelector('table').style.width", 'Escape cancels dragging and restores previous widths')
  await evaluate(`document.querySelector(${JSON.stringify(sourceHandle)}).dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }))`)
  await check("!!document.querySelector('table').style.width", 'right arrow sets a custom column width')
  await evaluate(`document.querySelector(${JSON.stringify(sourceHandle)}).dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true }))`)
  await check("!document.querySelector('table').style.width", 'double-click resets the column to its default width')
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
  snapshot.channels[1].video.incompatibleReason = "video codec 'mpeg4' not RTSP-compatible"
  await check("document.querySelector('.conversion')?.textContent.trim() === '42%' && !!document.querySelector('.status-converting') && document.querySelector('button[aria-label=\"테스트 2 시작\"]').disabled", 'conversion percentage and disabled controls')
  await check("!document.querySelector('.state-cell .conversion') && document.querySelector('.media-cell [role=progressbar]')?.getAttribute('aria-valuenow') === '42' && document.querySelector('.status-converting').parentElement.textContent.trim() === '변환 중'", 'status contains only its label and accessible conversion percentage belongs to media')
  await check("!document.querySelector('progress') && !document.querySelector('.media-cell .cell-reason') && Array.from(document.querySelectorAll('.media-cell')).every(cell => !cell.textContent.includes('변환 필요') && !cell.textContent.includes('not RTSP-compatible')) && !document.querySelector('.media-cell [title*=\"RTSP-compatible\"]')", 'conversion shows percentage without bar, explanation or reason tooltip')
  await check("Array.from(document.querySelectorAll('.media-summary')).every(group => group.getBoundingClientRect().height <= 20)", 'media information and percentage stay on one line')
  snapshot.channels[1].status = 'Error'
  snapshot.channels[1].statusMessage = '송출 연결 실패'
  await check("document.querySelector('.row-error .status-error').textContent.includes('오류') && document.querySelector('.summary .bad dd').textContent === '1'", 'error status has text and summary emphasis')
  await check("document.querySelector('.row-error .state-cell').textContent.trim() === '오류' && document.querySelector('.row-error .state-cell').children.length === 1", 'error status shows only its colored label without secondary text')
  snapshot.channels[1].status = 'Idle'
  snapshot.channels[1].statusMessage = '중지됨'
  snapshot.channels[1].conversionProgress = 0
  snapshot.channels[1].video.streamCopyCompatible = true
  await check("Array.from(document.querySelectorAll('.state-cell')).every(cell => cell.textContent.trim() === '대기' && cell.children.length === 1)", 'idle status omits stopped and other secondary messages')
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
  await clickSelector('.logging-setting input[type=checkbox]')
  await click('설정 저장')
  await waitFor(() => snapshot.settings.mediaMtxHost === 'changed.example', 'settings persisted')
  await check("!document.querySelector('.logging-setting input').checked && document.querySelector('.settings-disclosure > summary').textContent.includes('.log 기록 꺼짐')", 'saved file logging policy is reflected in settings')
  assert.equal(snapshot.settings.fileLoggingEnabled, false)
  await check("Array.from(document.querySelectorAll('button')).find(b => b.textContent === '설정 저장').disabled", 'saved settings clean')
  snapshot.channels[0].statusMessage = 'Starting stream copy'
  await clickSelector('button[aria-label="테스트 1 시작"]')
  await check("!!document.querySelector('.status-streaming') && !!document.querySelector('.health-cell svg')", 'start and live health graph')
  await check("!document.querySelector('.channels-panel').textContent.includes('Starting stream copy')", 'routine stream-copy startup message is omitted')
  await check(compactRows, 'streaming rows with health graphs fit within 36px')
  await check("document.querySelector('.health-reading').getBoundingClientRect().right <= document.querySelector('.health-cell svg').getBoundingClientRect().left", 'health reading and graph sit side by side')
  await check("(() => { const reading = document.querySelector('.health-reading'); return reading.getBoundingClientRect().height <= 20 && reading.scrollWidth <= reading.clientWidth && ['fps', 'x', 'kbps', 'ms'].every(unit => reading.textContent.includes(unit)); })()", 'all four quality metrics fit in one concise line')
  await check("(() => { const style = getComputedStyle(document.querySelector('.health-cell svg')); return ['Top', 'Right', 'Bottom', 'Left'].every(side => style['border' + side + 'Style'] === 'solid' && style['border' + side + 'Width'] === '1px') && style.borderTopColor !== style.color; })()", 'graph has a subtle outline on all four sides')
  await clickSelector('button[aria-label="테스트 1 중지"]')
  await check("document.querySelector('button[aria-label=\"테스트 1 시작\"]').disabled === false", 'stop returns channel to idle')
  await clickSelector('button[aria-label="테스트 1 RTSP 주소 편집"]')
  await check("!document.querySelector('dialog') && document.querySelector('.endpoint-editor input') === document.activeElement", 'inline input receives focus without a modal')
  await fill('.endpoint-editor input', 'http://invalid/path')
  await click('적용')
  await check("!!document.querySelector('.endpoint-editor [role=alert]')", 'invalid URL shown next to input')
  await fill('.endpoint-editor input', 'rtsp://127.0.0.1:8554/edited')
  await new Promise(resolve => setTimeout(resolve, 400))
  await check("document.querySelector('.endpoint-editor input').value.endsWith('/edited')", 'SSE preserves URL draft')
  await click('적용')
  await check("document.querySelector('.url-cell code').textContent.endsWith('/edited') && !document.querySelector('dialog')", 'endpoint edit')
  await clickSelector('button[aria-label="테스트 1 RTSP 주소 편집"]')
  await check("document.querySelector('.endpoint-editor input') === document.activeElement", 'inline editor receives keyboard focus')
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 })
  await check("!document.querySelector('.endpoint-editor')", 'Escape cancels inline editing without saving')
  await check("['.workspace-heading', '.summary', '.command-bar'].every(selector => document.querySelector('.app-header').contains(document.querySelector(selector)))", 'controls and summary belong to white header')
  await clickSelector('button[aria-label="테스트 2 삭제"]')
  await click('확인')
  await check("document.querySelectorAll('tbody tr').length === 1 && !document.querySelector('dialog')", 'confirmed deletion')
  await waitFor(() => evaluate("!document.querySelector('button.primary').disabled"), 'upload enabled after deletion')
  await evaluate(`(() => { const data = new DataTransfer(); for (const [name, content] of [['good.mp4', 'good'], ['bad.mp4', 'bad'], ['oversized.mp4', 'oversized'], ['last.mp4', 'last']]) data.items.add(new File([content], name, { type: 'video/mp4' })); const input = document.querySelector('input[type=file]'); input.files = data.files; input.dispatchEvent(new Event('change', { bubbles: true })); })()`)
  await check("document.querySelector('.notification-list')?.textContent.includes('잘못된 영상')", 'per-file upload errors')
  await check("document.querySelectorAll('.notification-list li[data-kind=upload]').length === 4 && !document.querySelector('.upload-status')", 'all per-file results retained after sequential upload')
  await check("document.querySelector('.notification-list').textContent.includes('초과하여 전송하지 않았습니다')", 'oversized file rejected before transmission')
  await check("!document.querySelector('.notification-panel').matches(':popover-open') && !!document.querySelector('.notification-count')", 'results stay hidden with unread badge')
  await clickSelector('.notification-button')
  await check("document.querySelector('.notification-panel').matches(':popover-open') && !document.querySelector('.notification-count')", 'bell expands result history and clears unread count')
  await clickSelector('button[aria-label="작업 알림 닫기"]')
  assert.equal(requests.filter((r) => r.url === '/api/channels/upload').length, 3)
  await click('연결 확인')
  await check("document.querySelector('.notification-list .notification-error')?.textContent.includes('테스트 충돌 오류')", 'ProblemDetails retained in notifications')
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
  await clickSelector('.log-files-panel > summary')
  await check("document.querySelectorAll('.log-files-panel select option').length === 101", 'backend log list loads first hundred files')
  await click('다음 파일 목록')
  await check("document.querySelectorAll('.log-files-panel select option').length === 2", 'log list next page')
  await click('이전 파일 목록')
  await check("document.querySelectorAll('.log-files-panel select option').length === 101", 'log list previous page')
  await evaluate("(() => { const select = document.querySelector('.log-files-panel select'); select.value = 'ch1_20260320.log'; select.dispatchEvent(new Event('change', { bubbles: true })); })()")
  await check("document.querySelector('.file-output')?.textContent.includes('마지막 구간')", 'file selection reads latest content while file logging disabled')
  assert.equal(await evaluate('window.fileInjected'), undefined)
  await click('처음부터')
  await check("document.querySelector('.file-output')?.textContent.includes('처음 구간')", 'file detail starts at byte zero')
  await click('다음 구간')
  await check("document.querySelector('.file-output')?.textContent.includes('마지막 구간')", 'file detail follows next cursor')
  await fill('input[aria-label="현재 파일 구간 검색"]', 'no-match')
  await check("document.querySelector('.file-output').textContent.includes('검색 결과가 없습니다')", 'file content search is limited to current segment')
  await fill('input[aria-label="현재 파일 구간 검색"]', '')
  for (const width of [1280, 768, 390, 320]) {
    await command('Emulation.setDeviceMetricsOverride', { width, height: 844, deviceScaleFactor: 1, mobile: width < 720 })
    await command('Emulation.setTouchEmulationEnabled', { enabled: width < 720 })
    await check('document.documentElement.scrollWidth <= window.innerWidth', `${width}px layout with expanded settings and long content has no page overflow`)
    await check("document.querySelector('.table-scroll').scrollWidth > document.querySelector('.table-scroll').clientWidth", `${width}px table scrolls within its own region`)
    if (width >= 720) await check(compactRows, `${width}px long channel content retains compact rows`)
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
  logReadError = true
  await click('최신 내용')
  await check("document.querySelector('.log-files-panel [role=alert]')?.textContent.includes('로그 파일을 찾을 수 없습니다') && !document.querySelector('.file-output')", 'deleted file errors do not leave stale content visible')
  logReadError = false
  logFiles = []
  await click('파일 목록 새로고침')
  await check("document.querySelector('.log-files-panel').textContent.includes('저장된 .log 파일이 없습니다')", 'empty backend log folder is explained')
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
