export function problemMessage(problem, status) {
  const validation = Object.values(problem?.errors ?? {}).flat().join('\n')
  return validation || problem?.detail || problem?.title || `요청에 실패했습니다. (HTTP ${status})`
}

export async function request(path, { method = 'GET', body, signal, timeout = 30000 } = {}) {
  const headers = { Accept: 'application/json' }
  if (method !== 'GET') headers['X-RTSPCaster-Client'] = 'web'
  const multipart = body instanceof FormData
  if (body !== undefined && !multipart) headers['Content-Type'] = 'application/json'
  const timeoutSignal = AbortSignal.timeout(timeout)
  const payload = method === 'GET' || body === undefined ? {} : { body: multipart ? body : JSON.stringify(body) }
  try {
    const response = await fetch(`/api${path}`, {
      method,
      headers,
      ...payload,
      signal: signal ? AbortSignal.any([signal, timeoutSignal]) : timeoutSignal,
    })
    if (response.status === 204) return null
    const text = await response.text()
    let data
    try { data = text ? JSON.parse(text) : null } catch {
      throw new Error(`Backend 응답을 읽을 수 없습니다. API 프록시를 확인하세요. (HTTP ${response.status})`)
    }
    if (!response.ok) throw new Error(problemMessage(data, response.status))
    return data
  } catch (error) {
    if (error.name === 'TimeoutError') throw new Error('요청 시간이 초과되었습니다. 실제 처리 결과는 채널 상태를 확인하세요.', { cause: error })
    if (error.name === 'TypeError') throw new Error('Backend에 연결할 수 없습니다. 서버 실행 및 프록시 주소를 확인하세요.', { cause: error })
    throw error
  }
}

async function uploadFiles(files, signal, { onResult = () => {}, onProgress = () => {} } = {}) {
  if (!files.length) return []
  signal?.throwIfAborted()
  const limits = await request('/uploads/limits', { signal })
  if (!Number.isSafeInteger(limits?.maxUploadBytes) || limits.maxUploadBytes <= 0) {
    throw new Error('서버의 업로드 제한을 확인할 수 없습니다. Backend를 업데이트하고 다시 시작하세요.')
  }
  const results = []
  function report(result) {
    results.push(result)
    onResult(result)
  }
  for (const [index, file] of files.entries()) {
    signal?.throwIfAborted()
    onProgress({ index: index + 1, total: files.length, fileName: file.name })
    if (file.size === 0 || file.size > limits.maxUploadBytes) {
      report({ fileName: file.name, channel: null, error: file.size === 0 ? '빈 파일은 등록할 수 없습니다.'
        : `파일 크기가 서버 제한 ${limits.maxUploadBytes.toLocaleString('ko-KR')}바이트를 초과하여 전송하지 않았습니다.` })
      continue
    }
    // One request per file: the selected files' total must not hit the request limit.
    const body = new FormData()
    body.append('files', file)
    let response
    try {
      response = await request('/channels/upload', { method: 'POST', body, signal, timeout: 60 * 60 * 1000 })
      if (!Array.isArray(response) || response.length !== 1 || !response[0]
        || (!response[0].channel && !response[0].error)) {
        throw new Error('파일 등록 응답 형식이 올바르지 않습니다.')
      }
    } catch (error) {
      if (signal?.aborted || error.name === 'AbortError') throw error
      report({ fileName: file.name, channel: null, error: `등록 여부 확인 필요: ${error.message}` })
      // A reset can happen after registration. Never retry automatically or submit the remaining queue.
      throw new Error(`${file.name}: 업로드가 중단되었습니다. ${error.message}\n남은 파일은 전송하지 않았습니다. 채널 목록에서 등록 여부를 확인한 뒤 미등록 파일만 다시 선택하세요.`, { cause: error })
    }
    report(response[0])
  }
  return results
}

export const api = {
  status: (signal) => request('/status', { signal }),
  settings: (body) => request('/settings', { method: 'PUT', body }),
  check: () => request('/mediamtx/check', { method: 'POST' }),
  templateHelp: () => request('/rtsp-template/help'),
  template: (template) => request('/rtsp-template/apply', { method: 'POST', body: { template } }),
  startAll: () => request('/channels/start-all', { method: 'POST' }),
  stopAll: () => request('/channels/stop-all', { method: 'POST' }),
  start: (id) => request(`/channels/${id}/start`, { method: 'POST' }),
  stop: (id) => request(`/channels/${id}/stop`, { method: 'POST' }),
  remove: (id) => request(`/channels/${id}`, { method: 'DELETE' }),
  endpoint: (id, body) => request(`/channels/${id}/endpoint`, { method: 'PUT', body }),
  upload: uploadFiles,
}
