import { readFileSync } from 'node:fs'
import { defineConfig } from 'vite'
import plugin from '@vitejs/plugin-vue'

const settings = JSON.parse(readFileSync(new URL('./appsettings.json', import.meta.url)))

export default defineConfig(() => {
  const target = settings.BackendUrl
  const proxy = {
    '/api': {
      target,
      // Preserve Host so same-origin browser requests pass Backend's origin policy.
      changeOrigin: false,
      // SSE stays open; buffering/time limits belong to the production proxy.
      timeout: 0,
      proxyTimeout: 0,
    },
  }
  return {
    plugins: [plugin()],
    server: { host: '127.0.0.1', port: 4848, strictPort: true, proxy },
    preview: { host: '127.0.0.1', port: 4848, strictPort: true, proxy },
  }
})
