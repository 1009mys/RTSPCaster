export function parseEndpoint(value) {
  const match = /^rtsp:\/\/([a-z0-9.-]+)(?::([0-9]+))?\/([a-z0-9_/-]+)$/i.exec(value.trim())
  if (!match) throw new Error('rtsp://호스트:포트/경로 형식으로 입력하세요. 인증 정보·IPv6·쿼리·공백은 지원하지 않습니다.')
  const [, host, port, path] = match
  const mediaMtxPort = port === undefined ? 554 : Number(port)
  const rtspPath = path.replace(/^\/+|\/+$/g, '')
  if (host.length > 253 || !host.split('.').every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i.test(label))) {
    throw new Error('유효한 IPv4 주소 또는 호스트 이름을 입력하세요.')
  }
  if (!Number.isInteger(mediaMtxPort) || mediaMtxPort < 1 || mediaMtxPort > 65535) throw new Error('포트는 1~65535 범위여야 합니다.')
  if (!rtspPath || rtspPath.length > 512) throw new Error('RTSP 경로는 1~512자여야 합니다.')
  return { mediaMtxHost: host, mediaMtxPort, rtspPath }
}

export function toHlsUrl(value, pageProtocol = globalThis.location?.protocol) {
  const { mediaMtxHost, rtspPath } = parseEndpoint(value)
  const protocol = pageProtocol === 'https:' ? 'https:' : 'http:'
  return `${protocol}//${mediaMtxHost}:8888/${rtspPath}`
}
